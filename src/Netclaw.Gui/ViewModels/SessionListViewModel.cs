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

/// <summary>Sort orders for the session list. Each order applies inside a section.</summary>
public enum SessionSortOrder
{
    LastActivity,
    Created,
    Name,
    Type
}

/// <summary>One entry of the sort control.</summary>
public sealed record SessionSortOption(SessionSortOrder Order, string Label);

/// <summary>Relative and absolute time text for list rows.</summary>
public static class RelativeTime
{
    /// <summary>
    /// "just now", "N min ago", "N h ago", "N d ago", or the local date past
    /// 30 days. A timestamp in the future (clock skew) reads as "just now".
    /// </summary>
    public static string Format(DateTimeOffset now, long unixMs)
    {
        var age = now - DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return $"{(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromDays(1))
            return $"{(int)age.TotalHours} h ago";
        if (age < TimeSpan.FromDays(30))
            return $"{(int)age.TotalDays} d ago";
        return Absolute(unixMs, "yyyy-MM-dd");
    }

    public static string Absolute(long unixMs, string format = "yyyy-MM-dd HH:mm")
        => DateTimeOffset.FromUnixTimeMilliseconds(unixMs).ToLocalTime().ToString(format);
}

/// <summary>One row in the session list.</summary>
public sealed partial class SessionListItemViewModel : ObservableObject
{
    public required string SessionId { get; init; }

    public required string Channel { get; init; }

    public long CreatedAt { get; set; }

    public long LastActivity { get; set; }

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private bool _pinned;

    public bool Archived { get; set; }

    /// <summary>True on the attached session's row only.</summary>
    [ObservableProperty]
    private bool _isActive;

    [ObservableProperty]
    private string _createdDisplay = string.Empty;

    [ObservableProperty]
    private string _lastActivityDisplay = string.Empty;

    [ObservableProperty]
    private string _timesTooltip = string.Empty;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? SessionId : Title;

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));

    /// <summary>Recomputes the relative texts against <paramref name="now"/>.</summary>
    public void RefreshTimes(DateTimeOffset now)
    {
        CreatedDisplay = $"created {RelativeTime.Format(now, CreatedAt)}";
        LastActivityDisplay = $"active {RelativeTime.Format(now, LastActivity)}";
        TimesTooltip = $"Created {RelativeTime.Absolute(CreatedAt)}\nLast activity {RelativeTime.Absolute(LastActivity)}";
    }
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
/// A catalog load reconciles rows by session id instead of rebuilding the
/// list, so a periodic refresh keeps row identity: an open dialog, the
/// context menu, and the scroll position survive it.
/// </summary>
public sealed partial class SessionListViewModel : ObservableObject
{
    public static IReadOnlyList<SessionSortOption> SortOptions { get; } =
    [
        new(SessionSortOrder.LastActivity, "Last activity"),
        new(SessionSortOrder.Created, "Created"),
        new(SessionSortOrder.Name, "Name"),
        new(SessionSortOrder.Type, "Type")
    ];

    private static readonly SessionManagementActions NoActions = new(
        (_, _) => Task.CompletedTask,
        (_, _) => Task.CompletedTask,
        (_, _) => Task.CompletedTask,
        _ => Task.CompletedTask,
        () => Task.CompletedTask);

    private readonly SessionManagementActions _actions;
    private readonly TimeProvider _timeProvider;
    private string? _activeSessionId;

    public SessionListViewModel() : this(NoActions, TimeProvider.System)
    {
    }

    public SessionListViewModel(SessionManagementActions actions, TimeProvider timeProvider)
    {
        _actions = actions;
        _timeProvider = timeProvider;
    }

    public ObservableCollection<SessionListItemViewModel> Sessions { get; } = [];

    public ObservableCollection<SessionListItemViewModel> PinnedSessions { get; } = [];

    public ObservableCollection<SessionListItemViewModel> UnpinnedSessions { get; } = [];

    public bool HasPinned => PinnedSessions.Count > 0;

    /// <summary>The order applied inside each section.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedSortOption))]
    private SessionSortOrder _sortOrder = SessionSortOrder.LastActivity;

