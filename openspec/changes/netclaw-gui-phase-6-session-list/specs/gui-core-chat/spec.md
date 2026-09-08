# gui-core-chat Delta

## MODIFIED Requirements

### Requirement: Managed session list

The GUI SHALL list sessions from the daemon session catalog in two
sections: pinned sessions on top, unpinned and unarchived sessions
below. Rows SHALL show the title (or a short id fallback), the relative
creation time, the relative last-activity time, and an active indicator.
The absolute creation and last-activity times SHALL be available on the
row (for example in a tooltip). Exactly one row SHALL carry the active
indicator while a session is attached, across both sections, and a list
refresh SHALL keep the indicator on that row. The GUI SHALL offer a sort
order of last activity (the default), creation time, name, or type, and
SHALL apply the order inside each section. While connected, the GUI
SHALL refresh the list from the catalog on a fixed interval of at most
10 seconds, SHALL skip a refresh while one is in flight, and SHALL update
rows in place rather than rebuild the list. A click SHALL attach the
selected session and render its replayed recent history. `session_title`
events SHALL update the list live. A right-click context menu SHALL
offer Rename, Pin/Unpin, Archive, and Delete. Delete SHALL show a
confirmation dialog that names the session and states that the deletion
is permanent; the GUI SHALL send the delete only after the operator
confirms. The GUI SHALL apply list changes from daemon confirmations,
not optimistically.

#### Scenario: Click attaches a session

- **GIVEN** the list shows a session created by the TUI
- **WHEN** the operator clicks it
- **THEN** the GUI attaches to that session
- **AND** the middle pane shows its replayed recent messages

#### Scenario: Title updates live

- **GIVEN** an attached session with no title
- **WHEN** the daemon emits `session_title`
- **THEN** the list entry shows the new title without a manual refresh

#### Scenario: Pinned sessions render in the pinned section

- **GIVEN** a pinned session and an unpinned session
- **WHEN** the list renders
- **THEN** the pinned session appears in the pinned section on top
- **AND** the unpinned session appears in the section below

#### Scenario: Delete requires confirmation

- **GIVEN** the context menu is open for a session
- **WHEN** the operator chooses Delete
- **THEN** a dialog names the session and states the deletion is
  permanent
- **AND** the GUI sends no delete until the operator confirms
- **AND** a cancel leaves the session untouched

#### Scenario: Rejected operation keeps the list state

- **GIVEN** a management action that the daemon rejects
- **WHEN** the rejection arrives
- **THEN** the list keeps its previous state
- **AND** the GUI surfaces the daemon's reason

#### Scenario: Row shows both times

- **GIVEN** a session created three hours ago with activity two minutes
  ago
- **WHEN** the list renders
- **THEN** the row shows a relative creation time of about three hours
- **AND** the row shows a relative last-activity time of about two
  minutes
- **AND** the absolute times are available on the row

#### Scenario: Only the attached session is marked

- **GIVEN** a pinned session and an unpinned session
- **WHEN** the operator attaches the pinned session, then the unpinned
  session
- **THEN** after each attach exactly one row carries the active
  indicator
- **AND** the marked row is the attached session

#### Scenario: Refresh keeps the mark

- **GIVEN** an attached session with the active indicator
- **WHEN** a periodic refresh loads the catalog again
- **THEN** the same session still carries the indicator

#### Scenario: Sort by name orders each section

- **GIVEN** pinned sessions "Zeta" and "Alpha" and unpinned sessions
  "Mid" and "Beta"
- **WHEN** the operator selects the name sort
- **THEN** the pinned section reads "Alpha", "Zeta"
- **AND** the unpinned section reads "Beta", "Mid"

#### Scenario: Another client's session appears without an action

- **GIVEN** a connected GUI
- **WHEN** the TUI creates a session
- **THEN** the session appears in the list within the refresh interval
- **AND** the operator took no action in the GUI

#### Scenario: Refresh does not disturb an open dialog

- **GIVEN** the rename dialog is open for a session
- **WHEN** a periodic refresh loads the catalog
- **THEN** the dialog stays open for the same session
- **AND** the row keeps its identity in the list

## ADDED Requirements

### Requirement: New session from the GUI

The GUI SHALL offer a new-session action as a button in the session pane
and as the Ctrl+N shortcut. The action SHALL create a session through
the daemon, attach it, and show it in the list with the active
indicator. A failed creation SHALL keep the current attached session and
SHALL surface the daemon's reason.

#### Scenario: Button creates and attaches

- **GIVEN** a connected GUI attached to session A
- **WHEN** the operator presses the new-session button
- **THEN** the daemon creates session B
- **AND** the middle pane shows an empty history for session B
- **AND** the list marks session B as active

#### Scenario: Shortcut matches the button

- **GIVEN** a connected GUI with keyboard focus anywhere in the window
- **WHEN** the operator presses Ctrl+N
- **THEN** the GUI performs the same new-session action

#### Scenario: Failed creation keeps the current session

- **GIVEN** a GUI attached to session A
- **WHEN** the new-session call fails
- **THEN** session A stays attached with its history
- **AND** the GUI shows the failure reason

### Requirement: Attach loading state

The GUI SHALL show a loading state in the history area from the start
of an attach until the session replay arrives or the attach fails. An
empty replay SHALL end the loading state and SHALL show an empty
history, so a slow replay and an empty session are distinguishable.

#### Scenario: Loading shows until the replay arrives

- **GIVEN** the operator clicks a session in the list
- **WHEN** the attach starts
- **THEN** the history area shows a loading state
- **AND** the loading state ends when the replay renders

#### Scenario: Empty session ends the loading state

- **GIVEN** an attach of a session with no messages
- **WHEN** the replay arrives with no messages
- **THEN** the loading state ends
- **AND** the history area is empty

#### Scenario: Failed attach ends the loading state

- **GIVEN** an attach in progress
- **WHEN** the attach fails
- **THEN** the loading state ends
- **AND** the GUI shows the failure reason

### Requirement: Window title reflects the attached session

The window title SHALL show the attached session's title, or its id
when it has no title, followed by the application name. While the GUI is
not connected, the title SHALL also show the connection state. A live
`session_title` event for the attached session SHALL update the window
title.

#### Scenario: Attached session names the window

- **GIVEN** an attached session titled "Deploy checklist"
- **WHEN** the window renders
- **THEN** the window title starts with "Deploy checklist"
- **AND** the title ends with the application name

#### Scenario: Disconnected state shows in the title

- **GIVEN** an attached session
- **WHEN** the daemon connection drops
- **THEN** the window title shows the disconnected state
- **AND** the title returns to the session title on reconnect
