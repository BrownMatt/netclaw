# gui-core-chat Delta

## ADDED Requirements

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
