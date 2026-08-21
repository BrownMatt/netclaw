# Backlog Parking Lot

This file holds non-NOW work so autonomous loops do not accidentally bulldoze
deprioritized tasks. Move items into `IMPLEMENTATION_PLAN.md` only when the user
explicitly changes priority.

## NEXT Candidates

- Webhook service identity and inbound webhook hardening.
- Subagent explicit model selection.
- Subagent parent-context alignment.
- GitHub Copilot provider refinements.
- VLLM capability strategy and timing work.
- Fixed-length approval button labels and richer approval UI.
- Config hot-reload beyond startup-time configuration.
- Operator diagnostics refinements beyond current CLI/doctor/status work.
- Recurring reminders can disable themselves. `ReminderExecutionActor.BuildPrompt` tells every
  `Interval`/`Cron` reminder it may call `cancel_reminder`, and the `Personal` audience exposes
  that tool, so a weak model can disable a monitor reminder after a normal run (verified
  2026-08-18: `network-daily-pull` self-disabled; verdicts stopped for two days). Fix options:
  (1) suppress the auto-cancel guidance for `delivery_kind=none` monitor reminders, or make
  self-cancel opt-in per reminder; (2) add a per-reminder tool allowlist so a reminder can run
  with only `file_read` + `file_write`. Workaround already live on Nooch: `cancel_reminder:
  Approval` in the `Personal` audience profile fail-closes autonomous callers (Approval-mode
  tools deny for non-interactive reminder sessions).

## LATER Candidates

- Ambient channel monitoring workflows.
- Delegated coding task orchestration.
- Browser automation as a first-class product feature.
- Split gateway/agent process architecture.
- Hosted/multi-tenant operator console.
- Delivery-policy tuning beyond the first Telemetry & Alerting config pass.

## Parking Rule

If a future task is interesting but not necessary for the active milestone, add
it here instead of expanding `NOW`. The implementation plan should stay small
enough that an agent can finish the selected task all the way through runtime
verification.
