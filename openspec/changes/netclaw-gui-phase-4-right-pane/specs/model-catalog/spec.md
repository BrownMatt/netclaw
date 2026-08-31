# model-catalog Delta

## ADDED Requirements

### Requirement: Running-models listing

The system SHALL serve a listing of the models currently loaded by the
configured providers over the daemon API, with the same operator
authentication as the catalog. Each entry SHALL name the model, the
provider key, the loaded size when the provider reports it, and the
expiry time when the provider reports it. A provider without the concept
of loaded models SHALL contribute no entries and SHALL NOT appear as
failed for that reason. A provider whose running-models probe fails
SHALL appear as failed with a reason; the system SHALL NOT return an
empty listing that hides a probe failure.

#### Scenario: Loaded model appears with size and expiry

- **GIVEN** an Ollama provider with a loaded model
- **WHEN** an operator client requests the running-models listing
- **THEN** the response lists the model with its provider key, its
  loaded size, and its expiry time

#### Scenario: Provider without loaded-model support contributes nothing

- **GIVEN** a configured provider whose backend has no loaded-models
  concept
- **WHEN** an operator client requests the running-models listing
- **THEN** the provider contributes no entries
- **AND** the provider does not appear as failed

#### Scenario: Probe failure is loud

- **GIVEN** an Ollama provider whose backend is unreachable
- **WHEN** an operator client requests the running-models listing
- **THEN** the provider appears as failed with a reason
