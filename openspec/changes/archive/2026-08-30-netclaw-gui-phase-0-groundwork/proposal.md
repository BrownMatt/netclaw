# Proposal: netclaw-gui-phase-0-groundwork

## Why

Netclaw gets a desktop GUI as a second front end on the daemon. The GUI plan
(`RiderProjects/docs/netclaw/plans/netclaw-gui-plan.md`, 2026-08-30) defines
five phases. Phase 0 builds the foundation: a shared daemon-client library, a
new Avalonia project, and a walking skeleton that chats through the daemon.
Later phases cannot start until this foundation exists and CI accepts it.

## What Changes

- Extract the daemon client code out of the `Netclaw.Cli` executable into a
  new shared library, `src/Netclaw.Client`. The extraction moves
  `DaemonClient` (the SignalR reconnect/re-attach state machine), `DaemonApi`
  (the REST client), `IDaemonHubTransport`, `HubConnectionBuilderExtensions`,
  and the client DTOs. The CLI consumes the new library. Behavior does not
  change.
- Add a new Avalonia desktop project, `src/Netclaw.Gui` (MVVM, `net10.0`).
  The project references `Netclaw.Client` and does not reference
  `Netclaw.Daemon`.
- Add Avalonia package versions to `Directory.Packages.props`: Avalonia,
  Avalonia.AvaloniaEdit, AvaloniaEdit.TextMate, TextMateSharp.Grammars.
  OllamaSharp is already present.
- Add both projects to `Netclaw.slnx` under `/src/` so PR CI builds and
  tests them with the inherited `TreatWarningsAsErrors` setting.
- Deliver the walking skeleton: one window that connects to the daemon over
  loopback, creates or resumes a session with `channelType: "tui"`, sends a
  message, and renders the streamed reply as raw text.

Not a breaking change. No daemon code changes. No public API changes.

**Out of scope for this change** (later phases of the GUI plan): the
three-pane layout, session list, attachments, folder grants, approvals UI,
model dropdown, usage line, right pane, and release packaging of a `gui`
component.

## Capabilities

### New Capabilities

None. The walking skeleton is scaffolding, not agreed product behavior. The
GUI capabilities enter the spec base in Phase 1, where the real chat surface
lands.

### Modified Capabilities

None. The client-library extraction is a behavior-neutral refactor, and no
existing requirement moves. This change sets `skip_specs: true` in
`.openspec.yaml` (decision by the operator, 2026-08-30).

## Impact

- **Code**: new `src/Netclaw.Client` and `src/Netclaw.Gui` projects;
  `Netclaw.Cli` loses the moved files and gains a project reference;
  `Netclaw.slnx` and `Directory.Packages.props` gain entries.
- **Tests**: existing `Netclaw.Cli.Tests` coverage of `DaemonClient` and
  `DaemonApi` must pass unchanged after the move (the test project follows
  the types or references the new library). No new test surface is required
  beyond compile-and-pass, because behavior does not change.
- **PRD linkage**: PRD-001 (MVP) is the base. PRD-003 (Operator UX - Ops
  Console) defines a deferred Phase-5 web console; the desktop GUI is a
  distinct chat-first surface and does not implement PRD-003. The GUI plan
  document is the product source for this work. A PRD update for the GUI
  belongs with Phase 1, where product behavior ships.
- **Security**: none. The GUI is a loopback client and uses the existing
  `LoopbackAuthenticationHandler` operator path. No ACL, policy, or gateway
  change. No new network exposure.
- **Operations**: none at runtime. CI time grows by two projects. The GUI
  binary is not released, not installed, and not part of the update feed in
  this phase.
- **Dependencies**: Avalonia UI packages enter central package management.
  They are MIT-licensed and compile cross-platform; CI stays on the existing
  build agents.
