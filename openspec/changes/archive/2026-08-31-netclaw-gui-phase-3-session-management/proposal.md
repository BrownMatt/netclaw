# Proposal: netclaw-gui-phase-3-session-management

## Why

The GUI session list is read-only. The operator cannot rename, pin,
archive, or delete a session. Old sessions accumulate in the list, and a
wrong LLM-generated title cannot be corrected. GUI roadmap Phase 3
(`docs/netclaw/plans/netclaw-gui-plan.md`) makes session management the
next deliverable after the model dropdown, with the exit criterion "the
left pane matches the full design".

## What Changes

- Add a manual rename. A manual title persists with a locked flag, so the
  sidecar title generator does not overwrite it.
- Add pin and archive as session catalog columns with list filters. A
  pinned session sorts into a pinned section. An archived session leaves
  the default list but stays attachable through a filter.
- Add a delete operation with full multi-store teardown: stop the session
  actor, delete the journal and snapshot rows, delete the catalog row,
  delete the session directory and staging files, and delete the session
  log.
- Add operator-authenticated daemon surface for the four operations.
- GUI: split the left pane into a Pinned section and an All sessions
  section, add a right-click context menu (Rename, Pin/Unpin, Archive,
  Delete), and require a confirmation dialog before delete that names the
  session and states that deletion is permanent.

## Capabilities

### New Capabilities

- `session-management`: rename with a title lock, pin, archive, and
  delete with multi-store teardown — commands, catalog columns, list
  filters, and their authority boundaries.

### Modified Capabilities

- `gui-core-chat`: replace the read-only constraint on the session list
  with the managed list — pinned/unpinned sections, the context menu, and
  the delete confirmation dialog.

## Impact

- **PRD linkage:** PRD-003 (`UX-002` session inspector) is the closest
  operator-UX anchor. No PRD requirement covers rename, pin, archive, or
  delete of sessions, so the specs in this change are the authority.
- **Code:** `LlmSessionActor` (rename command, title-lock gate on
  `TitleGenerationCompleted`), `SessionState`/`SessionProtocol.Events`
  (locked title event), `SessionCatalogService` (pin/archive columns,
  filters, delete of the catalog row), a session teardown service in the
  daemon, `SessionHub`/`SessionRegistry` or REST routes for the four
  operations, `Netclaw.Client`, `Netclaw.Gui` (left pane sections,
  context menu, dialogs).
- **Security:** all four operations require the same operator
  authentication as the rest of the daemon API. Delete is destructive and
  irreversible; the daemon refuses a delete for an unknown session id and
  tears down only stores keyed by that session's persistence id — no
  path traversal from client input. The GUI confirmation dialog is a
  usability guard, not the security boundary.
- **Operations:** delete logs each teardown step, so a partial failure is
  diagnosable. Archive is reversible; delete is not. The catalog schema
  gains columns through the existing SQLite migration seam.
- **Out of scope for this change:** bulk operations, session export
  before delete, retention policies, TUI parity for the new operations,
  and any change to session identity or channel binding.
