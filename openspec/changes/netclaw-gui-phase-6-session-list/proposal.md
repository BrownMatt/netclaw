# Proposal: netclaw-gui-phase-6-session-list

## Why

The GUI session list works, but it does not tell the operator where they
are or what is new. No row shows a date, no row marks the attached
session, the order is fixed, the GUI cannot start a session, and a
session that the TUI creates does not appear until the operator attaches
or runs an action. An attach with a slow replay looks the same as an
empty session. Round 2 of the GUI roadmap
(`docs/netclaw/plans/netclaw-gui-plan.md`, Phase 6) makes the session
list the first deliverable because every item is GUI-only and each one
removes a daily friction.

## What Changes

- Add a `+ New` button and the Ctrl+N shortcut. Both create a session
  through the hub and attach it.
- Show the creation time and the last-activity time under each row title
  as relative times. The absolute times appear in a tooltip.
- Mark the attached session in the list. Only one row carries the mark,
  across the pinned and unpinned sections, and a list reload keeps it.
- Add a sort control: last activity (default), created, name, and type.
  The order applies inside each section.
- Show a loading state in the history area from the start of an attach
  until the replay arrives or the attach fails.
- Refresh the list on a fixed interval while the GUI is connected, so a
  session that another client creates, renames, or deletes appears
  without an operator action. A refresh updates rows in place; it does
  not rebuild the list.
- Put the attached session title and the connection state in the window
  title.

No daemon change. No new endpoint. No wire change.

## Capabilities

### New Capabilities

None.

### Modified Capabilities

- `gui-core-chat`: the "Managed session list" requirement gains row
  dates, the active mark, sort, and the periodic refresh. Three
  requirements are added: "New session from the GUI", "Attach loading
  state", and "Window title reflects the attached session".

## Impact

- **PRD linkage:** no PRD requirement covers the GUI session list; the
  spec delta in this change is the authority (the Phase 3, 4, and 5
  posture).
- **In scope (MVP):** the seven items under "What Changes", their
  viewmodel tests, and a manual smoke pass on the installed GUI.
- **Out of scope:** a hub push for catalog changes (polling is enough at
  this scale), persisted sort and pane settings (Phase 8), session
  search (Phase 9), and any TUI change.
- **Code:** `src/Netclaw.Gui` only: `SessionListViewModel` and its row
  item, `MainWindowViewModel`, `MainWindow.axaml` and its code-behind,
  `IDaemonSessionService` (one new method that wraps the existing client
  `CreateSessionAsync`), and `Netclaw.Gui.Tests`.
- **Security:** none. The new-session call uses the same hub method and
  the same loopback or paired authentication as the TUI. The periodic
  refresh calls the existing operator-authenticated catalog endpoint.
- **Operations:** one `GET /api/sessions` request every 5 seconds per
  connected GUI. The request is skipped while a refresh is in flight and
  while the GUI is disconnected. The GUI operations skill does not
  change; no daemon behavior changes.
