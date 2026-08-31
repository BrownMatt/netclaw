# model-catalog Delta

## Purpose

Expose the models an operator can select for a session. The daemon owns
provider configuration and probe code, so the daemon serves the catalog to
clients over its authenticated API.

## ADDED Requirements

### Requirement: Operator-authenticated model catalog endpoint

The system SHALL serve a catalog of selectable models over the daemon API.
The endpoint SHALL require the same operator authentication as the rest of
the daemon API. Each entry SHALL identify the model and its provider.

#### Scenario: Authenticated listing succeeds

- **GIVEN** a configured provider whose backend is reachable
- **WHEN** an authenticated operator client requests the catalog
- **THEN** the response lists the models available from that provider
- **AND** each entry names the model and the provider key

#### Scenario: Unauthenticated request is refused

- **WHEN** a request without operator credentials hits the catalog
  endpoint
- **THEN** the daemon refuses the request with an authentication error

### Requirement: Per-provider probe failure reporting

The system SHALL report a provider whose model probe fails as failed, with
a reason. Models from other providers SHALL still appear. The system SHALL
NOT return an empty catalog that hides a probe failure.

#### Scenario: One dead provider does not empty the catalog

- **GIVEN** two configured providers, one reachable and one unreachable
- **WHEN** an operator client requests the catalog
- **THEN** the reachable provider's models appear
- **AND** the unreachable provider appears as failed with a reason

### Requirement: Capability data with an explicit unknown state

Each catalog entry SHALL carry capability data when the provider reports
it, including tool-call support. When the provider does not report a
capability, the entry SHALL mark that capability as unknown. The system
SHALL NOT map an absent capability report to "not supported".

#### Scenario: Absent capability report maps to unknown

- **GIVEN** a provider that does not report capability data for a model
- **WHEN** the catalog lists that model
- **THEN** the entry marks tool support as unknown, not as unsupported
