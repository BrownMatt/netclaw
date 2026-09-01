# Design: netclaw-gui-phase-4-right-pane

## Context

See `proposal.md` — Why. Current state that shapes the design:

- The daemon already serves `GET /api/health/status`, `GET /api/stats`,
  `GET /api/stats/skills`, `GET /api/mcp/statuses`, and
  `GET /api/mcp/tools/{name}` (`src/Netclaw.Daemon/Program.cs`,
  `McpEndpointRouteBuilderExtensions.cs`). No log API and no
  running-models API exist.
- The daemon log is a date-rolled file `daemon-YYYY-MM-DD.log` in
  `NetclawPaths.LogsDirectory`
  (`src/Netclaw.Daemon/Configuration/LoggingRegistrationExtensions.cs`).
  The daemon holds the current file open for writes. Session logs live
  at `SessionLogFile.GetLogPath(sessionId, paths.SessionLogsDirectory)`
  and are held open by `SessionLogActor`.
- `ModelCatalogService` composes `IProviderDescriptor` probes per
  configured provider (parallel, 10 s per provider) and reports a
  failed provider with a reason. `OllamaDescriptor` uses a raw
  `HttpClient` against `/api/tags` — no OllamaSharp dependency.
  Ollama's `/api/ps` returns loaded models with size and expiry.
- Session ids contain `/`, so management routes take the id as a query
  parameter (Phase 3 D3).
- The GUI shell is `MainWindowViewModel`; feature actions reach services
  through delegate records for headless testability
  (`SessionManagementActions` pattern).

## Goals / Non-Goals

**Goals:**

- Logs, running models, status, stats, and MCP state visible in the GUI,
  local or remote-paired, without daemon-host filesystem access.
- Log reads never disturb the daemon's open write handles.
- A collapsed pane costs nothing.

**Non-Goals:**

- No log search, download, or streaming push. No model pull or delete.
- No historical charts; the views render current endpoint responses.
- No TUI parity for the new views.

## Decisions

### D1. Log tail endpoints read backward with shared access

Routes (operator-authenticated, ids as query parameters):

- `GET /api/logs/daemon?tail=N`
- `GET /api/logs/session?sessionId={id}&tail=N`

`N` defaults to 500 and is capped at 2000. A new `LogTailReader` opens
the file with `FileShare.ReadWrite | FileShare.Delete` — the modes the
daemon's own writers require — and scans backward in fixed-size blocks
from the end until it has N lines or hits the start, so a large log
never streams through memory whole. A partial first line from a
mid-write read is tolerated (the next refresh completes it). The daemon
log path resolves to the newest `daemon-*.log` in
`NetclawPaths.LogsDirectory`; the session log path resolves through
`SessionLogFile.GetLogPath` from the sanitized id. The endpoint returns
the resolved file name and the lines; a missing file is 404. Client
input never contributes a path segment.

Rationale: backward block scan bounds cost by the requested window, not
the file size. Alternative — forward scan with a ring buffer — reads
the whole file on every poll.

Data classification: call-local. Nothing persists.

### D2. Running models is a provider probe behind an optional interface

A new `IRunningModelsProbe` interface with
`ProbeRunningAsync(CancellationToken)`; `OllamaDescriptor` implements it
against `/api/ps` with its existing `HttpClient`. The catalog
composition layer checks `descriptor is IRunningModelsProbe`: a
provider that does not implement it contributes nothing and is not
failed; a provider that implements it and throws appears failed with a
reason (the catalog's failure contract). `GET /api/models/running`
serves the composed listing with no cache — running state changes with
every load/unload, and the GUI polls at its own interval.

Rationale: an optional interface models "this provider has no such
concept" without a nullable dependency or a default method that hides
absence. This is a type-level absence, not a null check on a security
path.

### D3. The right pane is a view-switching viewmodel with one timer

`DiagnosticsPaneViewModel` owns: `IsExpanded` (starts false), the
selected view (enum: Logs, RunningModels, Status, Stats, Mcp), the
per-view state objects, and one refresh timer. The timer ticks only
while the pane is expanded; collapse stops it — the collapsed pane
sends no requests. Each tick refreshes only the visible view. The
refresh interval is a constant shown in the pane header. Service calls
go through a `DiagnosticsActions` delegate record (the
`SessionManagementActions` pattern), so every behavior is headlessly
testable. The Avalonia timer wraps through `IUiDispatcher`-compatible
abstraction; tests drive ticks directly.

A failed refresh sets a per-view error string and keeps the last loaded
data; the next tick retries. The Logs view holds a source toggle
(daemon / attached session); the session option binds to
`MainWindowViewModel.Chat` — it disables when `Chat` is null and
re-targets when the attached session changes. The Running models view
marks the entry matching the attached session's effective model
(override when set, configured default otherwise), matched by provider
key and model id; an untagged Ollama id matches its ":latest" tag,
because /api/ps reports the tag while a configured model can omit it
(found in the E2E smoke). The status endpoint is the only place the GUI
can learn the configured model, so the Running models refresh also reads
status as an input — one extra GET per tick, on that view only; the
collapsed-pane and visible-view-only contracts are untouched. The mark
is GUI-side only.

### D4. Client surface mirrors the endpoints one-to-one

`DaemonApi` gains `GetDaemonLogTailAsync`, `GetSessionLogTailAsync`,
`GetRunningModelsAsync`, `GetDaemonStatusAsync`, `GetStatsAsync`, and
`GetMcpStatusesAsync` — thin typed GETs over the existing authenticated
`HttpClient`, DTOs matching the wire shape. No polling logic lives in
the client; the GUI owns cadence (D3).

## Risks / Trade-offs

- [Daemon log rolls at midnight] → the endpoint re-resolves the newest
  `daemon-*.log` per request, so a roll just switches files on the next
  refresh.
- [Reading a file mid-write yields a torn last line] → tolerated; tail
  output is display-only and self-heals on the next refresh.
- [Polling five endpoints could load the daemon] → only the visible
  view refreshes, the collapsed pane is idle, and the tail window is
  capped.
- [Session log endpoint could probe arbitrary ids] → the path derives
  from the sanitized id inside the session logs directory, unknown ids
  404, and the route is operator-authenticated; log content is exactly
  what the operator can already read over the session.
- [`/api/ps` shape drift across Ollama versions] → the probe maps only
  name, size, and expiry; absent fields map to absent, not to failure.
