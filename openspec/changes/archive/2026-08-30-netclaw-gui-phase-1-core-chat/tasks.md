# Tasks: netclaw-gui-phase-1-core-chat

## 1. Folder grants — durable state and policy

- [x] 1.1 Add `GrantedFolders` (`ImmutableList<string>`, empty default) to `WorkingContext` with the same control-character rejection as existing paths; verify with unit tests that reject NUL, CR, and LF grant paths
- [x] 1.2 Add grant add/remove commands and persisted events to the session actor; validate path exists and is a directory before persistence; verify with an actor test that an invalid grant is rejected and the persisted list is unchanged
- [x] 1.3 Add a legacy-shape round-trip test: an old `SessionSnapshot` without `GrantedFolders` recovers with an empty grant list, and a snapshot from this build tolerant-reads under the old shape; verify the test passes
- [x] 1.4 Extend `ScopedFileAccessPolicy` to accept granted roots as one more authorized-root source for absolute paths, evaluated after canonicalization and after hard-deny; verify with policy unit tests that a path under a granted root authorizes and a sibling path stays denied
- [x] 1.5 Add precedence tests: hard-deny inside a granted root stays denied, and a symlink under a granted root that resolves outside the root is denied; verify both tests pass
- [x] 1.6 Prove the runtime policy reads the persisted grant list (no second store): actor-level test that a persisted grant authorizes a file tool call, and removal revokes the next call with no restart; verify both tests pass
- [x] 1.7 Confirm the shell approval safe-space set ignores grants: test that a granted root is not in the safe-space set and `shell_execute` under it still routes to approval; verify the test passes

## 2. Folder grants — transport and client

- [x] 2.1 Add `SessionHub` grant add/remove methods with acks that return only after the persisted event; verify with a hub integration test that the ack follows persistence and an invalid path returns a rejection
- [x] 2.2 Emit a grant-change session output event to attached clients and map it in the DTO layer (decide reuse-vs-new discriminator here, wire and mapper in the same commit); verify with a test that an attached client receives the event on add and on remove
- [x] 2.3 Add grant add/remove calls and the grant-change event to `Netclaw.Client` (`DaemonClient`, DTOs); verify with client mapping tests

## 3. Attachments — daemon and client

- [x] 3.1 Size-limit config: RESOLVED BY REUSE — `ToolAudienceProfile.ChannelAttachments` (`MaxFileBytes`, `AllowedCategories`) already exists in `Netclaw.Configuration` and in `netclaw-config.v1.schema.json` with defaults; the upload endpoint enforces the Personal-audience policy instead of adding a parallel key; verify the endpoint tests in 3.2 prove the limit applies
- [x] 3.2 Add `POST` upload endpoint under the session API path: validate session exists, enforce the size limit, classify through the media catalog, store the file, return an attachment reference; verify with API tests for success, unknown session, and oversize (no partial file remains). NOTE: files land in the canonical session `inbox/` (the same store channel attachments use), not a new `attachments/` directory — reuse-before-add
- [x] 3.3 Add durable pending-attachment state to the session actor; the next `SendMessage` embeds every pending reference structurally once and clears the list in the same persisted event; verify with an actor test that the second message carries no repeated content
- [x] 3.4 Fail the send loudly when a stored attachment cannot be read at embed time (error names the attachment, message does not reach the model); verify with a fake-failure test
- [x] 3.5 Add a restart test: upload, restart the actor, send; the recovered pending reference embeds; verify the test passes
- [x] 3.6 Prove attachments grant no filesystem authority: test that the source directory of an uploaded file stays denied to `file_read`; verify the test passes
- [x] 3.7 Add the upload call to `Netclaw.Client`; document the two-client interleave trade-off in the endpoint help; verify with a client test against a fake daemon

## 4. GUI — block history, streaming, activity, usage

- [x] 4.1 Replace the skeleton text buffer with an `ItemsControl` over block viewmodels (user message, assistant markdown via AvaloniaEdit + TextMate, thinking expander, tool expander, approval card, usage line); verify with viewmodel tests that each `SessionOutput` kind produces the right block
- [x] 4.2 Implement delta streaming with ~80 ms coalescing and final-`text` snapshot replacement; verify with a viewmodel test that accumulated deltas are replaced by the snapshot with no duplication
- [x] 4.3 Implement collapsed-by-default thinking and tool sections (call + result share one section, expansion is local state); verify with viewmodel tests
- [x] 4.4 Implement inline approval cards through `RespondToInteraction`: card disables after one response, renders the resolved outcome, and sends nothing for a detached session; verify with viewmodel tests for resolve-once and detach
- [x] 4.5 Implement the activity indicator state machine (Waiting, Responding, Running tool with name, Approval required, idle) driven only by `SessionOutput` events; verify with a transition table test
- [x] 4.6 Implement the usage line `in=<n> out=<n> (<p>% ctx)` from the latest `usage` event, blank before the first event; verify with a viewmodel test
- [x] 4.7 Keep queue-and-flush behavior on the new history model: queued count shown, ordered flush on re-attach, failed send re-queues; verify with viewmodel tests and one manual daemon-down smoke pass
  — Manual pass (2026-08-30): daemon stopped, two messages typed → status
  "Queued (2)"; daemon restarted → both flushed in order; second reply was
  exactly "FLUSHED-OK". A hub-rejected send (daemon dropped the session
  binding) re-queues and triggers one guarded re-ensure + flush.

