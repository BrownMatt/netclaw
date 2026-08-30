# Proposal: improve-tool-calling (Phase 1 — loop quality)

Source brief: `docs/netclaw/plans/improve-tool-calling.md` (RiderProjects docs
vault), Phase 1. Source PRDs: PRD-001 (MVP agent loop), PRD-006 (MCP tool
integration).

## Why

A production session on 2026-08-27 (`signalr_f25ac78233a84eda93f745edcf2a3868`)
hit five consecutive MCP tool errors, then streamed 41,500+ thinking deltas in
one response for 17 minutes with no text and no tool call. Netclaw has no
guard for either behavior: nothing shapes the model's reaction to a tool
error, and nothing bounds a single response that thinks forever. Both gaps
waste context, block the session, and hide the failure from the operator.

## What Changes

- **Tool-error follow-up nudge.** When a tool result is an error, the session
  appends one short system nudge: try a different approach or report the
  failure; do not retry identical arguments. The nudge is suppressed when the
  duplicate-call guard already fired for that tool. Ships enabled with a
  config kill switch.
- **Per-response thinking cap.** The session enforces a configurable cap on
  thinking tokens within a single streamed response. On breach the session
  cancels the stream, keeps the accumulated thinking for the transcript
  (never delete model output), and re-prompts once with a nudge to act or
  report. Ships enabled with a generous default and a config kill switch.
- **Plan-without-action re-prompt.** A new response classification detects a
  reply that only states intent: short, names no final answer, calls no tool.
  The session re-prompts up to 3 times with repeat/restatement guards.
  Ships **disabled** by default; enable per deployment after observation.
  The intent heuristic is a fresh implementation; no Unsloth (AGPL) code,
  regexes, or comments are copied.
- No breaking changes. All three behaviors are additive turn-loop guards.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `turn-loop-governance` — three new requirements: tool-error follow-up
  nudge, per-response thinking cap, plan-without-action re-prompt. This spec
  already owns per-turn iteration limits and the force-no-tools completion
  path; these guards extend the same turn-loop authority.

## Impact

- **Code**: `TurnStateTracker` (nudge state, repeat guards),
  `LlmResponseClassifier` (new plan-without-action kind), the session's LLM
  invocation path (mid-stream thinking-token accounting and cancel),
  tool-result handling next to `McpToolResultFormatter` (error nudge).
  The thinking cap must sit at a provider-agnostic seam so it covers both
  the OllamaSharp client and `OpenAiCompatibleChatClient`.
- **Config**: new `SessionConfig` settings (cap value, three enable
  switches). `netclaw-config.v1.schema.json` updates in the same PR with
  `"default"` values (Configuration Schema Sync Rule).
- **Evals**: eval suite runs (SessionConfig defaults change). New cases:
  runaway thinking recovers at the cap (fixture: the 2026-08-27 session),
  a tool error does not produce an identical retry, a plan-only turn
  recovers when the re-prompt is enabled.
- **Security**: no ACL, gateway, or tool-authority changes. Nudges are
  system-authored text; they must not echo tool arguments or results beyond
  what the transcript already contains. Fail-open is not possible: a
  disabled guard means current behavior, never a bypass of an ACL check.
- **Operations**: new config keys documented in CLI help / runbooks. The
  thinking cap emits one log line per breach so operators can size the cap
  from telemetry. No system-skill update expected unless operator-visible
  workflow changes emerge in design (then `netclaw-operations` per the
  skill-sync table).

## Out of Scope (MVP boundary for this change)

- Text tool-call promotion, hold-and-release stream buffering, and
  multi-format parsing (plan Phases 2-4; separate change, gated on shared
  telemetry).
- Any change to Ollama/OllamaSharp request mapping or provider selection.
- Per-model tuning of the intent heuristic beyond the enable switch.
