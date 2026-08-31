// -----------------------------------------------------------------------
// <copyright file="SessionListViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netclaw.Client;

namespace Netclaw.Gui.ViewModels;

/// <summary>One row in the session list.</summary>
public sealed partial class SessionListItemViewModel : ObservableObject
{
    public required string SessionId { get; init; }

    public required string Channel { get; init; }

    public long LastActivity { get; set; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _pinned;

    public bool Archived { get; init; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? SessionId : Title;

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
}

/// <summary>
/// The daemon operations behind the session context menu. A delegate record
/// so viewmodel tests substitute recorders without a live daemon.
/// </summary>
public sealed record SessionManagementActions(
    Func<string, string, Task> Rename,
    Func<string, bool, Task> SetPinned,
    Func<string, bool, Task> SetArchived,
    Func<string, Task> Delete,
    Func<Task> Refresh);

/// <summary>
/// Managed session list for the left pane: pinned sessions on top, unpinned
/// (and unarchived) sessions below, plus the context-menu actions. List
/// changes apply from daemon confirmations — after an ack the list refreshes
/// from the catalog; a rejection keeps the previous state and surfaces the
/// daemon's reason. Delete goes out only after the operator confirms the
/// dialog this viewmodel models as <see cref="DeleteTarget"/>.
/// </summary>
public sealed partial class SessionListViewModel : ObservableObject
{
    private static readonly SessionManagementActions NoActions = new(
        (_, _) => Task.CompletedTask,
        (_, _) => Task.CompletedTask,
        (_, _) => Task.CompletedTask,
        _ => Task.CompletedTask,
        () => Task.CompletedTask);

    private readonly SessionManagementActions _actions;

    public SessionListViewModel() : this(actions: null)
    {
    }

    public SessionListViewModel(SessionManagementActions? actions)
        => _actions = actions ?? NoActions;

    public ObservableCollection<SessionListItemViewModel> Sessions { get; } = [];

    public ObservableCollection<SessionListItemViewModel> PinnedSessions { get; } = [];

    public ObservableCollection<SessionListItemViewModel> UnpinnedSessions { get; } = [];

    public bool HasPinned => PinnedSessions.Count > 0;

    /// <summary>Rename dialog state; non-null while the dialog is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameOpen))]
    private SessionListItemViewModel? _renameTarget;

    [ObservableProperty]
    private string _renameText = string.Empty;

    public bool IsRenameOpen => RenameTarget is not null;

    /// <summary>Delete confirmation state; non-null while the dialog is open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeleteOpen))]
    private SessionListItemViewModel? _deleteTarget;

    public bool IsDeleteOpen => DeleteTarget is not null;

    /// <summary>The daemon's reason when a management action was rejected.</summary>
    [ObservableProperty]
    private string? _actionError;

    /// <summary>
    /// Replaces the list from a catalog load (the daemon already orders by
    /// last activity descending and excludes archived sessions by default).
    /// </summary>
    public void Load(IReadOnlyList<SessionCatalogEntryDto> entries)
    {
        Sessions.Clear();
        PinnedSessions.Clear();
        UnpinnedSessions.Clear();
        foreach (var entry in entries)
        {
            var item = new SessionListItemViewModel
            {
                SessionId = entry.SessionId,
                Channel = entry.Channel,
                LastActivity = entry.LastActivity,
                Title = entry.Title ?? string.Empty,
                Pinned = entry.Pinned,
                Archived = entry.Archived
            };
            Sessions.Add(item);
            if (item.Pinned)
                PinnedSessions.Add(item);
            else if (!item.Archived)
                UnpinnedSessions.Add(item);
        }

        OnPropertyChanged(nameof(HasPinned));
    }

    /// <summary>Applies a live title event without a manual refresh.</summary>
    public void ApplyTitle(string sessionId, string title)
    {
        var item = Sessions.FirstOrDefault(s =>
            string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        if (item is not null)
            item.Title = title;
    }

    [RelayCommand]
    private void BeginRename(SessionListItemViewModel? item)
    {
        if (item is null)
            return;

        ActionError = null;
        RenameTarget = item;
        RenameText = item.DisplayTitle;
    }

    [RelayCommand]
    private async Task ConfirmRenameAsync()
    {
        if (RenameTarget is not { } target)
            return;

        await RunActionAsync(
            () => _actions.Rename(target.SessionId, RenameText),
            onSuccess: () => RenameTarget = null);
    }

    [RelayCommand]
    private void CancelRename() => RenameTarget = null;

    [RelayCommand]
    private Task TogglePinAsync(SessionListItemViewModel? item)
        => item is null
            ? Task.CompletedTask
            : RunActionAsync(() => _actions.SetPinned(item.SessionId, !item.Pinned));

    [RelayCommand]
    private Task ArchiveAsync(SessionListItemViewModel? item)
        => item is null
            ? Task.CompletedTask
            : RunActionAsync(() => _actions.SetArchived(item.SessionId, true));

    [RelayCommand]
    private void BeginDelete(SessionListItemViewModel? item)
    {
        if (item is null)
            return;

        ActionError = null;
        DeleteTarget = item;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (DeleteTarget is not { } target)
            return;

        await RunActionAsync(
            () => _actions.Delete(target.SessionId),
            onSuccess: () => DeleteTarget = null);
    }

    [RelayCommand]
    private void CancelDelete() => DeleteTarget = null;

    // Confirmation-driven updates: the list changes only through the
    // post-ack refresh, never by patching local state optimistically. A
    // rejection keeps the current list and shows the daemon's reason.
    private async Task RunActionAsync(Func<Task> action, Action? onSuccess = null)
    {
        ActionError = null;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            ActionError = ex.Message;
            return;
        }

        onSuccess?.Invoke();
        try
        {
            await _actions.Refresh();
        }
        catch (Exception ex)
        {
            ActionError = ex.Message;
        }
    }
}
