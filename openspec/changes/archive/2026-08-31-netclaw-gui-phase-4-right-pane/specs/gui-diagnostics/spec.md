# gui-diagnostics Delta

## Purpose

Give the operator a diagnostics surface inside the GUI: logs, running
models, daemon status, stats, and MCP state, without terminal access to
the daemon host.

## ADDED Requirements

### Requirement: Collapsed right pane with one view at a time

The GUI SHALL provide a right pane that starts collapsed. A control on
the right edge SHALL expand and collapse it. The pane SHALL offer five
views — Logs, Running models, Status, Stats, and MCP — and SHALL show
one view at a time. The collapsed pane SHALL cost no daemon requests.

#### Scenario: Pane starts collapsed and expands on demand

- **WHEN** the GUI starts
- **THEN** the right pane is collapsed
- **AND** the expand control opens it to the last selected view

#### Scenario: Collapsed pane is idle

- **GIVEN** the right pane is collapsed
- **WHEN** the refresh interval elapses
- **THEN** the GUI sends no diagnostics requests

### Requirement: Log view over the tail endpoints

The Logs view SHALL let the operator pick the daemon log or the attached
session's log, and SHALL render a tail window of at most 2000 lines with
scrollback inside the window. The view SHALL refresh automatically while
visible. The session log option SHALL be unavailable when no session is
attached.

#### Scenario: Daemon log renders and refreshes

- **GIVEN** the Logs view shows the daemon log
- **WHEN** the refresh interval elapses
- **THEN** the view shows the newest tail window without operator action

#### Scenario: Session log follows the attached session

- **GIVEN** a session is attached and its log is selected
- **WHEN** the operator attaches a different session
- **THEN** the view shows the newly attached session's log

### Requirement: Diagnostics views render daemon data with auto refresh

The Running models, Status, Stats, and MCP views SHALL render the
daemon's running-models, status, stats, and MCP endpoints. Each view
SHALL refresh automatically while visible and SHALL show the refresh
interval. The Running models view SHALL mark the model the attached
session currently uses when that model is loaded.

#### Scenario: Running models marks the active model

- **GIVEN** the attached session runs on a loaded model
- **WHEN** the Running models view renders
- **THEN** the entry for that model carries an active mark

#### Scenario: MCP view lists server statuses

- **WHEN** the MCP view renders
- **THEN** it lists each configured MCP server with its status from the
  daemon

### Requirement: Refresh failure keeps the last data

When a refresh request fails, the view SHALL keep the last successfully
loaded data, SHALL surface the failure with a reason, and SHALL retry on
the next interval. The GUI SHALL NOT clear a view because one refresh
failed.

#### Scenario: One failed refresh does not blank the view

- **GIVEN** a view with loaded data
- **WHEN** the next refresh fails
- **THEN** the view still shows the loaded data
- **AND** the failure and its reason are visible
- **AND** the next interval retries
