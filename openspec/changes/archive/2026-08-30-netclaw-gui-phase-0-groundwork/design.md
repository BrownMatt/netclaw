# Design: netclaw-gui-phase-0-groundwork

## Context

See `proposal.md` — Why. Current state that shapes the approach:

- The daemon client seam lives inside the CLI executable at
  `src/Netclaw.Cli/Daemon/`. That folder mixes two concerns: the reusable
  client (`DaemonClient`, `DaemonApi`, `DaemonClientFactory`,
  `IDaemonHubTransport`, `HubConnectionBuilderExtensions`,
  `DaemonConnectionEvent`, `SessionCatalogEntryDto`) and CLI-only process
  management (`DaemonManager`, `ContainerSupervisor`, `SystemdUserService`,
  `PairCommand`, `DaemonCommandDispatch`, `DaemonPathEnvironmentFile`).
- `DaemonClient` exposes R3 observables (`Observable<SessionOutput>`), and
  `SessionOutput` plus its DTO mapper live in `Netclaw.Actors`.
- Endpoint and token resolution read `~/.netclaw/client/config.json` and
  `secrets.json` through types in `src/Netclaw.Cli/Config/`.
- `Directory.Build.props` applies `TreatWarningsAsErrors=true` and
  `Nullable=enable` to every project. `Directory.Packages.props` is central
  package management; project files carry no versions.
- CI (`pr_validation.yml`) builds and tests the whole `Netclaw.slnx`; a
  project added to the solution is gated automatically.

## Goals / Non-Goals

**Goals:**

- One shared client library that the CLI and the GUI both consume, with the
  CLI's observable behavior unchanged.
- An Avalonia project that compiles under the repo-wide warning policy.
- A walking skeleton that proves the full path: window → SignalR →
  session actor → streamed reply → window.

**Non-Goals:**

- No product UI (layout, panes, history rendering). Phase 1 owns that.
- No daemon change of any kind. If the skeleton appears to need one, stop
  and re-plan; it means the extraction cut the seam in the wrong place.
- No relocation of existing tests. Test moves are churn without proof value
  in this phase.
- No release packaging, no installer feed entry, no `gui` publish component.

## Decisions

### Decision 1 — Extraction boundary: the transport seam, not the folder

`Netclaw.Client` receives exactly the types a second front end needs to talk
to a running daemon: `DaemonClient`, `DaemonApi`, `DaemonClientFactory`,
`IDaemonHubTransport`, `HubConnectionBuilderExtensions`,
`DaemonConnectionEvent`, `SessionCatalogEntryDto`, and the response records
`DaemonApi` deserializes. CLI-only process management (`DaemonManager`,
`ContainerSupervisor`, `SystemdUserService`, `PairCommand`,
`DaemonCommandDispatch`, `DaemonPathEnvironmentFile`) stays in the CLI.

The exact closure (helper types in `src/Netclaw.Cli/Config/` that
`DaemonClientFactory` needs, such as the client config file and secrets
readers) is computed at implementation time by following compile errors from
this seed set. Types in the closure move; types outside it stay.

*Alternative rejected:* move the whole `Daemon/` folder. It drags process
supervision and the `pair` command into a library the GUI must not own, and
it couples the GUI to systemd/container concerns.

### Decision 2 — Namespaces follow the assembly

Moved types change namespace from `Netclaw.Cli.*` to `Netclaw.Client`. The
CLI updates its `using` directives. This is a mechanical, compiler-verified
rename.

*Alternative rejected:* keep old namespaces to shrink the diff. A
`Netclaw.Cli` namespace inside a non-CLI assembly misleads every future
reader, and the repo treats naming drift as debt.

### Decision 3 — Reference direction

`Netclaw.Client` references `Netclaw.Actors` (for `SessionOutput`, the DTO
mapper, `ApprovalOptionKeys`, `ChannelType`) and `Netclaw.Configuration`
(for `NetclawPaths`, status/stats response types), plus the SignalR client
and R3 packages. `Netclaw.Gui` references only `Netclaw.Client` (and gets
`Netclaw.Actors`/`Netclaw.Configuration` transitively). `Netclaw.Gui` must
not reference `Netclaw.Daemon`; nothing may reference `Netclaw.Cli`.

*Alternative rejected:* re-modeling the wire DTOs inside `Netclaw.Client` to
avoid the `Netclaw.Actors` reference. That duplicates the protocol union and
drifts from `SessionOutputDtoMapper` — the exact defect class the
constitution's reuse rule forbids.

