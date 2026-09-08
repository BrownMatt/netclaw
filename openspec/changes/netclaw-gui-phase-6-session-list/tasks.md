# Tasks: netclaw-gui-phase-6-session-list

## 1. Row times and sort (D2, D4)

- [x] 1.1 Add `RelativeTime.Format(now, unixMs)` and the row properties
  `CreatedAt`, `CreatedDisplay`, `LastActivityDisplay`, `TimesTooltip`;
  `SessionListViewModel` takes a `TimeProvider`. Verify with tests over a
  fake `TimeProvider`: minutes, hours, days, past-30-days date, and the
  display refresh after a load.
- [x] 1.2 Add `SessionSortOrder` and the observable `SortOrder`; apply the
  comparer inside each section with in-place moves. Verify with tests:
  the name sort scenario, a date sort descending, the type sort, and row
  identity kept across a sort change.

## 2. Reconciling load and the active mark (D3, D5)

- [x] 2.1 Replace the rebuild in `Load` with a reconcile keyed by session
  id: update, add, remove, section move, re-sort. Verify with tests: an
  open rename target survives a load, a removed session leaves, a
  pin change moves the row, and a title update lands on the same object.
- [x] 2.2 Add `SetActive(sessionId)` and `IsActive` on the row; `Load`
  re-applies the mark. Verify with tests: exactly one row marked after
  two attaches across sections, and the mark survives a load.

## 3. Shell: new session, refresh, loading, title (D1, D6, D7, D8)

- [x] 3.1 Add `CreateSessionAsync` to `IDaemonSessionService` and the
  `NewSessionCommand` on `MainWindowViewModel` (adopt, ensure, flush,
  refresh, mark active; failure keeps `Chat` and sets `Status`). Verify
  with shell tests over the fake service: success adopts the new id and
  marks it, failure keeps the current session.
- [x] 3.2 Add `OnSessionListTimerTick` with the connected and in-flight
  guards. Verify with tests: a tick while disconnected sends nothing, a
  tick during an in-flight refresh sends nothing, a tick while connected
  lists once.
- [x] 3.3 Add `IsLoadingSession`: on in `AdoptSession`, off on
  `SessionJoined`, attach failure, session deletion, and connection loss.
  Verify with tests for each transition, including an empty replay.
- [x] 3.4 Add `WindowTitle` driven by the attached row title, the
  application name, and the connection state. Verify with tests: titled
  session, untitled session, disconnected suffix, live title update.

## 4. View (MainWindow.axaml)

- [x] 4.1 Add the `+ New` button and the sort `ComboBox` to the pane
  header, the Ctrl+N `KeyBinding` on the window, the two time lines and
  tooltip on the row template, the `IsActive` style class, the
  selection clear after an attach click, the loading overlay on the
  history area, the `Title` binding, and the timer call. Verify with a
  Release build with zero warnings and the GUI test project green.

## 5. Gates and smoke

- [x] 5.1 Run `dotnet build` (0 warnings), the full test suite (only the
  known baseline failures), `dotnet slopwatch analyze` (0 new), and
  `./scripts/Add-FileHeaders.ps1 -Verify`. Verify each command exits 0
  or matches the baseline.
- [x] 5.2 Deploy with `scripts/deploy-local.ps1 -Component gui` and run a
  manual smoke pass with the avalonia-drive scripts: new session via
  button and Ctrl+N, both times visible with the tooltip, one active
  mark after two attaches, name sort, a TUI-created session appears
  within 5 s, the loading state on attach, and the window title.
  Capture screenshots as evidence.

## 6. Docs

- [x] 6.1 Add `docs/netclaw/gui-phase-6.md` (feature, decisions, smoke
  result, known gaps), link it from `docs/README.md`, and mark Phase 6 as
  shipped in `docs/netclaw/plans/netclaw-gui-plan.md`. Verify the three
  files reference each other.
