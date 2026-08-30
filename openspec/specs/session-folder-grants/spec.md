## Purpose

Let an operator grant a session read, modify, and create access to one
folder tree for the life of the session, with explicit revocation, while
the default-deny posture, hard-deny list, and symlink guard stay
authoritative inside the granted tree.

## Requirements

### Requirement: Grant lifecycle

The daemon SHALL let an operator-authenticated client add a folder grant to
a session and remove it again. A grant SHALL name one existing absolute
directory. The daemon SHALL validate the path before persistence and SHALL
reject a nonexistent path, a non-directory, or a path with NUL, CR, or LF
without persisting anything. A persisted grant SHALL live in the session's
durable state, survive actor recovery and daemon restart, and end with the
session. Removal SHALL revoke the grant's authority immediately.

#### Scenario: Grant persists across restart

- **GIVEN** a session with a grant for `/home/user/projects/alpha`
- **WHEN** the daemon restarts and the session recovers
- **THEN** the recovered session still holds the grant
- **AND** file access under the granted root works as before the restart

#### Scenario: Invalid grant is rejected before persistence

- **GIVEN** a grant request for `/home/user/does-not-exist`
- **WHEN** the operator submits it
- **THEN** the daemon rejects the request
- **AND** the session's persisted grant list is unchanged

#### Scenario: Removal revokes immediately

- **GIVEN** a session with a grant for `/home/user/projects/alpha`
- **WHEN** the operator removes the grant
- **THEN** the next `file_read` under `/home/user/projects/alpha` is denied
- **AND** no daemon restart is required for the revocation

### Requirement: Grant enforcement for first-party file tools

First-party file tools SHALL allow read, modify, and create operations on
canonical paths inside a granted root and its subfolders. Paths outside
every granted root SHALL keep the existing default-deny outcome. The runtime
policy SHALL evaluate the same persisted grant list that the lifecycle
operations write; there SHALL be no second grant store.

#### Scenario: Create inside a granted root is allowed

- **GIVEN** a session with a grant for `/home/user/projects/alpha`
- **WHEN** the agent invokes `file_write` on
  `/home/user/projects/alpha/notes/new.md`
- **THEN** the tool creates the file

#### Scenario: Sibling path outside the grant stays denied

- **GIVEN** a session with a grant for `/home/user/projects/alpha`
- **WHEN** the agent invokes `file_read` on
  `/home/user/projects/beta/secrets.txt`
- **THEN** the existing policy denies the call

### Requirement: Hard-deny and symlink precedence inside grants

The hard-deny path list SHALL stay authoritative inside a granted root: a
grant SHALL NOT allow access to a hard-denied path. Path evaluation SHALL
canonicalize before the decision, and a path whose resolution escapes the
granted root through a symlink SHALL be treated as outside the grant.

#### Scenario: Hard-denied file inside a granted root stays denied

- **GIVEN** a grant for `/home/user` and a hard-deny rule covering
  `/home/user/.ssh`
- **WHEN** the agent invokes `file_read` on `/home/user/.ssh/id_ed25519`
- **THEN** the call is denied
- **AND** the grant does not override the hard-deny outcome

#### Scenario: Symlink escape is denied

- **GIVEN** a grant for `/home/user/projects/alpha` that contains a symlink
  `link` whose target is `/etc`
- **WHEN** the agent invokes `file_read` on
  `/home/user/projects/alpha/link/passwd`
- **THEN** the canonical target is evaluated
- **AND** the call is denied

### Requirement: Grants do not expand shell authority

A folder grant SHALL apply to first-party file tools only. It SHALL NOT
join the shell approval safe-space root set, SHALL NOT enable the
safe-verb auto-allow short-circuit for shell commands under the granted
root, and SHALL NOT change `set_working_directory` behavior.

#### Scenario: Shell under a granted root still prompts

- **GIVEN** a session with a grant for `/home/user/projects/alpha` and no
  declared project directory
- **WHEN** the agent invokes `shell_execute` with cwd
  `/home/user/projects/alpha`
- **THEN** the shell approval gate evaluates the call as outside the
  safe-space root set
- **AND** the grant does not suppress the approval prompt