### Decision 4 — Tests stay where they are

`Netclaw.Cli.Tests` keeps its `DaemonClient`/`DaemonApi` tests and passes
unchanged (only `using` updates). Unchanged tests are the proof that the
extraction is behavior-neutral. A dedicated `Netclaw.Client.Tests` project
is created only when Phase 1 adds client behavior.

### Decision 5 — GUI stack: Avalonia 12 + Fluent theme + CommunityToolkit.Mvvm

(Updated during apply, 2026-08-30: the design first named Avalonia 11 as the
stable line. NuGet now carries Avalonia 12.1.1 as latest stable, and the
AvaloniaEdit packages pair with it at 12.0.0, so the pin follows the design's
own rule — latest stable that restores cleanly.)

The GUI uses Avalonia 12 with the Fluent theme, compiled bindings, and
CommunityToolkit.Mvvm for viewmodels (source-generated observable
properties, no framework lock-in). R3 subscriptions from `DaemonClient`
marshal to the UI thread via the Avalonia dispatcher at the viewmodel
boundary; views never touch R3. New package versions enter
`Directory.Packages.props`: Avalonia, Avalonia.Desktop,
Avalonia.Themes.Fluent, Avalonia.Fonts.Inter, CommunityToolkit.Mvvm. The
AvaloniaEdit/TextMate packages from the plan enter now too, so version
conflicts surface in this phase, even though Phase 1 first uses them.

*Alternative rejected:* ReactiveUI. It layers a second reactive framework
over R3, which the client library already imposes.

### Decision 6 — Walking skeleton scope

One window: a status line, a read-only output area, a one-line input, a send
button. On launch it resolves the endpoint through
`DaemonClientFactory` (env var → client config → `netclaw.json` → default
`http://127.0.0.1:5199`), connects, and calls `EnsureSession(null, "tui")`.
Send calls `SendMessage`; the output area appends `text_delta` payloads and
replaces the buffer on the final `text` snapshot. No history, no styling, no
session list. The skeleton is the Phase 1 integration test bed, not a
product surface.

### Decision 7 — Warning policy stays intact

The GUI project inherits `TreatWarningsAsErrors`. If Avalonia XAML
generation emits warnings, suppress per-diagnostic-id in
`Netclaw.Gui.csproj` only, with a comment naming the generator defect. No
repo-wide `NoWarn`, no blanket suppression.

## Actor boundaries and persistence

Nothing changes inside the actor system. The GUI sits outside it, exactly
where the TUI sits: SignalR gateway → `SessionRegistry` →
`SignalRSessionActor` → `LlmSessionActor`. Session state stays in
Akka.Persistence (SQLite journal + snapshots, persistence id
`session-{id}`); the catalog stays in the SQLite `sessions` table. The GUI
holds only call-local UI state (connection status, input text, output
buffer) and persists nothing. `Netclaw.Client` remains transport-facing and
persistence-free.

## Failure modes and recovery

- **Daemon not running at launch.** `DaemonClient` retries with its existing
  backoff. The skeleton shows "disconnected — waiting for daemon" from
  `ConnectionEvents` and queues sent messages; the inherited queue-and-flush
  delivers them after connect.
- **Daemon restarts mid-session.** SignalR reconnects; `DaemonClient`
  re-attaches; the daemon rehydrates the session from persistence and
  replays `SessionJoined`. The skeleton only needs to not crash; rendering
  the replay is Phase 1.
- **401 from a non-loopback endpoint.** `DaemonClient` already maps this to
  a non-retryable error that names `netclaw pair`. The skeleton surfaces
  that message in the status line.
- **Extraction breaks the CLI.** The compiler and the unchanged
  `Netclaw.Cli.Tests` suite catch it in CI before merge. Recovery is fixing
  the reference, not shipping a shim.

## Migration Plan

No runtime migration. The change is additive plus a compiler-verified move
inside one release. Rollback is a single revert; no data, config, or wire
format changes. Suggested commit order: (1) extract `Netclaw.Client` + CLI
green, (2) scaffold `Netclaw.Gui` + solution/package wiring, (3) walking
skeleton. Each commit builds and tests green on its own.

## Open Questions

- Which exact Avalonia patch version to pin — resolved during apply:
  Avalonia 12.1.1 (via the `AvaloniaVersion` property), AvaloniaEdit 12.0.0,
  TextMateSharp.Grammars 2.0.4, CommunityToolkit.Mvvm 8.4.2.
