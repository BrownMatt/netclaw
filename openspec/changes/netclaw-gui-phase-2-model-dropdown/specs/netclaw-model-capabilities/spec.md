# netclaw-model-capabilities Delta

## ADDED Requirements

### Requirement: Tool-call support detection

Capability resolution SHALL surface tool-call support as a tri-state value:
supported, unsupported, or unknown. A resolver that cannot determine
tool-call support SHALL leave the value unknown. Composite field-merge
SHALL fill an unknown tool-call value from a later resolver, following the
same semantics as the modality fields.

#### Scenario: Ollama reports tool support

- **GIVEN** an Ollama model whose `/api/show` capabilities include `tools`
- **WHEN** capability resolution runs for that model
- **THEN** the resolved capabilities mark tool-call support as supported

#### Scenario: Absent report stays unknown

- **GIVEN** a resolver response with no tool-call information
- **WHEN** capability resolution runs
- **THEN** the resolved capabilities mark tool-call support as unknown
- **AND** downstream consumers do not treat unknown as unsupported