## 5. GUI — session list and + flows

- [x] 5.1 Add the read-only session list: load `GET /api/sessions` on start and reconnect, order by activity, show title and channel; verify with a viewmodel test over fake catalog data
- [x] 5.2 Wire click-to-attach: detach current, attach selected, render `SessionJoined.RecentMessages` replay as blocks; verify manually against a TUI-created session
  — Manual pass (2026-08-30): clicked the TUI-created session "Current
  session directory question"; replayed user + assistant messages rendered
  as blocks with TextMate markdown.
- [x] 5.3 Apply `session_title` events to the list live; verify with a viewmodel test
- [x] 5.4 Add the `+` flows: attach-file picker calls upload and shows the pending attachment on the composer, grant-folder picker calls grant add and shows a removable grant chip fed by grant-change events; verify with viewmodel tests plus a manual end-to-end pass (upload → send → model sees content; grant → `file_read` works → remove → denied)
  — Manual pass (2026-08-30): `+` → folder picker → grant chip appeared
  from the `folder_grant` echo; chip `x` revoked (chip cleared). Upload via
  the REST endpoint → next GUI message embedded one `[attachment]` line →
  model read `inbox/upload-note.txt` and returned the marker word. Grant →
  `file_read` under the granted root returned the marker word. NOTE: the
  live "remove → denied" leg is not observable on this machine's default
  config — an interactive Personal session has Mode.All (shell-equivalent)
  read reach by existing design, so grants change no live outcome there;
  scoped-config enforcement and revocation are proven by the
  `FolderGrantFileAccessPolicyTests` and `FolderGrantIntegrationTests`.

## 6. Replay depth measurement

- [x] 6.1 Measure `SessionJoined.RecentMessages` depth against real daemon sessions and record the result plus the keep-or-build decision for the history endpoint in this file; verify the note below this task is filled in

  **Measurement (2026-08-30, live daemon catalog via `GET /api/sessions`):**
  33 real sessions; turn counts: 0 turns ×3, 1 turn ×19, 2 ×5, 3 ×4, 5 ×2 (max = 5).
  The replay window (`SessionRecentMessageExtractor`) carries the last 20
  user/assistant messages (≈10 turns), each truncated at 2000 characters.
  Every observed session fits completely inside the window.
  **Decision: the replay window is sufficient — the history endpoint (gap 5)
  stays unbuilt.** Revisit if real GUI sessions start exceeding ~10 turns
  routinely; a history endpoint then enters as its own spec delta first.

## 7. Gates, docs, and validation

- [x] 7.1 Update the `netclaw-operations` system skill (config key, upload endpoint, grant commands) and bump its version; verify the skill text names the new surfaces
- [x] 7.2 Document operational impact: CLI/API help for upload and grants, GUI notes in `docs/netclaw/`; verify docs mention grant revocation and the attachment size limit
- [x] 7.3 Run quality gates: `dotnet slopwatch analyze` (no new violations), `./scripts/Add-FileHeaders.ps1 -Verify`, Release build with zero warnings, full test suite with only known pre-existing failures; verify all pass
  — 2026-08-30: slopwatch 0 issues; headers pass; Release build of
  Netclaw.slnx succeeded with 0 warnings; full test suite green except the
  three known pre-existing failures (2× SessionMemoryObserverActorTests
  prompt snapshots, 1× python MCP stdio smoke — environmental).
- [x] 7.4 Run a manual end-to-end smoke: TUI and GUI attached to one session, streamed turn with thinking + tool sections, approval card resolve, usage line, restart-persistence of a grant; verify each behavior against its spec scenario
  — Manual pass (2026-08-30, repo Release daemon + GUI, local Ollama):
  GUI attached to a TUI-created session; a second hub client attached to
  the same session concurrently (grant CLI). Streamed turns rendered
  collapsed Thinking and `Tool: file_read` / `Tool: shell_execute`
  expanders. A mutating shell command with cwd INSIDE the granted root
  raised the approval prompt (grants do not expand shell authority — live
  spec scenario); the inline card showed Once / This chat / Always here /
  Always anywhere / Deny; "Once" resolved it and the tool ran. The usage
  line tracked every turn (`in=… out=… (…% ctx)`). A grant added before a
  daemon restart survived it (duplicate add rejected with "already
  granted" after the restart).
  Known cosmetic gaps recorded for a later phase:
  1. history does not auto-scroll to the newest block;
  2. grant chips populate from live `folder_grant` events only — a fresh
     attach shows no chips until the next grant change (SessionJoined does
     not carry the grant list);
  3. a replay that lands right after a recovery flush can clear the
     just-sent user message block (render-order race, content is durable);
  4. pre-existing daemon behavior: when one operator client of a session
     disconnects, the session's SignalR registry binding drops and other
     clients' next send is rejected once (the GUI now recovers with one
     guarded re-ensure).
