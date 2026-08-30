# Proposal: netclaw-gui-phase-1-core-chat

## Why

Phase 0 proved the GUI transport seam with a walking skeleton. The skeleton
is not usable for daily work: it renders raw text only, hides thinking and
tool activity, cannot attach files, and cannot grant folder access. Phase 1
of the GUI plan (`RiderProjects/docs/netclaw/plans/netclaw-gui-plan.md`)
turns the skeleton into a daily-drivable chat client.

## What Changes

- **GUI middle pane, complete.** Streaming chat history with
  AvaloniaEdit/TextMate markdown rendering. Collapsible headers for thinking
  and for tool calls with results; both start collapsed. Inline approval
  cards for `tool_interaction` events. An activity indicator with the states
  from the plan (Waiting, Responding, Running tool, Approval required,
  idle). A usage line that shows `in=NN out=NN (PP% ctx)` from the `usage`
  event. The input box keeps the Phase 0 queue-and-flush behavior.
- **GUI left pane, read-only slice.** List all sessions from
  `GET /api/sessions`. A click attaches the selected session. Titles update
  live from `session_title` events. No rename, pin, archive, or delete in
  this phase.
- **Daemon: attachment upload (gap 2).** A new upload endpoint plus hub
  binding so a client can attach a file to the next message. The daemon
  embeds the file content in the prompt structurally, once, at send time.
- **Daemon: session folder grants (gap 3).** A session gains a grant list in
  `WorkingContext`. `ScopedFileAccessPolicy` accepts read, modify, and
  create inside a granted root and its subfolders. A grant lives for the
  session life. Removal revokes access immediately. Hard-deny paths stay
  denied inside a granted root. **This widens a security boundary by
  design**; the spec deltas carry positive and negative examples, and the
  test classes the constitution requires.
- **GUI `+` flows.** The `+` button attaches a file (through the upload
  endpoint) or grants a folder (through the grant seam), with visible grant
  chips and a remove action.
- **Replay depth measurement (gap 5, conditional).** Measure
  `SessionJoined.RecentMessages` depth against real sessions. Build the
  history endpoint only if the replay window is insufficient; if built, it
  enters as its own spec delta before implementation.

Not a breaking change. Existing channels (Slack, TUI, webhooks) keep their
behavior.

**Out of scope** (later phases): model dropdown and per-session override
(Phase 2), rename/pin/archive/delete (Phase 3), right pane and log tails
(Phase 4), packaging (Phase 5).

## Capabilities

### New Capabilities

- `gui-core-chat`: the desktop GUI chat surface — history rendering,
  collapsible thinking/tool sections, inline approvals, activity indicator,
  usage line, queue-and-flush input, and the read-only session list.
- `session-attachments`: the daemon upload endpoint, hub binding, and the
  embed-once rule for attached files.
- `session-folder-grants`: the grant model in `WorkingContext`, the
  `ScopedFileAccessPolicy` extension, grant lifetime, revocation, and the
  hard-deny precedence inside granted roots.

### Modified Capabilities

- `session-cwd`: granted folder roots join the declared project directory as
  session-owned trust inputs to path authorization; the spec's base
  resolution and safe-space rules must name the grant list.

## Impact

- **PRD linkage**: PRD-001 (MVP) is the base. The GUI plan document is the
  product source. PRD-003 (Ops Console) stays distinct and deferred. A PRD
  update for the GUI ships with this phase, because product behavior now
  lands.
- **Code**: `src/Netclaw.Gui` grows the product UI (views, viewmodels).
  `src/Netclaw.Daemon` gains the upload endpoint and grant plumbing.
  `Netclaw.Actors` extends `WorkingContext` and the session protocol where
  the grant list and attachment references live. `Netclaw.Client` gains the
  client calls for upload and grants.
- **Security**: folder grants widen filesystem authority per session.
  Default stays deny; a grant is explicit, session-scoped, and revocable;
  the hard-deny list and symlink guard stay authoritative inside granted
  roots. The upload endpoint accepts operator-authenticated loopback calls
  only, same exposure rules as the existing daemon API.
- **Tests**: the constitution's configuration contract applies — rejection
  before persistence, runtime enforcement matches persisted grants,
  immediate revocation, hard-deny precedence. GUI logic lands with viewmodel
  tests; native smoke coverage follows the Avalonia harness used in Phase 0.
- **Operations**: no new exposure, no config schema change expected; if a
  config key appears, `netclaw-config.v1.schema.json` updates in the same
  PR. CI cost grows with the new test surface.
- **Dependencies**: none new. AvaloniaEdit, TextMate, and grammar packages
  entered `Directory.Packages.props` in Phase 0.
