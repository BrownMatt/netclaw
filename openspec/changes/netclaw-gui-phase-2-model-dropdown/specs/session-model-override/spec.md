# session-model-override Delta

## Purpose

Give one session a temporary main-role model override. The operator can
switch the active session between hosted models mid-conversation without a
config edit or a daemon restart.

## ADDED Requirements

### Requirement: Per-session main-role model override

The system SHALL route a session's main-role chat calls to the override
model while an override is set. Compaction-role calls SHALL keep the
configured compaction routing. The configured fallback chain SHALL NOT
apply to an override model; a failed override call fails loudly.

#### Scenario: Override routes the next turn

- **GIVEN** a session with no override
- **WHEN** the operator sets the override to a resolvable model
- **THEN** the next main-role chat call for that session uses the override
  model
- **AND** other sessions keep their existing routing

#### Scenario: Compaction keeps the configured model

- **GIVEN** a session with an override set
- **WHEN** compaction runs for that session
- **THEN** the compaction call uses the configured compaction routing, not
  the override model

#### Scenario: Unknown model is rejected loudly

- **WHEN** the operator sets the override to a model that does not resolve
  through a configured provider
- **THEN** the command is rejected with a reason that names the model
- **AND** the session's routing does not change

### Requirement: Override lifetime is actor state only

The system SHALL hold the override in session actor state only. The
override SHALL NOT be persisted. A daemon restart SHALL clear the override
and return the session to the configured main-role routing. A clear
command SHALL return the session to the configured main-role routing
without a restart.

#### Scenario: Restart clears the override

- **GIVEN** a session with an override set
- **WHEN** the daemon restarts and the session recovers
- **THEN** the session's main-role calls use the configured main model
- **AND** attached clients see no active override

#### Scenario: Clear command restores configured routing

- **GIVEN** a session with an override set
- **WHEN** the operator clears the override
- **THEN** the next main-role call uses the configured main model

### Requirement: Override commands acknowledge after apply

The system SHALL acknowledge a set or clear command only after the session
actor applies the new routing. After apply, the system SHALL emit an
output event that carries the session's current override state to every
attached client.

#### Scenario: All attached clients see the change

- **GIVEN** two operator clients attached to one session
- **WHEN** one client sets the override
- **THEN** the ack returns to the caller after the actor applies the
  override
- **AND** both clients receive an output event with the new override state

#### Scenario: Join snapshot carries the override

- **GIVEN** a session with an override set
- **WHEN** a client attaches to the session
- **THEN** the join snapshot reports the active override model

### Requirement: Capabilities follow the active model

The system SHALL resolve the capabilities of the override model when the
override is set, and SHALL apply modality gating from the active model's
capabilities. When capability resolution fails, the system SHALL treat the
model as text-only and SHALL log the failure.

#### Scenario: Modality gating tracks the override

- **GIVEN** the configured main model accepts image input
- **AND** the session's override model is text-only
- **WHEN** a message with an image attachment reaches the session
- **THEN** the image is gated by the override model's capabilities

#### Scenario: Capability lookup failure degrades loudly

- **WHEN** capability resolution for the override model fails
- **THEN** the session treats the override model as text-only
- **AND** the failure appears in the daemon log

### Requirement: Override selects only configured-provider models

The system SHALL accept an override only for a model that resolves through
a provider already present in the daemon configuration. The override
surface SHALL NOT accept a provider endpoint, credential, or new provider
definition.

#### Scenario: Configured provider model is accepted

- **GIVEN** provider `local-ollama` is configured
- **WHEN** the operator sets the override to a model served by
  `local-ollama`
- **THEN** the override is applied

#### Scenario: Injected provider data is rejected

- **WHEN** a client sends an override request that carries anything other
  than a model selector (for example a URL or an API key)
- **THEN** the request is rejected
- **AND** no provider configuration changes
