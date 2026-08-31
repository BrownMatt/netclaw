# Design: netclaw-gui-phase-2-model-dropdown

## Context

See `proposal.md` — Why. Current state that shapes the design:

- `RoutingChatClientProvider.GetClient(role)` caches one `RoutingChatClient`
  per `ModelRole` with an immutable `ChatRoutingContext`. `SessionId` on the
  context exists but nothing populates it
  (`src/Netclaw.Configuration/ChatRoutingContext.cs`).
- `RoleBasedFailoverRouter` builds all pipelines eagerly at construction
  and switches only on role
  (`src/Netclaw.Daemon/Configuration/ChatClientRouter.cs`).
- `LlmSessionActor` resolves `_chatClient` once in its constructor from
  `IChatClientProvider` and keeps `_clientProvider`
  (`src/Netclaw.Actors/Sessions/LlmSessionActor.cs:236`).
- `ModelCapabilityActor` is a registered singleton capability cache with no
  production caller
  (`src/Netclaw.Actors/Sessions/ModelCapabilityActor.cs`).
- `IProviderDescriptor.ProbeAsync` already lists models per provider
  (Ollama `/api/tags`); the TUI model manager uses it in-process. No daemon
  endpoint exposes it.
- `OllamaCapabilityResolver` already reads `/api/show` but drops the
  `tools` capability flag
  (`src/Netclaw.Providers/SelfHosted/OllamaCapabilityResolver.cs`).
- The folder-grant path (`SessionHub` → `SessionRegistry` →
  `SendFeedbackAndWaitAsync` → actor `Persist` → ack + output event) is the
  command template. The override differs in one point: it does not persist.

## Goals / Non-Goals

Goals:

- One override slot per session, main role only, actor-owned, cleared by
  restart or by an explicit clear command.
- Model selection limited to configured providers. Fail loudly on an
  unresolvable selection.
- Catalog and capability data served by the daemon, not fetched by the GUI
  from a model host directly.

Non-goals:

- No override persistence. No per-role or sub-agent overrides. No provider
  CRUD from the GUI. No model pull. No change to failover or compaction
  routing for non-overridden sessions.

## Decisions

### D1. The session actor owns the override; state is actor-local

`LlmSessionActor` holds the override in a plain instance field (a
`ModelReference?` plus the re-resolved `ModelCapabilities`). The command
handler applies it without `Persist`, so a restart clears it by
construction. Rationale: the roadmap fixes "not persisted; a restart
clears it", and a plain field is the only design where that invariant
cannot drift. Alternative — a persisted event with a recovery-time reset —
adds a journal record whose only purpose is to be ignored.

Data classification: override model and its capabilities are actor-local.
The catalog cache (D4) is call-local per request with a short in-process
cache. Nothing new is durable.

### D2. Routing goes through `ChatRoutingContext` with a lazy pipeline cache

`ChatRoutingContext` gains `OverrideModel: ModelReference?`. On override
set or clear, the actor re-resolves its client:
`_clientProvider.GetClient(context)` with
`{ Role = Main, SessionId, OverrideModel }`. A new router policy wraps
`RoleBasedFailoverRouter`: with no override it delegates; with an override
it returns a single candidate built lazily via
`PipelineChatClientFactory.Create(overrideRef)` and memoized per
`ModelReference`. Rationale: this fills the netclaw#648 seam as designed
(context in, policy decides), reuses the existing factory, and keeps the
single-candidate rule from the spec (no fallback chain for an override).
Alternative — the actor calls the factory directly — bypasses the router
seam and duplicates pipeline composition knowledge in the actor.

`IChatClientProvider` gains a context-taking overload. The existing
`GetClient(role)` remains for compaction and sub-agents and maps to a
context with no override.

### D3. Set-time validation uses the catalog service

`SessionRegistry` validates a `SetSessionModel` request before it sends
the actor command: the provider key must exist in the configured provider
dictionary, and the model id must appear in that provider's probe result
(served by the same catalog service as the endpoint, D4). Rejection is a
`HubException` with the model name. Rationale: the spec requires loud
rejection with unchanged routing; validation at first chat call would flip
the routing first and fail later. The wire request carries only
`(providerKey, modelId)` — no endpoint, no credential — which satisfies
the injected-provider-data requirement structurally.

