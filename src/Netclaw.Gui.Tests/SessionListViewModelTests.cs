// -----------------------------------------------------------------------
// <copyright file="SessionListViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Client;
using Netclaw.Gui.ViewModels;
using Xunit;

namespace Netclaw.Gui.Tests;

/// <summary>
/// Managed session list: pinned/unpinned sections, delete only after the
/// confirmation, confirmation-driven refresh (no optimistic patch),
/// rejection surfacing that keeps the previous list state, relative row
/// times, sort inside each section, reconciling loads that keep row
/// identity, and the exclusive active mark.
/// </summary>
public sealed class SessionListViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);

    private sealed class RecordingActions
    {
        public List<(string SessionId, string Title)> Renames { get; } = [];

        public List<(string SessionId, bool Pinned)> Pins { get; } = [];

        public List<(string SessionId, bool Archived)> Archives { get; } = [];

        public List<string> Deletes { get; } = [];

        public int Refreshes { get; private set; }

        public Exception? Failure { get; set; }

        public SessionManagementActions Build() => new(
            Rename: (id, title) => Run(() => Renames.Add((id, title))),
            SetPinned: (id, pinned) => Run(() => Pins.Add((id, pinned))),
            SetArchived: (id, archived) => Run(() => Archives.Add((id, archived))),
            Delete: id => Run(() => Deletes.Add(id)),
            Refresh: () =>
            {
                Refreshes++;
                return Task.CompletedTask;
            });

        private Task Run(Action record)
        {
            if (Failure is { } failure)
                return Task.FromException(failure);
            record();
            return Task.CompletedTask;
        }
    }

    private static long Ago(TimeSpan age) => (Now - age).ToUnixTimeMilliseconds();

    private static SessionCatalogEntryDto Entry(
        string id,
        bool pinned = false,
        bool archived = false,
        string? title = null,
        string channel = "tui",
        long createdAt = 1,
        long lastActivity = 2) => new()
    {
        PersistenceId = $"session-{id}",
        Channel = channel,
        Status = "inactive",
        TurnCount = 1,
        CreatedAt = createdAt,
        LastActivity = lastActivity,
        Title = title,
        Pinned = pinned,
        Archived = archived
    };

    private SessionListViewModel Create(RecordingActions? actions = null)
        => new((actions ?? new RecordingActions()).Build(), _clock);

    [Fact]
    public void Load_splits_pinned_and_unpinned_sections()
    {
        var vm = Create();

        vm.Load([
            Entry("signalr/pinned", pinned: true),
            Entry("signalr/plain"),
            Entry("signalr/archived", archived: true)
        ]);

        Assert.Equal("signalr/pinned", Assert.Single(vm.PinnedSessions).SessionId);
        Assert.Equal("signalr/plain", Assert.Single(vm.UnpinnedSessions).SessionId);
        Assert.True(vm.HasPinned);
    }

    [Fact]
    public async Task Delete_sends_only_after_the_confirmation()
    {
        var actions = new RecordingActions();
        var vm = Create(actions);
        vm.Load([Entry("signalr/doomed", title: "Doomed")]);
        var item = Assert.Single(vm.UnpinnedSessions);

        vm.BeginDeleteCommand.Execute(item);
        Assert.True(vm.IsDeleteOpen);
        Assert.Empty(actions.Deletes);

        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.Equal("signalr/doomed", Assert.Single(actions.Deletes));
        Assert.False(vm.IsDeleteOpen);
        Assert.Equal(1, actions.Refreshes);
    }

    [Fact]
    public void Cancelled_delete_sends_nothing()
    {
        var actions = new RecordingActions();
        var vm = Create(actions);
        vm.Load([Entry("signalr/kept")]);

        vm.BeginDeleteCommand.Execute(vm.UnpinnedSessions[0]);
        vm.CancelDeleteCommand.Execute(null);

        Assert.False(vm.IsDeleteOpen);
        Assert.Empty(actions.Deletes);
        Assert.Equal(0, actions.Refreshes);
    }

    [Fact]
    public async Task Rejected_action_keeps_the_list_and_surfaces_the_reason()
    {
        var actions = new RecordingActions
        {
            Failure = new InvalidOperationException("Session 'signalr/x' not found.")
        };
        var vm = Create(actions);
        vm.Load([Entry("signalr/x", title: "Before")]);

        vm.BeginRenameCommand.Execute(vm.UnpinnedSessions[0]);
        vm.RenameText = "After";
        await vm.ConfirmRenameCommand.ExecuteAsync(null);

        Assert.Contains("not found", vm.ActionError);
        // The dialog stays open and the list did not change — no refresh, no patch.
        Assert.True(vm.IsRenameOpen);
        Assert.Equal("Before", vm.UnpinnedSessions[0].Title);
        Assert.Equal(0, actions.Refreshes);
    }

    [Fact]
    public async Task Rename_confirm_sends_the_text_and_refreshes()
    {
        var actions = new RecordingActions();
        var vm = Create(actions);
        vm.Load([Entry("signalr/renamed")]);

        vm.BeginRenameCommand.Execute(vm.UnpinnedSessions[0]);
        vm.RenameText = "Fresh title";
        await vm.ConfirmRenameCommand.ExecuteAsync(null);

        Assert.Equal(("signalr/renamed", "Fresh title"), Assert.Single(actions.Renames));
        Assert.False(vm.IsRenameOpen);
        Assert.Equal(1, actions.Refreshes);
    }

    [Fact]
    public async Task Toggle_pin_inverts_the_current_state_and_archive_sets_true()
    {
        var actions = new RecordingActions();
        var vm = Create(actions);
        vm.Load([Entry("signalr/pinned", pinned: true), Entry("signalr/plain")]);

        await vm.TogglePinCommand.ExecuteAsync(vm.PinnedSessions[0]);
        await vm.TogglePinCommand.ExecuteAsync(vm.UnpinnedSessions[0]);
        await vm.ArchiveCommand.ExecuteAsync(vm.UnpinnedSessions[0]);

        Assert.Equal(
            [("signalr/pinned", false), ("signalr/plain", true)],
            actions.Pins);
        Assert.Equal(("signalr/plain", true), Assert.Single(actions.Archives));
        Assert.Equal(3, actions.Refreshes);
    }

    // ── Row times (D2) ───────────────────────────────────────────────

    [Theory]
    [InlineData(30, "just now")]
    [InlineData(2 * 60, "2 min ago")]
    [InlineData(3 * 3600, "3 h ago")]
    [InlineData(2 * 86400, "2 d ago")]
    [InlineData(-120, "just now")] // clock skew: a future stamp is not negative
    public void Relative_time_formats_by_age(int ageSeconds, string expected)
        => Assert.Equal(expected, RelativeTime.Format(Now, Ago(TimeSpan.FromSeconds(ageSeconds))));

    [Fact]
    public void Relative_time_past_thirty_days_is_the_date()
    {
        var stamp = Ago(TimeSpan.FromDays(45));
        var expected = DateTimeOffset.FromUnixTimeMilliseconds(stamp).ToLocalTime().ToString("yyyy-MM-dd");

        Assert.Equal(expected, RelativeTime.Format(Now, stamp));
    }

    [Fact]
    public void Load_sets_the_row_times_and_the_next_load_refreshes_them()
    {
        var vm = Create();
        var entry = Entry("signalr/a",
            createdAt: Ago(TimeSpan.FromHours(3)),
            lastActivity: Ago(TimeSpan.FromMinutes(2)));

        vm.Load([entry]);
        var row = Assert.Single(vm.UnpinnedSessions);
        Assert.Equal("created 3 h ago", row.CreatedDisplay);
        Assert.Equal("active 2 min ago", row.LastActivityDisplay);
        Assert.Contains("Created ", row.TimesTooltip);
        Assert.Contains("Last activity ", row.TimesTooltip);

        _clock.Advance(TimeSpan.FromMinutes(10));
        vm.Load([entry]);

        Assert.Same(row, Assert.Single(vm.UnpinnedSessions));
        Assert.Equal("active 12 min ago", row.LastActivityDisplay);
    }

    // ── Sort (D4) ────────────────────────────────────────────────────

    [Fact]
    public void Name_sort_orders_each_section()
    {
        var vm = Create();
        vm.Load([
            Entry("signalr/1", pinned: true, title: "Zeta"),
            Entry("signalr/2", pinned: true, title: "Alpha"),
            Entry("signalr/3", title: "Mid"),
            Entry("signalr/4", title: "Beta")
        ]);

        vm.SortOrder = SessionSortOrder.Name;

        Assert.Equal(["Alpha", "Zeta"], vm.PinnedSessions.Select(s => s.Title));
        Assert.Equal(["Beta", "Mid"], vm.UnpinnedSessions.Select(s => s.Title));
        Assert.Equal(SessionSortOrder.Name, vm.SelectedSortOption.Order);
    }

    [Fact]
    public void Date_sorts_descend_and_type_sort_groups_by_channel()
    {
        var vm = Create();
        vm.Load([
            Entry("signalr/old", channel: "tui", createdAt: 10, lastActivity: 300),
            Entry("signalr/new", channel: "tui", createdAt: 30, lastActivity: 100),
            Entry("signalr/slack", channel: "slack", createdAt: 20, lastActivity: 200)
        ]);

        // Default: last activity descending.
        Assert.Equal(["signalr/old", "signalr/slack", "signalr/new"], vm.UnpinnedSessions.Select(s => s.SessionId));

        vm.SelectedSortOption = SessionListViewModel.SortOptions.Single(o => o.Order == SessionSortOrder.Created);
        Assert.Equal(["signalr/new", "signalr/slack", "signalr/old"], vm.UnpinnedSessions.Select(s => s.SessionId));

        vm.SortOrder = SessionSortOrder.Type;
        Assert.Equal(["signalr/slack", "signalr/old", "signalr/new"], vm.UnpinnedSessions.Select(s => s.SessionId));
    }

    [Fact]
    public void Sort_change_keeps_row_identity()
    {
        var vm = Create();
        vm.Load([Entry("signalr/b", title: "B", lastActivity: 2), Entry("signalr/a", title: "A", lastActivity: 1)]);
        var rowA = vm.UnpinnedSessions.Single(s => s.SessionId == "signalr/a");
        var rowB = vm.UnpinnedSessions.Single(s => s.SessionId == "signalr/b");

        vm.SortOrder = SessionSortOrder.Name;

        Assert.Same(rowA, vm.UnpinnedSessions[0]);
        Assert.Same(rowB, vm.UnpinnedSessions[1]);
    }

    // ── Reconciling load (D5) ────────────────────────────────────────

    [Fact]
    public void Load_reconciles_rows_in_place()
    {
        var vm = Create();
        vm.Load([
            Entry("signalr/keep", title: "Keep"),
            Entry("signalr/gone", title: "Gone"),
            Entry("signalr/moves", title: "Moves")
        ]);
        var keep = vm.UnpinnedSessions.Single(s => s.SessionId == "signalr/keep");
        var moves = vm.UnpinnedSessions.Single(s => s.SessionId == "signalr/moves");
        vm.BeginRenameCommand.Execute(keep);

        vm.Load([
            Entry("signalr/keep", title: "Kept"),
            Entry("signalr/moves", title: "Moves", pinned: true),
            Entry("signalr/added", title: "Added")
        ]);

        // The open dialog still points at the same row object, now retitled.
        Assert.True(vm.IsRenameOpen);
        Assert.Same(keep, vm.RenameTarget);
        Assert.Equal("Kept", keep.Title);
        // Removed session left; pin change moved the same object; new row added.
        Assert.DoesNotContain(vm.Sessions, s => s.SessionId == "signalr/gone");
        Assert.Same(moves, Assert.Single(vm.PinnedSessions));
        Assert.DoesNotContain(vm.UnpinnedSessions, s => s.SessionId == "signalr/moves");
        Assert.Contains(vm.UnpinnedSessions, s => s.SessionId == "signalr/added");
        Assert.Equal(3, vm.Sessions.Count);
    }

    // ── Active mark (D3) ─────────────────────────────────────────────

    [Fact]
    public void Active_mark_is_exclusive_across_sections_and_survives_a_load()
    {
        var vm = Create();
        var entries = new[] { Entry("signalr/pinned", pinned: true), Entry("signalr/plain") };
        vm.Load(entries);

        vm.SetActive("signalr/pinned");
        Assert.Equal("signalr/pinned", Assert.Single(vm.Sessions, s => s.IsActive).SessionId);

        vm.SetActive("signalr/plain");
        Assert.Equal("signalr/plain", Assert.Single(vm.Sessions, s => s.IsActive).SessionId);

        vm.Load(entries);
        Assert.Equal("signalr/plain", Assert.Single(vm.Sessions, s => s.IsActive).SessionId);

        vm.SetActive(null);
        Assert.DoesNotContain(vm.Sessions, s => s.IsActive);
    }
}
