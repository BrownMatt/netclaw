## Purpose

Define the desktop GUI chat surface: how it renders a streamed session,
how it exposes thinking and tool activity, how it collects approvals, and
how it reports activity and token usage to the operator.

## Requirements

### Requirement: Streamed reply rendering

The GUI SHALL append `text_delta` payloads to the active assistant turn as
they arrive. When the final `text` snapshot for that turn arrives, the GUI
SHALL replace the accumulated deltas with the snapshot content. The GUI
SHALL render assistant markdown with syntax-aware formatting.

#### Scenario: Delta stream renders live

- **GIVEN** an attached session with a turn in progress
- **WHEN** three `text_delta` events arrive
- **THEN** the history shows the concatenated delta text without waiting
  for the turn to end

#### Scenario: Final snapshot is authoritative

- **GIVEN** accumulated delta text that differs from the final `text` event
- **WHEN** the final `text` event arrives
- **THEN** the rendered turn equals the snapshot content
- **AND** no delta fragment remains duplicated

### Requirement: Collapsible thinking and tool sections

The GUI SHALL render thinking content and tool activity under separate
expandable headers. Each tool call and its result SHALL share one section.
All thinking and tool sections SHALL start collapsed. Expansion state SHALL
be a local UI concern and SHALL NOT alter session state.

#### Scenario: Tool call collapses by default

- **GIVEN** a turn that includes a `tool_call` and its `tool_result`
- **WHEN** the turn renders
- **THEN** the history shows a collapsed header that names the tool
- **AND** expansion reveals the call arguments and the result

#### Scenario: Thinking hidden until expanded

- **GIVEN** a turn that includes `thinking` content
- **WHEN** the turn renders
- **THEN** the thinking text is not visible
- **AND** expansion of the thinking header reveals it

### Requirement: Inline approval cards

The GUI SHALL render a `tool_interaction` event as an inline card in the
history with the interaction's options. The operator's choice SHALL be sent
through the existing interaction response path. The card SHALL show the
resolved outcome afterward and SHALL NOT accept a second response.

#### Scenario: Approval renders and resolves inline

- **GIVEN** a pending tool approval for the attached session
- **WHEN** the operator selects an option on the card
- **THEN** the response reaches the daemon interaction path
- **AND** the card shows the chosen outcome and disables its options

#### Scenario: Approval for a detached session is not answerable

- **GIVEN** the GUI detaches from a session with a pending approval
- **WHEN** the operator views another session
- **THEN** the GUI does not send any response for the detached session

### Requirement: Activity indicator

The GUI SHALL show one activity state for the attached session: Waiting
(message sent, no event yet), Responding (text deltas active), Running tool
(tool call active, named), Approval required (interaction pending), or idle
(turn complete). Event arrival SHALL drive every transition.

#### Scenario: Send enters Waiting

- **GIVEN** an idle attached session
- **WHEN** the operator sends a message
- **THEN** the indicator shows Waiting

#### Scenario: Tool call names the tool

- **GIVEN** a turn in progress
- **WHEN** a `tool_call` event arrives for `file_read`
- **THEN** the indicator shows the Running state with the tool name
- **AND** the matching `tool_result` returns the indicator to Responding
  or Waiting

#### Scenario: Turn completion returns to idle

- **GIVEN** any active state
- **WHEN** `turn_completed` arrives
- **THEN** the indicator shows idle

### Requirement: Usage line

The GUI SHALL render the latest `usage` event for the attached session as
input tokens, output tokens, and percent of the context window, in the form
`in=<n> out=<n> (<p>% ctx)`. When no usage event has arrived, the GUI SHALL
show no usage values rather than zeros.

#### Scenario: Usage updates after a turn

- **GIVEN** a completed turn whose `usage` event reports 1200 input tokens,
  300 output tokens, and a 262144-token context window
- **WHEN** the usage line renders
- **THEN** it shows `in=1200 out=300` and the percent of 262144 that the
  reported context consumption represents

### Requirement: Input queue-and-flush

The GUI SHALL accept input while disconnected, queue it in order, show the
queued count, and flush the queue in order after the session is re-attached.
A failed send SHALL return the message to the queue and SHALL NOT drop it
silently.

#### Scenario: Queued messages flush in order

- **GIVEN** two messages queued while the daemon is down
- **WHEN** the daemon starts and the GUI re-attaches
- **THEN** both messages reach the session in their original order

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

### Requirement: Model dropdown

The GUI SHALL show a model dropdown for the active session. The dropdown
SHALL list the models from the daemon model catalog, SHALL mark the
session's active model, and SHALL warn on entries whose tool-call support
is missing or unknown. A selection SHALL set the session's model override
through the daemon, and the dropdown SHALL offer a return to the
configured default model.

#### Scenario: Switch the active session mid-conversation

- **GIVEN** an attached session and a populated dropdown
- **WHEN** the operator selects a different model
- **THEN** the GUI sets the override for that session
- **AND** the dropdown marks the selected model as active after the daemon
  confirms the change

#### Scenario: Tool-support warning

- **GIVEN** a catalog entry whose tool-call support is unsupported or
  unknown
- **WHEN** the dropdown renders that entry
- **THEN** the entry carries a visible warning
- **AND** an unknown state is labeled unknown, not unsupported

#### Scenario: Catalog failure is visible

- **WHEN** the catalog request fails
- **THEN** the dropdown shows the failure state
- **AND** the GUI does not render an empty list as if no models exist

#### Scenario: Attach reflects the session's active model

- **GIVEN** a session with an override set by another client
- **WHEN** the operator attaches to that session
- **THEN** the dropdown marks the override model as active
