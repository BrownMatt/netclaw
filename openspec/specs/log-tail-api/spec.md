# log-tail-api Specification

## Purpose

Give operator clients read access to the daemon log and per-session
logs over the daemon API, so a GUI — local or remote-paired — can show
logs without filesystem access to the daemon host.

## Requirements

### Requirement: Daemon log tail endpoint

The system SHALL serve a tail window of the current daemon log over the
daemon API. The client SHALL be able to request a line count; the system
SHALL cap the returned window at 2000 lines and SHALL apply the cap when
the request asks for more. The response SHALL identify the log file the
window came from. The system SHALL read the log with shared file access,
because the daemon holds the file open for writes; the read SHALL NOT
block or break daemon logging.

#### Scenario: Tail returns the newest lines

- **GIVEN** a daemon log with more lines than the requested count
- **WHEN** an operator client requests a tail of N lines
- **THEN** the response contains the last N lines of the current daemon
  log in file order

#### Scenario: Oversized request is capped

- **WHEN** an operator client requests a tail larger than 2000 lines
- **THEN** the response contains at most 2000 lines

#### Scenario: Read coexists with the daemon's open write handle

- **GIVEN** the daemon writes to the current log file
- **WHEN** an operator client requests a tail
- **THEN** the read succeeds without an error
- **AND** daemon logging continues undisturbed

### Requirement: Session log tail endpoint

The system SHALL serve a tail window of a session's log over the daemon
API, addressed by session id. The same 2000-line cap and shared-read
contract as the daemon log SHALL apply. The system SHALL resolve the log
path from daemon-owned configuration and the sanitized session id; it
SHALL NOT accept a filesystem path from the client. The system SHALL
reject a session id that has no log with a not-found error.

#### Scenario: Session log tail by id

- **GIVEN** a session with a session log
- **WHEN** an operator client requests that session's log tail
- **THEN** the response contains the last requested lines of that
  session's log

#### Scenario: Unknown session is rejected

- **WHEN** a request names a session id with no session log
- **THEN** the system responds with a not-found error
- **AND** no file outside the session logs directory is read

### Requirement: Log access requires operator authentication

Log content can contain sensitive data. Both log endpoints SHALL require
the same operator authentication as the rest of the daemon API and SHALL
refuse an unauthenticated request with an authentication error.

#### Scenario: Unauthenticated log request is refused

- **WHEN** a request without operator credentials asks for a log tail
- **THEN** the daemon refuses the request with an authentication error
- **AND** no log content is returned
