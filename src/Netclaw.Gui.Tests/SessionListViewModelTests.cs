// -----------------------------------------------------------------------
// <copyright file="SessionListViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Client;
using Netclaw.Gui.ViewModels;
using Xunit;

namespace Netclaw.Gui.Tests;

/// <summary>
/// Managed session list: pinned/unpinned sections, delete only after the
/// confirmation, confirmation-driven refresh (no optimistic patch), and
/// rejection surfacing that keeps the previous list state.
/// </summary>
public sealed class SessionListViewModelTests
{
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

    private static SessionCatalogEntryDto Entry(string id, bool pinned = false, bool archived = false, string? title = null) => new()
    {
        PersistenceId = $"session-{id}",
        Channel = "tui",
        Status = "inactive",
        TurnCount = 1,
        CreatedAt = 1,
        LastActivity = 2,
        Title = title,
        Pinned = pinned,
        Archived = archived
    };

    [Fact]
    public void Load_splits_pinned_and_unpinned_sections()
    {
        var vm = new SessionListViewModel(new RecordingActions().Build());

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
        var vm = new SessionListViewModel(actions.Build());
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
        var vm = new SessionListViewModel(actions.Build());
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
        var vm = new SessionListViewModel(actions.Build());
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
        var vm = new SessionListViewModel(actions.Build());
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
        var vm = new SessionListViewModel(actions.Build());
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
}
