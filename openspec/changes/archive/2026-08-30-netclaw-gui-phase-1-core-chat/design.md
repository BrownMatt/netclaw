# Design: netclaw-gui-phase-1-core-chat

## Context

See `proposal.md` — Why. Current state that shapes the approach:

- `WorkingContext` (`src/Netclaw.Actors/Sessions/WorkingContext.cs`) is the
  durable "what the session works on" record. It persists through
  `SessionSnapshot` and `SessionCompacted`, survives compaction, recovery,
  and restart, and already rejects control characters in paths.
- `ScopedFileAccessPolicy` (`src/Netclaw.Actors/Tools/`) is the single path
  authorization point for first-party file tools. It already evaluates
  session directory, project directory, global read roots, hard-deny rules,
  and symlink canonicalization.
- `SessionHub` (`src/Netclaw.Daemon/Gateway/SessionHub.cs`) carries the
  operator transport: `SendMessage`, `RespondToInteraction`, attach/ensure.
  The daemon REST API serves the catalog and health surfaces. Loopback
  callers are operator-authenticated.
- The wire union `SessionOutput` already carries every event the GUI needs:
  `text`, `text_delta`, `thinking`, `tool_call`, `tool_result`,
  `tool_interaction`, `usage`, `turn_completed`, `session_title`,
  `session_joined` (with `RecentMessages` replay).
- Phase 0 delivered `Netclaw.Client` (transport library) and the
  `Netclaw.Gui` walking skeleton: one window, raw text, queue-and-flush.
- The media catalog (`Netclaw.Media`) classifies MIME, attachment category,
  and model-input eligibility.

## Goals / Non-Goals

**Goals:**

- The four spec deltas implemented on existing seams, with no parallel
  state stores and no new authority paths outside the policy.
- A GUI history model that can host rich blocks (markdown, expanders,
  approval cards), replacing the skeleton's single text buffer.
- Durable grant and attachment state that survives restart with the same
  guarantees `WorkingContext` already gives.

**Non-Goals:**

- No shell-authority change of any kind. Grants touch file tools only.
- No three-pane shell; the session list docks left of the chat pane, and
  the right pane waits for Phase 4.
- No message-history endpoint unless the replay-depth measurement says the
  `SessionJoined.RecentMessages` window is insufficient.
- No changes to Slack/webhook channels.

## Decisions

### Decision 1 — Grants live in `WorkingContext.GrantedFolders`

The grant list is a new `ImmutableList<string>` on `WorkingContext`. This
is the reuse-before-add choice: `WorkingContext` is already the durable,
compaction-surviving, snapshot-persisted session state that flows into tool
invocation context, and its control-character rejection already matches the
grant validation rules. The session actor owns the list; the policy reads
it; nothing else writes it.

*Alternative rejected:* a separate persisted grant record or table. That is
a second store for session-scoped authority — the exact drift the spec's
"no second grant store" requirement forbids.

### Decision 2 — Grant management over the hub; upload over REST

Grant add/remove are `SessionHub` methods with acks (like
`RespondToInteraction`): the GUI already holds an authenticated hub
connection and an attached session, and the command targets the session
actor. Grant changes echo to attached clients as a session output event so
grant chips update live.

Attachment upload is a REST endpoint (`POST` under the session's API path)
because the payload is binary and multipart fits REST, not SignalR. The
endpoint validates (session exists, size limit, media-catalog eligibility)
before it stores anything.

*Alternative rejected:* both operations on REST. That splits session
mutation across two auth/transport paths and forces the GUI to correlate
REST mutations with hub events for its own session.

### Decision 3 — Enforcement point stays inside `ScopedFileAccessPolicy`

The policy gains granted roots as one more authorized-root source for
absolute paths, evaluated after canonicalization and after hard-deny. The
ordering guarantees the spec's precedence scenarios: hard-deny wins inside
a grant, and a symlink that escapes the granted root fails the grant test
because the canonical path is what gets compared. Relative-base resolution
code does not change. The shell approval gate's safe-space set does not
consume the grant list.

*Alternative rejected:* a separate grant-checking wrapper in front of the
policy. A second authorization layer is a hidden bypass risk and would
duplicate canonicalization.

### Decision 4 — Pending attachments are durable session-actor state

Upload stores the file under the session directory
(`~/.netclaw/sessions/{id}/attachments/`) and records a pending reference
in durable session state. The next `SendMessage` for the session consumes
every pending reference: the daemon composes the structured content
(through the media catalog), clears the pending list in the same persisted
event, and fails the send loudly if the stored file cannot be read. Durable
pending state means a daemon restart between upload and send loses nothing
— the no-silent-fallback rule applied to attachments.

*Alternative rejected:* the client passes attachment ids in a new
`SendMessage` overload. More explicit, but it changes the hub contract for
every client and adds client-side bookkeeping; the pending model matches
the spec's embed-on-next-message contract. The known trade-off (two
operator clients on one session could interleave) is accepted and
documented — the surface is operator-only.

