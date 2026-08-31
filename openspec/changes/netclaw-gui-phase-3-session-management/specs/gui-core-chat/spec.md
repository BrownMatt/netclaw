# gui-core-chat Delta

## ADDED Requirements

### Requirement: Managed session list

The GUI SHALL list sessions from the daemon session catalog in two
sections: pinned sessions on top, unpinned and unarchived sessions
below. Rows SHALL show the title (or a short id fallback), the relative
last-activity time, and an active indicator. A click SHALL attach the
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

## REMOVED Requirements

### Requirement: Read-only session list

**Reason**: Replaced by the managed session list — Phase 3 adds the
management actions this requirement explicitly excluded.
**Migration**: The click-to-attach and live-title scenarios carry over
into the "Managed session list" requirement unchanged.
