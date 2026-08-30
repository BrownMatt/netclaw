# Design: improve-tool-calling (Phase 1 — loop quality)

## Context

See `proposal.md` — Why. The relevant seams already exist:

- `StreamingResponseReader` (`src/Netclaw.Actors/Sessions/Pipelines/`)
  consumes every streamed update, already switches on `TextReasoningContent`,
  and already receives a `CancellationToken`. It runs for every provider
  type, so it is the provider-agnostic seam the thinking cap needs.
- `TurnStateTracker` (`src/Netclaw.Actors/Sessions/Handlers/`) owns all
  existing nudges (thinking-only, truncated, empty, budget, duplicate) and
  their one-shot sent-flags per turn.
- `LlmResponseClassifier` owns `LlmResponseKind` (Text / ToolCalls /
  ThinkingOnly / Empty).
- The session actor (`LlmSessionActor`) wires all three per turn.

Constraints: actor boundaries stay transport-agnostic; persistence types
stay framework-owned; `TimeProvider` for any time reads; no
`Thread.Sleep`/`Task.Delay` in test orchestration; Configuration Schema
Sync Rule for every new `SessionConfig` property.

## Goals / Non-Goals

**Goals:**

- All three guards live in the existing turn-loop constructs. No new
  services, actors, or pipelines.
- The thinking cap counts and cancels inside `StreamingResponseReader`, so
  no chat-client (OllamaSharp or `OpenAiCompatibleChatClient`) needs edits.
- Every guard state resets with the turn, like existing nudge flags.

**Non-Goals:**

- No token-usage accounting changes; the cap counts thinking characters or
  update tokens as exposed by `TextReasoningContent`, not provider usage
  metadata (which arrives only at stream end).
- No persistence-schema change: guard state is per-turn and in-memory;
  a restart mid-turn already replays the turn from persisted lifecycle
  events, and guards re-arm cleanly.
- No changes to `McpToolResultFormatter` output format (consumers parse it).

## Decisions

1. **Thinking cap counts inside `StreamingResponseReader`.**
   The reader accumulates a thinking-size counter across
   `TextReasoningContent` updates. On breach it stops enumeration and
   returns a result flagged `ThinkingCapBreached`, keeping all content read
   so far. The session actor decides what to do (nudge + one re-invoke, or
   end the turn). Alternative considered: a delegating `IChatClient`
   wrapper. Rejected: a wrapper cannot see turn state (one-re-prompt-per-
   turn), and the reader already owns stream interpretation.
   Cancellation uses a linked CTS owned by the reader call, so the outer
   turn token is untouched.

2. **Cap unit is thinking characters, not tokens.**
   Token counts per update are not available mid-stream from either
   provider client. Characters are available, deterministic, and testable.
   The config name says characters (`ThinkingCapChars`) so the unit is
   explicit. Telemetry from the 2026-08-27 incident: 41,500 deltas of a few
   characters each — a default of 120,000 characters (~30k tokens) is far
   above any legitimate response seen and far below the incident's burn.

3. **Tool-error nudge lives in `TurnStateTracker`.**
   The tracker already receives tool results for duplicate detection and
   already appends nudges. Add a per-iteration flag: when any tool result
   in the iteration is an error and the duplicate nudge did not fire, emit
   one `ToolErrorNudge`. Alternative considered: nudge inside the tool
   pipeline next to `McpToolResultFormatter`. Rejected: the formatter is a
   pure formatter; the tracker owns nudge policy and the suppression state.

4. **Plan-without-action is a new `LlmResponseKind`.**
   `LlmResponseClassifier` gains `PlanWithoutAction`, gated so it can only
   replace what today classifies as `Text` (short, no tool calls). The
   heuristic (original implementation): reply under a length threshold,
   contains a first-person intent construction, no code fence, no question
   to the user. Restatement guard: normalized-text similarity against prior
   plan-only replies in the turn (reuse the duplicate guard's normalization
   approach). Re-prompt counter and guard state live in `TurnStateTracker`.

5. **Config: four new `SessionConfig` properties.**
   `ToolErrorNudgeEnabled` (bool, default true),
   `ThinkingCapEnabled` (bool, default true),
   `ThinkingCapChars` (int, default 120000),
   `PlanRepromptEnabled` (bool, default false).
   Schema updated in the same PR with `"default"` values so
   `netclaw doctor --fix` migrates existing configs.

## Risks / Trade-offs

- [Cap fires on a legitimately long reasoning chain] → Generous default,
  the breach keeps all thinking in the transcript, the nudge lets the model
  continue deliberately, and the switch disables the cap per deployment.
- [Character unit drifts from token intuition] → The config name carries
  the unit; the breach log records both characters and delta count so
  operators can re-size.
- [Plan-without-action false positives annoy users] → Ships disabled;
  enabling is a per-deployment decision after observing logs. The
  classifier logs would-have-fired events even when disabled (cheap
  observability, principle: measure before invest).
- [Nudge text grows context] → Nudges are one short sentence each, at most
  one per iteration / per turn per cause.
- [Interplay: cap re-prompt response is plan-only] → Guard order is
  deterministic: cap outcome is handled before classification; a turn ends
  after the cap's second breach regardless of other guards.

## Migration Plan

- Additive config with schema defaults; existing configs load unchanged
  (`doctor --fix` inserts defaults if a strict consumer needs them).
- Deploy: normal binary deploy; no data migration; hot-reload picks up the
  new settings like any `SessionConfig` change.
- Rollback: disable via the three switches, or roll back the binary; no
  persisted state depends on the guards.

## Open Questions

- Exact default for `ThinkingCapChars` may be re-sized after a week of
  breach logs; the value is config, so this does not change specs or tasks.
