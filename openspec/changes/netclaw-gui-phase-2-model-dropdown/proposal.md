# Proposal: netclaw-gui-phase-2-model-dropdown

## Why

The GUI cannot change the model of a session. The operator must edit the
config and restart the daemon to try a different hosted model. GUI roadmap
Phase 2 (`docs/netclaw/plans/netclaw-gui-plan.md`) makes a mid-conversation
model switch the next deliverable after core chat.

## What Changes

- Add a per-session model override in the daemon. The override routes the
  session's main-role chat calls to a selected model. Compaction and
  fallback roles keep the configured models.
- The override lives in actor state only. It is not persisted. A daemon
  restart clears it and the session returns to the configured main model.
- Add a hub method to set and clear the override. The ack returns after the
  actor applies the override. A rejected model name returns a loud error.
- Re-resolve model capabilities on override through the existing
  `ModelCapabilityActor`, so modality gating follows the active model.
- Add a daemon endpoint that lists the models available for selection, with
  capability data. The endpoint composes the existing provider probe
  (`IProviderDescriptor.ProbeAsync`; Ollama `/api/tags`).
- GUI: add a model dropdown to the chat pane. The dropdown shows the
  available models, marks the active one, warns when a model lacks tool
  support, and calls the override hub method.
- Populate `ChatRoutingContext.SessionId` and add a router policy that
  honors the override. This fills the seam reserved by netclaw#648.

**Divergence from the roadmap sketch:** the plan text feeds the dropdown
from OllamaSharp `/api/tags` inside the GUI. This proposal feeds it from a
new daemon endpoint instead. Reasons: the daemon already owns provider
config and probe code, the endpoint stays provider-agnostic, and a remote
GUI cannot always reach the Ollama host directly.

## Capabilities

### New Capabilities

- `session-model-override`: per-session main-role model override — hub
  command, routing, capability re-resolve, reset semantics, and the output
  event that mirrors the override state to attached clients.
- `model-catalog`: operator-authenticated daemon endpoint that lists
  selectable models with capability data.

### Modified Capabilities

- `gui-core-chat`: add the model dropdown requirement — model list,
  active-model display, capability warnings, and the switch action.
- `netclaw-model-capabilities`: add tri-state tool-call support to
  capability resolution, so the catalog can warn without a guess.

## Impact

- **PRD linkage:** PRD-005 (`MP-002` provider abstraction, `MP-009`
  primary + fallback configuration) governs routing. PRD-005 defers
  per-request model selection ("Sub-Agent Model Routing, Post-MVP") — this
  change builds the per-session form of that seam. PRD-003 (`UX-002`
  session inspector) is the closest operator-UX anchor; no PRD requirement
  covers a session model switcher yet, so the specs in this change are the
  authority.
- **Code:** `ChatRoutingContext`, `IChatClientRouter`/`RoutingChatClient`,
  `IChatClientProvider`, `LlmSessionActor` (new command + state field +
  client re-resolve), `SessionHub`/`SessionRegistry`, a new
  `/api/models` route group, `Netclaw.Client`, `Netclaw.Gui`.
- **Security:** the endpoint and hub method are operator-authenticated like
  the rest of the daemon API. The override selects only models that resolve
  through configured providers — it cannot introduce a new provider or
  endpoint. Unknown model names fail loudly; no silent fallback.
- **Operations:** the override is visible in the session's output stream
  and clears on restart. The catalog endpoint reports probe failures per
  provider instead of an empty merged list, so a dead provider is
  diagnosable.
- **Out of scope for this change:** persistence of the override, per-role
  overrides (compaction/fallback), sub-agent model selection
  (`subagent-explicit-model-selection` covers that), provider management
  from the GUI, and model pull/download.
