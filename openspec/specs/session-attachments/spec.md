## Purpose

Let an operator client attach a file to a session message. The daemon
stores the upload, embeds the content in the prompt structurally once, and
grants no filesystem authority from the attachment.

## Requirements

### Requirement: Attachment upload endpoint

The daemon SHALL accept a file upload bound to an existing session through
the daemon API, under the same exposure and authentication rules as the
rest of the daemon control surface. The daemon SHALL classify the file
through the shared media catalog and SHALL enforce a configured size limit.
The daemon SHALL reject an upload for an unknown session, an oversize file,
or a file whose classification the model input path cannot accept, with a
bounded error.

#### Scenario: Operator upload succeeds

- **GIVEN** a healthy daemon and an existing session
- **WHEN** an operator-authenticated client uploads a text file within the
  size limit
- **THEN** the daemon stores the attachment bound to that session
- **AND** returns an attachment reference to the client

#### Scenario: Unknown session is rejected

- **GIVEN** a session id that does not exist in the catalog
- **WHEN** a client uploads a file for that id
- **THEN** the daemon rejects the upload
- **AND** stores nothing

#### Scenario: Oversize upload is rejected before storage

- **GIVEN** a file larger than the configured limit
- **WHEN** a client uploads it
- **THEN** the daemon rejects the upload with a bounded error
- **AND** no partial attachment remains

### Requirement: Embed once at send time

The daemon SHALL embed a pending attachment's content structurally in the
next message the client sends for that session, exactly once. Later turns
SHALL NOT re-embed the content. If the embed step fails, the daemon SHALL
fail the send with an error and SHALL NOT send the message without its
attachment.

#### Scenario: Attachment rides the next message only

- **GIVEN** a stored attachment pending for a session
- **WHEN** the client sends the next message
- **THEN** the prompt contains the attachment content in its structural
  form once
- **AND** the following message contains no repeated attachment content

#### Scenario: Embed failure does not degrade silently

- **GIVEN** a pending attachment whose content cannot be read at send time
- **WHEN** the client sends the message
- **THEN** the send fails with an error that names the attachment
- **AND** the message does not reach the model without it

### Requirement: Attachments grant no filesystem authority

An attachment SHALL carry content only. It SHALL NOT add its source path,
parent directory, or any other path to the session's authorized roots, and
SHALL NOT change tool or shell authorization.

#### Scenario: Attached file's directory stays unauthorized

- **GIVEN** a session with default-deny file policy and an attachment
  uploaded from `/home/user/private/report.txt`
- **WHEN** the agent invokes `file_read` on `/home/user/private/other.txt`
- **THEN** the existing policy denies the call
- **AND** the attachment changes no authorization outcome
