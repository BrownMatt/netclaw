# turn-loop-governance Delta: improve-tool-calling (Phase 1 — loop quality)

## ADDED Requirements

### Requirement: Tool-error follow-up nudge

When a tool result is an error, the session SHALL append one system nudge to
the conversation before the next model invocation. The nudge SHALL instruct
the model to try a different approach or report the failure, and to not retry
the same tool with identical arguments.

The session SHALL suppress the nudge when the duplicate-call guard already
fired for that tool in the same turn. The session SHALL append at most one
nudge per tool iteration, regardless of how many tool calls in the iteration
returned errors.

The nudge text SHALL NOT include tool arguments or tool output beyond what
the transcript already contains.

A configuration switch SHALL control the behavior. The switch defaults to
enabled. When the switch is disabled, the session SHALL behave exactly as it
does today.

#### Scenario: Tool error produces one nudge

- **WHEN** a tool iteration returns one or more error results
- **THEN** the session appends exactly one follow-up nudge before the next
  model invocation

#### Scenario: Duplicate guard suppresses the nudge

- **GIVEN** the duplicate-call guard already fired for a tool in this turn
- **WHEN** the same tool returns another error result
- **THEN** the session does not append a tool-error nudge for that result

#### Scenario: Kill switch restores current behavior

- **GIVEN** the tool-error nudge switch is disabled
- **WHEN** a tool iteration returns an error result
- **THEN** the session appends no nudge and continues with today's behavior

### Requirement: Per-response thinking cap

The session SHALL enforce a configurable cap on the number of thinking tokens
accepted within a single streamed model response. The cap SHALL apply for
every provider type.

When a response reaches the cap, the session SHALL:

- cancel the in-flight stream
- retain the accumulated thinking content for the transcript
- append one nudge that instructs the model to act with a tool call or
  report its status
- invoke the model again, at most once per turn for this cause

If the re-invoked response reaches the cap again in the same turn, the
session SHALL end the turn through the existing completion path and SHALL
deliver an operator-visible message that names the cap as the cause.

The session SHALL write one log record per cap breach. The record SHALL
include the model id and the thinking-token count.

A configuration setting SHALL hold the cap value, and a switch SHALL disable
the cap. The switch defaults to enabled. When the switch is disabled, the
session SHALL accept unbounded thinking, which is today's behavior.

#### Scenario: Cap breach cancels and re-prompts once

- **GIVEN** the thinking cap is enabled with value N
- **WHEN** a streamed response accumulates N thinking tokens without a tool
  call or final text
- **THEN** the session cancels the stream, keeps the accumulated thinking in
  the transcript, appends an act-or-report nudge, and invokes the model again

#### Scenario: Second breach in one turn ends the turn

- **GIVEN** a turn already consumed its one cap re-prompt
- **WHEN** the next response reaches the cap again
- **THEN** the session ends the turn through the existing completion path
- **AND** the delivered message names the thinking cap as the cause

#### Scenario: Breach is logged for cap sizing

- **WHEN** any response reaches the thinking cap
- **THEN** the session writes one log record with the model id and the
  thinking-token count

#### Scenario: Disabled cap preserves current behavior

- **GIVEN** the thinking-cap switch is disabled
- **WHEN** a response streams thinking without limit
- **THEN** the session applies no cap and behaves as it does today

### Requirement: Plan-without-action re-prompt

The response classifier SHALL support a plan-without-action classification: a
completed response that is short, states an intent to act, requests no tool
call, and gives no final answer.

When the feature is enabled and a response classifies as plan-without-action,
the session SHALL re-prompt the model to execute the stated plan. The session
SHALL re-prompt at most 3 times per turn for this cause. The session SHALL
stop re-prompting early when a re-prompted response repeats or restates a
prior plan-only response in the same turn.

The classification heuristic SHALL be an original implementation. The
implementation SHALL NOT copy code, regular expressions, or comment text from
the Unsloth Studio backend (AGPL-3.0-only).

A configuration switch SHALL control the behavior. The switch defaults to
disabled. When the switch is disabled, the session SHALL deliver plan-only
responses unchanged, which is today's behavior.

#### Scenario: Plan-only response triggers a re-prompt

- **GIVEN** the plan-without-action switch is enabled
- **WHEN** a response states intent to act, requests no tool, and gives no
  final answer
- **THEN** the session re-prompts the model to execute the plan

#### Scenario: Repeat guard stops the loop

- **GIVEN** a re-prompted response restates the same plan
- **WHEN** the session evaluates the response
- **THEN** the session stops re-prompting and delivers the response

#### Scenario: Re-prompt budget is bounded

- **GIVEN** a turn already used 3 plan-without-action re-prompts
- **WHEN** the next response is again plan-only
- **THEN** the session delivers the response without another re-prompt

#### Scenario: Disabled by default

- **GIVEN** a default configuration
- **WHEN** a response classifies as plan-without-action
- **THEN** the session delivers the response unchanged
