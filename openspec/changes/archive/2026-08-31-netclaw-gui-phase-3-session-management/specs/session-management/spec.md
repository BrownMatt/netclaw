# session-management Delta

## Purpose

Let the operator manage the session list: rename a session, pin it,
archive it, and delete it permanently. The daemon owns the operations;
clients only ask.

## ADDED Requirements

### Requirement: Manual rename with a title lock

The system SHALL let an operator set a session title. A manual title
SHALL persist with the session and SHALL carry a locked flag. While the
locked flag is set, the automatic title generator SHALL NOT change the
title. The system SHALL reject a rename to an empty or whitespace-only
title. The system SHALL acknowledge a rename only after the title event
persists, and SHALL emit the title change to every attached client.

#### Scenario: Manual title survives the generator

- **GIVEN** a session with a manual title
- **WHEN** the automatic title generator produces a new title for that
  session
- **THEN** the session keeps the manual title

#### Scenario: Manual title survives a restart

- **GIVEN** a session with a manual title
- **WHEN** the daemon restarts and the session recovers
- **THEN** the session still has the manual title
- **AND** the title stays locked against the generator

#### Scenario: Empty rename is rejected

- **WHEN** an operator renames a session to an empty string
- **THEN** the command is rejected with a reason
- **AND** the session title does not change

### Requirement: Pin and archive as catalog state

The system SHALL store a pinned flag and an archived flag per session in
the session catalog. The list surface SHALL support filters for the two
flags. The default list SHALL exclude archived sessions. Pin and archive
SHALL be reversible. Pin and archive SHALL NOT change session actor
state, routing, or persistence; an archived session SHALL stay
attachable.

#### Scenario: Archived session leaves the default list

- **GIVEN** a session in the default list
- **WHEN** an operator archives it
- **THEN** the default list no longer contains the session
- **AND** a list request with the archived filter still returns it

#### Scenario: Unarchive restores the session to the default list

- **GIVEN** an archived session
- **WHEN** an operator unarchives it
- **THEN** the default list contains the session again

#### Scenario: Pin state round-trips through the list

- **WHEN** an operator pins a session
- **THEN** the list marks the session as pinned
- **AND** an unpin removes the mark

#### Scenario: Unknown session is rejected

- **WHEN** a pin, archive, rename, or delete names a session id the
  catalog does not know
- **THEN** the operation is rejected with a reason
- **AND** no store changes

### Requirement: Delete with multi-store teardown

The system SHALL delete a session permanently on operator request. The
teardown SHALL stop the session actor before any store is touched, and
SHALL then remove the session's journal rows, snapshot rows, catalog
row, session directory, staged attachment files, and session log. Every
store the teardown touches SHALL be keyed by the session's persistence
identity derived on the daemon side; the system SHALL NOT build a
filesystem path from client-supplied text. The system SHALL log each
teardown step. When a step fails, the system SHALL report the failure
with the completed and remaining steps; it SHALL NOT report success.

#### Scenario: Delete removes every store

- **GIVEN** a session with journal rows, a snapshot, a catalog row, a
  session directory, and a session log
- **WHEN** an operator deletes the session
- **THEN** the session actor stops
- **AND** the journal rows, snapshot rows, catalog row, session
  directory, and session log are gone
- **AND** the session no longer appears in any list filter

#### Scenario: Deleted session does not recover on restart

- **GIVEN** a deleted session
- **WHEN** the daemon restarts
- **THEN** the session does not reappear in the catalog
- **AND** no actor recovers for it

#### Scenario: Delete of an attached session detaches the client first

- **GIVEN** a client attached to a session
- **WHEN** an operator deletes that session
- **THEN** the attached client is detached before the stores are removed
- **AND** the client receives a signal that the session is gone

#### Scenario: Partial teardown failure is loud

- **GIVEN** a teardown step that fails (for example a locked file)
- **WHEN** the delete runs
- **THEN** the operation reports the failure and the incomplete steps
- **AND** the daemon log records which steps completed

### Requirement: Management operations require operator authentication

Every management operation — rename, pin, archive, and delete — SHALL
require the same operator authentication as the rest of the daemon API.
The system SHALL refuse an unauthenticated request with an
authentication error.

#### Scenario: Unauthenticated delete is refused

- **WHEN** a request without operator credentials asks to delete a
  session
- **THEN** the daemon refuses the request with an authentication error
- **AND** no store changes
