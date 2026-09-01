# Tasks: netclaw-gui-phase-4-right-pane

## 1. Log tail endpoints (D1)

- [x] 1.1 Implement `LogTailReader`: shared-mode open
  (`FileShare.ReadWrite | Delete`), backward block scan, 2000-line cap,
  torn-first-line tolerance. Verify with tests: newest-lines window,
  cap enforcement, read while a writer holds the file open, empty file.
- [x] 1.2 Add `GET /api/logs/daemon?tail=N` and
  `GET /api/logs/session?sessionId={id}&tail=N`, operator-
  authenticated; daemon path = newest `daemon-*.log`, session path via
  `SessionLogFile.GetLogPath`; missing file → 404. Verify with endpoint
  tests: authenticated success, unauthenticated refusal, unknown
  session 404, cap applied.

## 2. Running models (D2)

- [x] 2.1 Add `IRunningModelsProbe`, implement it on `OllamaDescriptor`
  against `/api/ps` (name, provider key, size, expiry; absent fields
  stay absent). Verify with probe tests: mapping, unreachable backend
  throws.
- [x] 2.2 Compose the listing across providers (`is IRunningModelsProbe`
  check; non-implementing providers contribute nothing and are not
  failed; a throwing probe reports failed with a reason) and serve
  `GET /api/models/running`, operator-authenticated, uncached. Verify
  with tests: mixed providers, failure reporting, auth refusal.

## 3. Client (D4)

- [x] 3.1 Add `DaemonApi` methods and DTOs for the two log tails,
  running models, status, stats, and MCP statuses; expose them on the
  GUI session service. Verify with mapping tests.

## 4. GUI right pane (D3)

- [x] 4.1 Build `DiagnosticsPaneViewModel`: collapsed start, expand/
  collapse, view enum, one timer that ticks only while expanded and
  refreshes only the visible view, `DiagnosticsActions` delegate
  record, per-view error state that keeps last data. Verify with
  viewmodel tests: collapsed pane sends nothing, tick refreshes only
  the visible view, failed refresh keeps data and surfaces the reason,
  next tick retries.
- [x] 4.2 Logs view: daemon/session source toggle, session option
  disabled with no attached session and re-targeted on attach, tail
  window rendering with scrollback. Verify with viewmodel tests.
- [x] 4.3 Running models, Status, Stats, and MCP views with the visible
  refresh interval; active-model mark matched by provider key and model
  id from the attached session's effective model. Verify with viewmodel
  tests: active mark, MCP status list.
- [x] 4.4 Wire the pane into `MainWindow.axaml` (right-edge expand
  control, one view at a time) and `MainWindowViewModel`.

## 5. Verification and docs

- [x] 5.1 Run the gates: `dotnet build` (0 warnings), full test suite,
  `dotnet slopwatch analyze`, `./scripts/Add-FileHeaders.ps1 -Verify`.
- [x] 5.2 E2E smoke on the local install: expand the pane, read the
  daemon log tail and the attached session's log, see the running
  model with the active mark while a turn runs, check Status/Stats/MCP
  render, confirm a refresh survives a daemon restart. Record the
  result in the vault (`docs/netclaw/gui-phase-4.md`).
- [x] 5.3 Confirm no config schema change is needed
  (`ConfigSchemaDoctorCheck` passes unchanged).