    /// <summary>The sort control's selection; maps onto <see cref="SortOrder"/>.</summary>
    public SessionSortOption SelectedSortOption
    {
        get => SortOptions.First(option => option.Order == SortOrder);
        set => SortOrder = value.Order;
    }

    partial void OnSortOrderChanged(SessionSortOrder value) => ReconcileSections();

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
    /// Applies a catalog load. Rows are reconciled by session id: existing
    /// rows update in place, new rows are added, absent rows leave, a pin
    /// change moves the row between sections, and the sort and the active
    /// mark are re-applied. The daemon still orders and filters; the GUI
    /// never invents a row.
    /// </summary>
    public void Load(IReadOnlyList<SessionCatalogEntryDto> entries)
    {
        var now = _timeProvider.GetUtcNow();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            seen.Add(entry.SessionId);
            var item = Find(entry.SessionId);
            if (item is null)
            {
                item = new SessionListItemViewModel
                {
                    SessionId = entry.SessionId,
                    Channel = entry.Channel
                };
                Sessions.Add(item);
            }

            item.Title = entry.Title ?? string.Empty;
            item.CreatedAt = entry.CreatedAt;
            item.LastActivity = entry.LastActivity;
            item.Pinned = entry.Pinned;
            item.Archived = entry.Archived;
            item.IsActive = string.Equals(item.SessionId, _activeSessionId, StringComparison.Ordinal);
            item.RefreshTimes(now);
        }

        for (var i = Sessions.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Sessions[i].SessionId))
                Sessions.RemoveAt(i);
        }

        ReconcileSections();
    }

    /// <summary>Applies a live title event without a manual refresh.</summary>
    public void ApplyTitle(string sessionId, string title)
    {
        var item = Find(sessionId);
        if (item is null)
            return;

        item.Title = title;
        if (SortOrder == SessionSortOrder.Name)
            ReconcileSections();
    }

    /// <summary>
    /// Marks the attached session. Exactly one row carries the mark, across
    /// both sections; <c>null</c> clears it. A later load re-applies it.
    /// </summary>
    public void SetActive(string? sessionId)
    {
        _activeSessionId = sessionId;
        foreach (var item in Sessions)
            item.IsActive = string.Equals(item.SessionId, sessionId, StringComparison.Ordinal);
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

    private SessionListItemViewModel? Find(string sessionId)
        => Sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));

    // Rebuilds the section order from Sessions with in-place moves so the
    // row objects keep their identity (bound dialogs, menus, and scroll).
    private void ReconcileSections()
    {
        var comparer = Comparer<SessionListItemViewModel>.Create(CompareFor(SortOrder));
        SyncSection(PinnedSessions, Sessions.Where(s => s.Pinned).OrderBy(s => s, comparer).ToList());
        SyncSection(UnpinnedSessions, Sessions.Where(s => !s.Pinned && !s.Archived).OrderBy(s => s, comparer).ToList());
        OnPropertyChanged(nameof(HasPinned));
    }

    private static Comparison<SessionListItemViewModel> CompareFor(SessionSortOrder order) => order switch
    {
        SessionSortOrder.Created => (a, b) => b.CreatedAt.CompareTo(a.CreatedAt),
        SessionSortOrder.Name => (a, b) =>
        {
            var byName = string.Compare(a.DisplayTitle, b.DisplayTitle, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : b.LastActivity.CompareTo(a.LastActivity);
        },
        SessionSortOrder.Type => (a, b) =>
        {
            var byType = string.Compare(a.Channel, b.Channel, StringComparison.OrdinalIgnoreCase);
            return byType != 0 ? byType : b.LastActivity.CompareTo(a.LastActivity);
        },
        _ => (a, b) => b.LastActivity.CompareTo(a.LastActivity)
    };

    private static void SyncSection(
        ObservableCollection<SessionListItemViewModel> section,
        List<SessionListItemViewModel> desired)
    {
        for (var i = section.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(section[i]))
                section.RemoveAt(i);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var want = desired[i];
            if (i < section.Count && ReferenceEquals(section[i], want))
                continue;

            var current = section.IndexOf(want);
            if (current < 0)
                section.Insert(i, want);
            else
                section.Move(current, i);
        }
    }

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
