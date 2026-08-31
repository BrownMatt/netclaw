# Design: netclaw-gui-phase-3-session-management

## Context

See `proposal.md` — Why. Current state that shapes the design:

- Titles are LLM-generated only. `SessionTitleGenerator.ShouldGenerate`
  fires a sidecar call; `TitleGenerationCompleted` calls
  `LlmSessionActor.SetTitle`, which persists `SessionTitleSet` and emits
  `SessionTitleOutput`
  (`src/Netclaw.Actors/Sessions/Pipelines/SessionTitleGenerator.cs`,
  `LlmSessionActor.cs`). `SessionTitleSet` is a protobuf-mapped persisted
  event.
- The session catalog is the SQLite `sessions` table owned by
  `SessionCatalogService`, with a schema-detection migration seam
  (`EnsureSchemaUpToDate`) and `ListRecent(limit, offset)` with no
  filters (`src/Netclaw.Daemon/Gateway/SessionCatalogService.cs`).
- Akka persistence uses SQLite tables `journal`, `journal_metadata`,
  `tags`, and `snapshot` in the same `netclaw.db`
  (`src/Netclaw.Daemon/migrations/sqlite/001_akka_persistence_tables.sql`).
  Journal persistence ids are `session-{sessionId}`.
- Per-session logs live at
  `{SessionLogsDirectory}/{sanitized_id}/session.log`, held open by
  `SessionLogActor` (a child of the session actor). Session directories
  live under `{SessionsDirectory}` (`NetclawPaths`).
- Hub commands via `SessionRegistry` are scoped to the session attached
  to the calling connection (the folder-grant and model-override
  pattern). Management operations target arbitrary list rows, including
  sessions with no attached client and no running actor.

## Goals / Non-Goals

**Goals:**

- Rename persists with the session and wins over the title generator.
- Pin and archive are cheap, reversible catalog metadata.
- Delete removes every store a session touches, loudly reporting any
  partial failure.
- All four operations work on any cataloged session, attached or not.

**Non-Goals:**

- No bulk operations, export, or retention policies. No TUI surface for
  the new operations. No change to session identity, channel binding, or
  attach semantics.

## Decisions

### D1. Rename is a session actor command; the lock lives on the persisted title event

`SessionTitleSet` gains `Locked: bool` (protobuf-compatible addition;
absent on old events → `false`, so generated titles stay replaceable).
`SessionState` applies it to a `TitleLocked` flag. A new
`RenameSession { SessionId, Title }` command validates a non-empty
title, persists `SessionTitleSet { Locked = true }`, acks after persist,
and emits the existing `SessionTitleOutput`. The generator gate is
two-layer: the actor skips firing `SessionTitleGenerator` when
`TitleLocked` is set (no wasted sidecar call), and the
`TitleGenerationCompleted` handler drops a late result when the flag is
set (covers a generation already in flight when the rename lands).
Rationale: the title is persisted actor state; writing the catalog
column alone would drift on the next recovery. Alternative — a separate
`SessionTitleLocked` event — adds a second event whose only meaning is a
flag on the first.

Data classification: title and lock are durable actor state (journal +
snapshot). The catalog `title` column stays a projection updated by
`SessionTitleOutput`, exactly as today.

### D2. Pin and archive are catalog columns, not actor state

`sessions` gains `pinned` and `archived` integer columns (default 0)
through `EnsureSchemaUpToDate` column detection (ALTER TABLE ADD COLUMN
when missing — same seam the legacy migration uses). `ListRecent` gains
filter parameters; the default listing excludes archived rows.
`SessionCatalogEntry` and the client DTO gain the two fields. Rationale:
pin and archive change how the list renders, not how the session
behaves; putting them in actor state would force actor materialization
for a list toggle. Negative example kept explicit: an archived session
that receives an attach request still attaches — archive is a list
filter, not a lifecycle state.

### D3. All four operations are operator-authenticated REST, not hub commands

Routes under the existing authenticated API. Session ids contain `/`
(`signalr/{guid}`), so every route takes the id as a query parameter —
the convention the attachment upload endpoint already set — not as a
path segment:

- `POST /api/sessions/rename?sessionId={id}` body `{ title }` — routes
  to the actor command via the session command pipeline (materializes
  the actor when passivated, same as any command delivery), ack after
  persist. The endpoint rejects an id the catalog does not know before
  the pipeline, because the command pipeline creates actors on demand
  and a rename must not create a session.
- `PATCH /api/sessions/flags?sessionId={id}` body
  `{ pinned?, archived? }` — catalog update; rejects an id the catalog
  does not know.
- `DELETE /api/sessions?sessionId={id}` — teardown (D4).

Rationale: `SessionRegistry` hub commands are deliberately scoped to the
attached session; management acts on arbitrary rows. Widening the hub
contract would weaken the attachment invariant for no gain. The REST
surface reuses the catalog service for existence checks and the command
pipeline for actor delivery.

### D4. Delete is a daemon teardown service with a blocked-id window

A new `SessionTeardownService` executes, in order:

1. Mark the session id as deleting so the registry rejects new attach
   and ensure calls for it during teardown.
2. Detach any attached client and signal that the session is gone.
3. Stop the session actor (and thereby `SessionLogActor`, releasing the
   log file handle) and await termination.
4. Delete rows keyed by `persistence_id = 'session-{id}'` from
   `journal`, `journal_metadata`, `tags`, and `snapshot` with direct
   SQL.
5. Delete the catalog row.
6. Delete the session directory, staged attachment files, and the
   session log directory, using paths derived from `NetclawPaths` and
   the sanitized session id — never from client-supplied text.

Direct SQL over Akka's `DeleteMessages` because `DeleteMessages` marks
rows deleted rather than removing them, and it cannot touch
`journal_metadata` or `tags`; the actor is stopped first, so there is no
concurrent writer. Steps 4–6 continue through individual failures and
collect them; the operation reports success only when every step
succeeded, otherwise it returns the completed and failed steps and logs
each one. Recovery safety: restart recovery materializes sessions from
the catalog; with the catalog row gone, nothing recovers. A later
`EnsureSession` with the same id creates a fresh, empty session — a new
session that merely reuses the string, which is acceptable because ids
are GUIDs.

### D5. GUI applies daemon confirmations and refreshes the list

The left pane renders two groups from one catalog response (pinned;
unpinned and unarchived). The context menu commands call the client
methods; after an ack the GUI refreshes the list from
`GET /api/sessions` rather than patching local state optimistically. A
rejected operation keeps the previous list and surfaces the reason.
Rename opens a single-field dialog pre-filled with the current title;
Delete opens a confirmation dialog that names the session and states
permanence. Deleting the currently attached session clears the chat pane
when the detach signal arrives.

Reconnect after a delete (found by the E2E smoke): the client reconnect
authority only re-attaches an existing session. After a delete of the
attached session, a transport drop left no owner for recovery, and the
GUI hung on "Reconnecting...". The shell therefore rearms its guarded
connect loop when the transport drops or disconnects while no session is
ensured; the loop connects, ensures a fresh session, and refreshes the
list. A manual attach also sets the ensured flag and refreshes the list,
so a send after such a recovery dispatches instead of queuing forever.

## Risks / Trade-offs

- [Revival race: a client ensures the session mid-teardown] → the
  deleting-id block in the registry (D4 step 1) closes the window; an
  ensure during teardown fails loudly.
- [Windows file locks on the session log] → the actor stop in step 3
  releases the handle; the teardown retries a locked file once, then
  reports the step failed. No silent success.
- [Old persisted `SessionTitleSet` events have no `Locked` field] →
  protobuf default `false` means every pre-change title behaves as
  generated — correct, because they all were.
- [Catalog schema drift on old databases] → column detection in the
  existing migration seam; adding columns with defaults is idempotent.
- [Rename of a session whose journal cannot recover] → the command
  pipeline fails to materialize the actor and the rename fails loudly;
  the catalog row is untouched.
- [Title event flows only to attached clients] → the GUI refreshes the
  list after each ack, so a rename of an unattached session still
  renders without waiting for an output event.
