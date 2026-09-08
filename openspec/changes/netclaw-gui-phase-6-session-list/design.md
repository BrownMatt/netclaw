# Design: netclaw-gui-phase-6-session-list

## Context

See `proposal.md` — Why. Current state that shapes the design:

- `SessionCatalogEntryDto` already carries `CreatedAt` and `LastActivity`
  as unix milliseconds. The row item (`SessionListItemViewModel`) copies
  only `LastActivity` and shows neither.
- `SessionListViewModel.Load` clears and rebuilds the three collections
  on every call. The rename and delete dialogs hold row references
  (`RenameTarget`, `DeleteTarget`).
- `MainWindowViewModel` refreshes the list on connect, on attach, and
  after a management action. `MainWindow` already runs two
  `DispatcherTimer`s: 80 ms for delta flush and 5 s for diagnostics.
- An attach is two steps: `ResumeSessionAsync` returns, then
  `SessionJoined` arrives on the output stream and `RouteOutput` calls
  `Chat.LoadReplay`. Nothing marks the gap.
- The GUI never calls `CreateSession`. `DaemonClient.CreateSessionAsync`
  exists and the hub method exists; `IDaemonSessionService` does not
  expose it.
- Two `ListBox`es (pinned, unpinned) share a `SelectionChanged` handler.
  Each keeps its own selection, so two rows can show as selected.
- The GUI has no `TimeProvider`; the constitution requires one for any
  time computation.

## Goals / Non-Goals

**Goals:**

- Every new behavior lives in a viewmodel with a headless test. The view
  binds to it and adds no logic beyond focus and key routing.
- A periodic refresh is invisible when nothing changed: no flicker, no
  lost scroll position, no closed dialog.
- No new construct where an existing one carries the data: the row item
  already holds the catalog fields; the existing 5 s timer drives the
  refresh.

**Non-Goals:**

- No persisted sort or window state (Phase 8).
- No hub push for catalog changes; polling at 5 s is the mechanism.
- No change to the daemon, the client library, or the TUI.

## Decisions

### D1. New session through the existing client method

`IDaemonSessionService` gains `CreateSessionAsync()`, which calls
`DaemonClient.CreateSessionAsync(TuiChannelType)`. `MainWindowViewModel`
gets `NewSessionCommand`: it calls the service, then `AdoptSession(id)`,
sets `_sessionEnsured`, flushes the queue, and refreshes the list. The
window binds Ctrl+N to the command in `Window.KeyBindings`, so the
shortcut works with focus anywhere in the window. A failure sets
`Status` and leaves `Chat` untouched.

Alternative: create through `EnsureSession` with a fresh id. Rejected:
the daemon owns id generation, and `CreateSession` is the hub method the
TUI uses.

### D2. Relative times from an injected TimeProvider

`SessionListViewModel` takes a `TimeProvider` (the parameterless designer
constructor passes `TimeProvider.System`). The row item stores
`CreatedAt` and `LastActivity` and exposes `CreatedDisplay`,
`LastActivityDisplay`, and `TimesTooltip`. A static `RelativeTime.Format`
(`now`, `unixMs`) returns "just now", "N min ago", "N h ago", "N d ago",
or the short date past 30 days. The row recomputes its display text when
the list applies a refresh, so the relative text never goes more than one
refresh interval stale.

Alternative: a `DispatcherTimer` per row. Rejected: the 5 s refresh
already re-renders the rows; a second clock is a parallel construct.

### D3. The active mark is viewmodel state; ListBox selection is transient

`SessionListViewModel` keeps `_activeSessionId` and exposes
`SetActive(sessionId)`. It sets `IsActive` on exactly one row and clears
the rest, and `Load` re-applies the mark after a refresh.
`MainWindowViewModel` calls `SetActive` from `AdoptSession` and clears it
when the attached session is deleted.

The row highlight binds to `IsActive` (an accent bar and bold title
through a style class). The view clears the `ListBox` selection right
after it dispatches the attach, so the pinned and unpinned lists never
show two selections and a list reload cannot drop the highlight.