### D4. One `ModelCatalogService` feeds both the endpoint and validation

A daemon singleton composes `ProviderConfigurationLoader` output with
`IProviderDescriptor.ProbeAsync` per configured provider (parallel, per-
provider timeout), then enriches entries with capability data through the
existing capability resolvers. Results are cached briefly (about 30
seconds, `TimeProvider`-based) so a dropdown open and a set-validation do
not double-probe. The endpoint is `GET /api/models`, operator-
authenticated, mapped like the skills route group. Response shape: one
entry per provider with `status`, `error?`, and `models[]`; each model
carries `id` and tri-state `toolSupport`. Rationale for daemon-side
catalog over GUI→OllamaSharp (the roadmap sketch): the daemon owns
provider config and probe code, the surface stays provider-agnostic, and
a remote GUI cannot always reach the model host. This is the proposal's
declared divergence.

### D5. Tool support becomes a tri-state field on `ResolvedModelCapabilities`

`ResolvedModelCapabilities` gains `SupportsToolCalls: bool?`. The Ollama
resolver parses the `/api/show` `capabilities` array (`tools` present →
true; array present without `tools` → false; no array → null). Other
resolvers leave it null. The composite resolver merges it with the same
fill-if-null semantics as the modality fields. Rationale: the GUI warning
must distinguish "no tools" from "unknown" (roadmap risk note), and the
resolver pipeline is the one place that already talks to `/api/show`.

### D6. Capability re-resolve goes through `ModelCapabilityActor`

On override apply, the session actor asks `ModelCapabilityActor`
(`GetModelCapabilities`) and swaps an `_activeModel` field that all
modality gating reads; the injected startup `ModelCapabilities` stays the
default. The actor's existing failure behavior (text-only after timeout,
logged) matches the spec's degrade-loudly scenario. Rationale: the cache
actor was built for exactly this and is currently dead code; a second
resolve path would violate reuse-before-add.

The ack to the operator does not wait for capability resolution; routing
applies immediately and gating tightens when the response arrives.
Trade-off accepted: a brief window where an image could pass gating
against a text-only override model — the model host then rejects it
loudly.

### D7. Wire surface mirrors folder grants

- Hub: `SetSessionModel(sessionId, providerKey, modelId)` /
  `ClearSessionModel(sessionId)`, ack via
  `SendFeedbackAndWaitAsync` + `CommandNack` → `HubException`.
- Output: `ModelOverrideOutput : SessionOutput` carrying the current
  override (or null when cleared), mapped in `SessionOutputDtoMapper`.
- Join snapshot: `SessionJoined` gains `ModelOverride` so an attach
  renders the active model without waiting for an event — same pattern as
  `GrantedFolders`.
- Client: `DaemonClient` methods + `DaemonApi.GetModelsAsync`; GUI
  dropdown in the chat pane header bound to `ChatSessionViewModel`.

## Risks / Trade-offs

- [Override survives in UI but not daemon after restart] → `SessionJoined`
  carries the authoritative override; the GUI renders only what the join
  snapshot and events report.
- [Probe latency on dropdown open] → parallel probes with per-provider
  timeout; failed providers report as failed entries instead of blocking
  the list; short cache keeps repeat opens cheap.
- [Pipeline cache growth in the override router] → memoization is keyed by
  a `(provider, model id)` tuple (`ModelReference` is a mutable class
  without value equality); the model set an operator can select is bounded
  by the catalog; entries are plain client pipelines with no connection
  state worth evicting for MVP.
- [Capability re-resolve races a fast first turn] → accepted (D6); the
  window is one resolve round-trip and fails loudly at the provider.
- [`RoutingChatClient` per-role context cache conflicts with per-session
  contexts] → the provider's role-keyed cache stays for no-override
  clients; context-taking resolution returns a client bound to that
  context, resolved per actor, not cached per role.

## Migration Plan

No persisted state, no config schema change, no wire-breaking change
(`SessionJoined` and the DTO gain optional fields; old clients ignore
them). Deploy is a normal daemon + GUI release. Rollback is a redeploy;
nothing to migrate back.

## Open Questions

None.
