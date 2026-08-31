# Tasks: netclaw-gui-phase-3-session-management

## 1. Rename with title lock (D1)

- [x] 1.1 Add `Locked` to `SessionTitleSet` (event, protobuf mapping,
  serialization round-trip test), apply it to `SessionState.TitleLocked`,
  and add the `RenameSession` command with actor handlers: reject empty
  titles, persist with `Locked = true`, ack after persist, emit
  `SessionTitleOutput`. Verify with actor tests: rename persists and
  survives recovery; empty rename nacks.
- [x] 1.2 Gate the title generator on `TitleLocked` at both layers: skip
  firing generation, and drop a late `TitleGenerationCompleted`. Verify
  with actor tests for both layers.

## 2. Pin and archive catalog state (D2)

- [x] 2.1 Add `pinned` and `archived` columns via `EnsureSchemaUpToDate`
  column detection, add `SetPinned`/`SetArchived` catalog operations that
  reject unknown ids, and extend `ListRecent` with filters (default
  excludes archived). Verify with catalog tests: migration from the
  previous schema, filter combinations, unknown-id rejection.
- [x] 2.2 Extend `SessionCatalogEntry` and the client DTO with the two
  fields and map them through `GET /api/sessions`. Verify with list
  endpoint/mapping tests.

## 3. Management REST surface (D3)

- [x] 3.1 Add `POST /api/sessions/rename` routed to the actor
  command pipeline (materializes a passivated actor; ack after persist)
  and `PATCH /api/sessions/{id}` for pin/archive. Both operator-
  authenticated. Verify with endpoint tests: authenticated success,
  unauthenticated refusal, unknown session rejection, empty title
  rejection.

## 4. Delete teardown (D4)

- [x] 4.1 Implement `SessionTeardownService`: deleting-id block in the
  registry, detach + gone signal for attached clients, actor stop with
  termination await, SQL deletes for journal/journal_metadata/tags/
  snapshot, catalog row delete, and path-derived file cleanup with one
  retry on a locked file. Collect per-step results; report failure with
  completed and remaining steps. Verify with tests: full teardown
  removes all stores; partial failure reports steps; ensure during
  teardown is rejected; deleted session does not recover after restart
  (catalog empty, no actor).
- [x] 4.2 Add `DELETE /api/sessions` (operator-authenticated) over
  the teardown service. Verify with endpoint tests: success, unknown id,
  unauthenticated refusal.

## 5. Client (D3, D5)

- [x] 5.1 Add `DaemonApi`/service methods for rename, pin, archive, and
  delete, plus the detach-gone handling in `DaemonClient` for a deleted
  attached session. Verify with client tests.

## 6. GUI (gui-core-chat delta)

- [x] 6.1 Split the session list into pinned and unpinned sections and
  render pin state from the catalog fields. Verify with viewmodel tests.
- [x] 6.2 Add the context menu (Rename, Pin/Unpin, Archive, Delete), the
  rename dialog, and the delete confirmation dialog; wire commands to
  the client methods with confirmation-driven refresh (no optimistic
  patch) and rejection surfacing. Verify with viewmodel tests: delete
  sends only after confirm; cancel sends nothing; rejection keeps list
  state and surfaces the reason; attached-session delete clears the chat
  pane.

## 7. Verification and docs

- [x] 7.1 Run the gates: `dotnet build` (0 warnings), full test suite,
  `dotnet slopwatch analyze`, `./scripts/Add-FileHeaders.ps1 -Verify`.
- [x] 7.2 E2E smoke on the local install: rename a session and confirm
  the generator does not overwrite it, pin, archive (row leaves the
  default list), delete with confirmation, restart the daemon, confirm
  the deleted session is gone. Record the result in the vault
  (`docs/netclaw/gui-phase-3.md`).
- [x] 7.3 Confirm no config schema change is needed
  (`ConfigSchemaDoctorCheck` passes unchanged).
