# Tasks: netclaw-gui-phase-0-groundwork

## 1. Extract Netclaw.Client

- [x] 1.1 Create `src/Netclaw.Client/Netclaw.Client.csproj` (`net10.0`,
  references to `Netclaw.Actors`, `Netclaw.Configuration`, SignalR client
  and R3 packages, no versions in the csproj) and verify
  `dotnet build src/Netclaw.Client` succeeds empty.
- [x] 1.2 Move the seed types (`DaemonClient`, `DaemonApi`,
  `DaemonClientFactory`, `IDaemonHubTransport`,
  `HubConnectionBuilderExtensions`, `DaemonConnectionEvent`,
  `SessionCatalogEntryDto`) plus the compile-error closure from
  `src/Netclaw.Cli` into `Netclaw.Client`, renaming namespaces to
  `Netclaw.Client` per design Decision 2, and verify the library builds with
  zero warnings.
- [x] 1.3 Add the `Netclaw.Client` project reference to `Netclaw.Cli`,
  update `using` directives, confirm CLI-only types (`DaemonManager`,
  `ContainerSupervisor`, `SystemdUserService`, `PairCommand`,
  `DaemonCommandDispatch`, `DaemonPathEnvironmentFile`) did not move, and
  verify `dotnet build src/Netclaw.Cli -c Release` succeeds.
- [x] 1.4 Run `dotnet test src/Netclaw.Cli.Tests -c Release` and verify the
  `DaemonClient`/`DaemonApi` tests pass with no assertion or behavior edits
  (`using` updates only) — this is the behavior-neutrality proof from design
  Decision 4.

## 2. Scaffold Netclaw.Gui

- [x] 2.1 Add package versions to `Directory.Packages.props` (Avalonia,
  Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter,
  CommunityToolkit.Mvvm, Avalonia.AvaloniaEdit, AvaloniaEdit.TextMate,
  TextMateSharp.Grammars) and verify `dotnet restore` resolves with no
  version conflicts (NU1605/NU1608 clean).
- [x] 2.2 Create `src/Netclaw.Gui` (Avalonia app, Fluent theme, compiled
  bindings, MVVM skeleton with an empty `MainWindowViewModel`, project
  reference to `Netclaw.Client` only) and verify the project builds under
  the inherited `TreatWarningsAsErrors` with any suppression scoped
  per-diagnostic-id per design Decision 7.
- [x] 2.3 Add both projects to `Netclaw.slnx` under `/src/` and verify
  `dotnet build Netclaw.slnx -c Release` and
  `dotnet test Netclaw.slnx -c Release` pass — the same commands PR CI runs.

## 3. Walking skeleton

- [x] 3.1 Implement connection bootstrap: resolve the endpoint through
  `DaemonClientFactory`, connect on launch, bind `ConnectionEvents` to a
  status line, and verify the window shows a connected state against a
  running local daemon.
- [x] 3.2 Implement the chat slice: `EnsureSession(null, "tui")` on connect,
  a one-line input with a send button, an output area that appends
  `text_delta` payloads and replaces the buffer on the final `text`
  snapshot; verify by sending a message to the live daemon and reading the
  streamed reply, and confirm the session appears in `netclaw sessions`.
- [x] 3.3 Verify the failure modes from the design: launch with the daemon
  stopped (status shows disconnected, a sent message queues and flushes
  after `netclaw daemon start`), and restart the daemon mid-session (the
  skeleton reconnects without crashing).

## 4. Quality gates

- [x] 4.1 Run `dotnet slopwatch analyze` and verify no new violations.
- [x] 4.2 Run `./scripts/Add-FileHeaders.ps1 -Verify` and verify every new
  `.cs` file carries the copyright header.
- [x] 4.3 Run the full solution build and test in Release one final time and
  verify green, confirming no daemon project changed
  (`git diff --stat` shows no edits under `src/Netclaw.Daemon` or
  `src/Netclaw.Actors`).
