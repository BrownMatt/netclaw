# Tasks: netclaw-gui-phase-2-model-dropdown

## 1. Capability resolution — tool support (D5)

- [x] 1.1 Add `SupportsToolCalls: bool?` to `ResolvedModelCapabilities`,
  parse the `/api/show` `capabilities` array in `OllamaCapabilityResolver`
  (tools present → true; array without tools → false; no array → null),
  and verify with parser unit tests for all three states.
- [x] 1.2 Merge `SupportsToolCalls` in the composite resolver with
  fill-if-null semantics and verify with a merge unit test where a later
  resolver fills an earlier unknown.

## 2. Model catalog (D4)

- [x] 2.1 Implement `ModelCatalogService`: parallel `ProbeAsync` across
  configured providers with per-provider timeout, capability enrichment,
  per-provider failure entries, and a short `TimeProvider`-based cache.
  Verify with unit tests: one dead provider still yields the other's
  models plus a failed entry; cache prevents a double probe inside the
  window.
- [x] 2.2 Map `GET /api/models` (operator-authenticated) to the service
  and add `DaemonApi.GetModelsAsync`. Verify with endpoint tests:
  authenticated request returns provider entries; unauthenticated request
  is refused.

## 3. Routing (D2)

- [x] 3.1 Add `OverrideModel: ModelReference?` to `ChatRoutingContext`,
  add the context-taking `IChatClientProvider` overload, and keep the
  role-only path unchanged. Verify existing routing tests still pass.
- [x] 3.2 Implement the override-aware router policy: delegate with no
  override; single lazily-created memoized candidate with one. Verify with
  router unit tests: override routes to the override pipeline, compaction
  context still routes to configured compaction, no-override delegation is
  unchanged, memoization reuses a pipeline for a repeated reference.

## 4. Session actor override (D1, D6)

- [x] 4.1 Add `SetSessionModel` / `ClearSessionModel` commands and actor
  handlers: apply without `Persist`, re-resolve `_chatClient` through the
  context overload, ack after apply, emit `ModelOverrideOutput`. Verify
  with actor tests: set routes the next call to the override client, clear
  restores the configured client, restart (actor recreate) clears the
  override.
- [x] 4.2 Wire capability re-resolve through `ModelCapabilityActor` into a
  new `_activeModel` field and point modality gating at it. Verify with
  actor tests: gating follows the override capabilities; a resolver
  failure degrades to text-only and logs.
- [x] 4.3 Add `ModelOverride` to `SessionJoined` and map
  `ModelOverrideOutput` + the join field in `SessionOutputDtoMapper` both
  directions. Verify with mapper round-trip tests.

## 5. Gateway and client (D3, D7)

- [x] 5.1 Add `SetSessionModel` / `ClearSessionModel` hub methods with
  registry validation against the catalog service (provider key configured
  AND model id present in that provider's probe). Verify with registry
  tests: valid set acks; unknown provider and unknown model id both
  reject with a `HubException` naming the model and send no actor command.
- [x] 5.2 Add `DaemonClient` set/clear methods over the hub and verify
  with client mapping/command tests, including the `ModelOverrideOutput`
  and `SessionJoined.ModelOverride` mappings.

## 6. GUI (gui-core-chat delta)

- [x] 6.1 Add the model dropdown to the chat pane: catalog load on session
  attach, active-model marking from `SessionJoined`/`ModelOverrideOutput`,
  default entry that clears the override, warning badges for unsupported
  and unknown tool support, visible failure state for a failed catalog
  request. Verify with `ChatSessionViewModel`/GUI viewmodel tests for each
  state.
- [x] 6.2 Wire selection to the set/clear client methods with the ack
  driving the active marking (no optimistic flip). Verify with viewmodel
  tests: selection before ack does not mark active; nack restores the
  previous selection and surfaces the error.

## 7. Verification and docs

- [x] 7.1 Run the gates: `dotnet build` (0 warnings), full test suite,
  `dotnet slopwatch analyze`, `./scripts/Add-FileHeaders.ps1 -Verify`.
  Verify all pass with no new violations.
- [ ] 7.2 E2E smoke on the local install: open the dropdown, switch the
  session to a second hosted model mid-conversation, confirm the reply
  comes from the override model, clear, restart the daemon, confirm the
  override is gone. Record the result in the vault
  (`docs/netclaw/gui-phase-2.md`).
- [x] 7.3 Update the config schema only if a config property was added
  (none planned); confirm `ConfigSchemaDoctorCheck` still passes.
