# Tasks: improve-tool-calling (Phase 1 — loop quality)

## 1. Configuration

- [x] 1.1 Add `ToolErrorNudgeEnabled` (true), `ThinkingCapEnabled` (true),
      `ThinkingCapChars` (120000), `PlanRepromptEnabled` (false) to
      `SessionConfig`; verify existing config load tests pass with defaults.
- [x] 1.2 Update `netclaw-config.v1.schema.json` in the same commit with
      `"default"` values; verify `ConfigSchemaDoctorCheck` accepts a config
      with and without the new properties (round-trip test from the old
      shape per the Automation Floor).

## 2. Tool-error follow-up nudge

- [x] 2.1 Add tool-error detection and `ToolErrorNudge` emission to
      `TurnStateTracker` with per-iteration one-shot and duplicate-guard
      suppression; verify with unit tests: one error → one nudge, many
      errors in one iteration → one nudge, duplicate-guard fired → no
      nudge, switch off → no nudge.
- [x] 2.2 Wire the nudge into the session turn loop where existing tracker
      nudges are appended; verify with an actor test that the nudge message
      reaches the next model invocation and contains no tool arguments.

## 3. Per-response thinking cap

- [x] 3.1 Add thinking-character accounting and cap breach to
      `StreamingResponseReader` (linked CTS, keep accumulated content, flag
      `ThinkingCapBreached`); verify with fake-stream tests: breach at
      exactly the cap, content preserved, no breach when disabled, cap
      applies to `TextReasoningContent` only.
- [x] 3.2 Handle the breach in the session actor: append act-or-report
      nudge, re-invoke at most once per turn, end the turn through the
      existing completion path on a second breach with an operator-visible
      message naming the cap; verify with actor tests using a fake chat
      client that streams unbounded thinking (no Task.Delay in
      orchestration).
- [x] 3.3 Add the breach log record (model id, character count, delta
      count); verify the log line appears once per breach in the actor
      test's log capture.

## 4. Plan-without-action re-prompt

- [x] 4.1 Add `PlanWithoutAction` to `LlmResponseKind` and the original
      heuristic to `LlmResponseClassifier` (only refines `Text`; length
      threshold, intent construction, no code fence, no user-directed
      question); verify with classifier unit tests over positive and
      negative fixtures, including fixtures that must stay `Text`.
- [x] 4.2 Add re-prompt counter (max 3 per turn) and restatement guard to
      `TurnStateTracker`; verify unit tests: bounded budget, restatement
      stops early, state resets between turns.
- [x] 4.3 Wire into the session turn loop behind `PlanRepromptEnabled`
      (default off) and log would-have-fired events when disabled; verify
      actor tests for enabled re-prompt, disabled pass-through, and the
      disabled-mode log event.

## 5. Verification and rollout

- [x] 5.1 Add eval cases (Loop Quality category): a tool error does not
      produce an identical retry, and a plan-only turn recovers when
      enabled (fixture config enables PlanRepromptEnabled). The
      thinking-cap recovery eval is covered by the deterministic actor
      test instead (ThinkingCapActorTests, modeled on session
      `signalr_f25ac78233a84eda93f745edcf2a3868`) — a real model cannot
      be forced into a 120k-char runaway on demand. Verified 2026-08-30
      against `ollama/qwen3.6-35b-256k` (Docker eval run on the Windows
      host): Loop Quality 2/2 GREEN, `loop_tool_error_recovery` 5/5,
      `loop_plan_reprompt_recovers` 5/5 (run
      `evals/runs/3f24af0384f5caf9b0c0642859ceb48c`). The plan-reprompt
      assertion accepts both healthy outcomes (re-prompt fired, or the
      model went beyond the bare plan sentence on its own) and fails
      only when a bare plan-only reply is delivered without recovery —
      a real model obeys the "reply with exactly this sentence" setup
      only part of the time, and the guard logic itself has
      deterministic coverage in PlanRepromptActorTests. The re-prompt
      path fired live in 2 of 10 case runs and recovered both times.
- [x] 5.2 Run `dotnet slopwatch analyze` and
      `./scripts/Add-FileHeaders.ps1 -Verify`; verify no new violations and
      all headers present.
- [x] 5.3 Document the four config keys in CLI help / operational docs;
      verify `netclaw config` help output names them and `netclaw doctor`
      passes on a migrated config.