### Decision 5 — History is a block list, not a text buffer

`Netclaw.Gui` replaces the skeleton's single text box with an
`ItemsControl` over an observable list of block viewmodels: user message,
assistant markdown (AvaloniaEdit + TextMate), thinking expander, tool
expander (call + result), approval card, and a per-turn usage line.
`text_delta` events append into the current assistant block with ~80 ms
coalescing so the editor is not invalidated per token; the final `text`
snapshot replaces that block's content. The activity indicator is a small
state machine in the chat viewmodel driven only by `SessionOutput` events,
exactly as the spec's transitions describe.

*Alternative rejected:* rendering the whole history into one
AvaloniaEdit document. It cannot host expanders or interactive approval
cards, and full-document re-highlight on every delta does not scale.

### Decision 6 — Session list reads REST, updates from hub events

The left list loads `GET /api/sessions` on start and on reconnect, then
applies `session_title` (and future) events for the attached session live.
A click detaches the current session and attaches the selected one;
`SessionJoined.RecentMessages` replay renders the recent history. No
polling loop in this phase.

### Decision 7 — Measure replay depth before building more

An early task measures `RecentMessages` depth against real daemon sessions.
If the window covers normal GUI attach scenarios, the history endpoint (gap
5) stays unbuilt; if not, it enters as its own spec delta before any
implementation. The measurement result lands in the tasks file as evidence
either way.

## Actor boundaries and persistence

- Grant list: owned by the session actor, durable in `WorkingContext`
  (snapshot + compaction events). Policy evaluation is call-local.
- Pending attachments: owned by the session actor, durable in session
  state; attachment bytes are files under the session directory.
- GUI: call-local UI state only (blocks, expansion, activity, queue).
  Nothing persists client-side.
- All hub/REST entry points go through the existing registry → session
  actor ask paths; no new direct transport-to-actor shortcuts.

## Failure modes and recovery

- **Restart between upload and send.** Pending references and files are
  durable; the next send after recovery embeds them.
- **Stored attachment unreadable at send.** The send fails with a bounded
  error naming the attachment; the message does not go to the model without
  it.
- **Grant add during actor recovery.** The ack returns only after the
  persisted event; the GUI shows the chip on ack, not optimistically.
- **Revocation race.** Removal persists before the ack; the next policy
  evaluation reads the updated list. No cache sits between the list and the
  policy.
- **GUI reattach with a pending approval.** The replayed history renders
  the interaction; a resolved interaction renders as resolved and takes no
  second response.
- **Daemon down.** Phase 0 behavior stands: status line, queue-and-flush,
  reconnect loop.

## Risks / Trade-offs

- [Grants widen file authority] → hard-deny and symlink precedence enforced
  inside the policy, proven by the constitution's four test classes
  (rejection before persistence, runtime matches persisted grants,
  immediate revocation, hard-deny inside granted roots).
- [`WorkingContext` schema change breaks old snapshots] → new field with an
  empty default; a legacy-shape load/round-trip test per the Automation
  Floor; old snapshots recover with no grants.
- [Attachment memory/disk growth] → configured size limit enforced before
  storage; files live under the session directory and follow session
  retention; the limit key enters `netclaw-config.v1.schema.json` with a
  default in the same PR.
- [Editor performance on long sessions] → block virtualization via
  `ItemsControl`, delta coalescing, TextMate grammars loaded lazily once.
- [Two operator clients interleave pending attachments] → accepted for the
  operator-only surface; documented in the upload endpoint help.
- [Approval card double-submit] → card disables on first response; the
  daemon interaction path already rejects duplicate responses.

## Migration Plan

Additive change; no data migration. Old session snapshots deserialize with
an empty grant list and no pending attachments. Rollback is a revert;
persisted grants from a rolled-back build are ignored fields, not errors,
per the serializer's tolerant-read settings — verify this in the
legacy-shape test. Suggested commit order: (1) grants (actors + policy +
tests), (2) attachments (daemon + client), (3) GUI block history + activity
+ usage, (4) session list + `+` flows, (5) measurement + polish.

## Open Questions

- Exact default for the attachment size limit (config key with default in
  the schema; pick during implementation from media catalog guidance).
- Whether grant-change echo reuses an existing session output event type or
  adds one discriminator; resolved when the wire change is written, with
  the DTO mapper updated in the same commit.
