# Proposal: netclaw-gui-phase-4-right-pane

## Why

The GUI has no diagnostics surface. The operator must switch to a
terminal to read the daemon log, a session log, the running models, or
the MCP status. GUI roadmap Phase 4
(`docs/netclaw/plans/netclaw-gui-plan.md`) makes the right pane the next
deliverable, with the exit criterion "the three-pane design is
complete". The daemon already serves status, stats, and MCP data; it has
no log API and no running-models API.

## What Changes

- Add daemon log tail endpoints: the daemon log and a per-session log,
  with a capped tail window and shared-mode file reads, because the
  daemon holds the log files open.
- Add a daemon running-models listing composed per provider (the Ollama
  `/api/ps` data: loaded models, size, and expiry). Providers without
  the concept report no entries.
- GUI: add a collapsed-by-default right pane with one view at a time —
  Logs, Running models, Status, Stats, and MCP — with auto refresh and a
  visible refresh interval.

## Capabilities

### New Capabilities

- `log-tail-api`: operator-authenticated read access to the daemon log
  and per-session logs — tail windows, caps, and the shared-read
  contract.
- `gui-diagnostics`: the right pane — view selection, auto refresh, and
  failure behavior.

### Modified Capabilities

- `model-catalog`: add the running-models listing next to the existing
  catalog listing.

## Impact

- **PRD linkage:** no PRD requirement covers a GUI diagnostics pane; the
  specs in this change are the authority (same posture as Phase 3).
- **Plan divergence:** the roadmap sketches the running-models view as a
  GUI-side OllamaSharp call. This change puts it in the daemon instead,
  for the Phase 2 reasons: the daemon owns provider config, and a
  remote-paired GUI cannot always reach the Ollama host. The roadmap
  also sketches the session log route with the id as a path segment;
  session ids contain `/`, so the route takes the id as a query
  parameter (the Phase 3 convention).
- **Code:** daemon log endpoints plus a log tail reader, a
  running-models probe on the Ollama descriptor
  (`Netclaw.Providers`), a `/api/models/running` endpoint,
  `Netclaw.Client` methods, and the GUI right pane (views, viewmodels,
  refresh loop).
- **Security:** all new endpoints require operator authentication. Log
  paths derive from daemon-owned configuration and the sanitized
  session id — never from client-supplied paths. The tail cap bounds
  the response size.
- **Operations:** the right pane gives the operator log and MCP
  visibility without terminal access to the daemon host.
- **Out of scope:** log search or download, log streaming over SignalR,
  model pull or delete, historical stats charts, and TUI parity for the
  new views.