Alternative: bind `SelectedItem` on both lists and null the other on
change. Rejected: selection lives in the view and dies on every reload;
the mark must survive a reload.

### D4. Sort as a comparer applied inside each section

`SessionSortOrder { LastActivity, Created, Name, Type }` and an
observable `SortOrder` on `SessionListViewModel`. A comparer maps the
order: dates descending, name ordinal-ignore-case on `DisplayTitle`, type
by `Channel` then last activity descending. Both sections apply the same
comparer. A change of `SortOrder` re-sorts in place with
`ObservableCollection.Move`, so the row objects keep their identity. The
view offers a `ComboBox` in the pane header bound to `SortOrder`.

### D5. Reconciling load instead of rebuild

`Load(entries)` becomes a reconcile keyed by session id:

1. For each entry with a known id, update `Title`, `Pinned`,
   `LastActivity`, and the display texts on the existing row.
2. Add a row for each new id; remove rows whose id is absent.
3. Move rows between the pinned and unpinned sections when `Pinned`
   changed.
4. Re-apply the sort inside each section and re-apply the active mark.

Row objects survive a refresh, so `RenameTarget`, `DeleteTarget`, an open
context menu, and the scroll position are untouched. The daemon still
orders and filters; the GUI never invents a row.

### D6. Periodic refresh on the existing 5 s timer

`MainWindowViewModel.OnSessionListTimerTick()` refreshes the list when
the service reports connected and no refresh is in flight
(`_listRefreshing`). `MainWindow` calls it from the existing diagnostics
`DispatcherTimer` tick, next to the diagnostics tick. A failed refresh
keeps the current list; it sets `Status` only when the list is empty
(the existing rule). The interval constant is shared with
`DiagnosticsPaneViewModel.RefreshIntervalSeconds`.

Alternative: a `session_catalog_changed` hub event. Deferred: it needs a
daemon change and a new wire type for a list that changes a few times an
hour.

### D7. Loading state owned by the shell

`MainWindowViewModel.IsLoadingSession` turns on in `AdoptSession` and
off when `RouteOutput` receives `SessionJoined` for the attached session,
when the attach or create call throws, when the attached session is
deleted, and when the connection drops (the replay will not arrive on a
dead transport; the reconnect path adopts again). The history area
overlays an indeterminate `ProgressBar` and "Loading session..." while
the flag is on. `LoadReplay` with zero messages still routes through
`SessionJoined`, so an empty session ends the state with an empty
history.

The daemon can push `SessionJoined` on the stream before the ensure or
create call returns, so the join can arrive before the shell adopts the
id. `RouteOutput` keeps such a join in `_earlyJoins` by session id, and
`AdoptSession` applies it at once instead of the loading state. Test:
`Join_that_arrives_before_the_adopt_still_renders_and_ends_loading`.

### D8. Window title as a computed shell property

`MainWindowViewModel.WindowTitle` derives from the attached session's
list row (`DisplayTitle`), the application name, and the connection
state: "Deploy checklist - Netclaw", "signalr/… - Netclaw", or
"… - Netclaw (disconnected)". `ApplyTitle` and `HandleConnectionEvent`
raise the property change. The window binds `Title` to it.

## Risks / Trade-offs

- [The 5 s catalog poll adds load with many GUIs] → One request per GUI
  per interval, skipped while disconnected or in flight. The catalog
  query is indexed on `last_activity`.
- [A reconcile bug leaves a stale row] → The reconcile is pure viewmodel
  logic with tests for add, remove, update, section move, and identity
  retention.
- [`SessionJoined` never arrives after an attach] → The loading state
  also clears on connection loss and on attach failure; a wedged daemon
  shows the status line reason. No timeout is added, because the
  existing reconnect path re-adopts the session.
- [Ctrl+N fires while the composer has text] → The composer text is
  untouched; the new session attaches and the queued text stays in the
  box, the same as a click on another session today.
- [Relative time text drifts between refreshes] → At most one interval
  (5 s) of drift; the absolute tooltip is exact.

## Migration Plan

GUI-only. Deploy with `scripts/deploy-local.ps1 -Component gui`. Rollback
is the previous `netclaw-gui.exe`. No data or config migration.
