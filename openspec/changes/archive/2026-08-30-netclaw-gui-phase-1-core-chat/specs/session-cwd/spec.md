## ADDED Requirements

### Requirement: Granted folder roots join session-owned path authorization

Path authorization for first-party file tools SHALL consult the session's
persisted folder-grant list alongside the declared project directory and
the session directory. A grant SHALL NOT change relative-path base
resolution: relative paths SHALL keep resolving against the declared
project directory or the session directory only. A grant SHALL NOT join
the shell approval safe-space root set.

#### Scenario: Absolute path under a granted root authorizes

- **GIVEN** a session with no declared project directory and a grant for
  `/home/user/projects/alpha`
- **WHEN** `file_read` receives `/home/user/projects/alpha/src/App.cs`
- **THEN** the call authorizes through the grant list

#### Scenario: Relative base resolution ignores grants

- **GIVEN** a session with no declared project directory, session directory
  `/session/current`, and a grant for `/home/user/projects/alpha`
- **WHEN** `file_write` receives the relative path `notes/result.md`
- **THEN** it resolves `/session/current/notes/result.md`
- **AND** it does not resolve against the granted root

#### Scenario: Shell safe space is unchanged by grants

- **GIVEN** a session with a grant for `/home/user/projects/alpha` and no
  declared project directory
- **WHEN** the shell approval gate computes its safe-space root set
- **THEN** the set contains the session directory only
- **AND** the granted root is not a member
