// -----------------------------------------------------------------------
// <copyright file="SessionListViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Netclaw.Client;

namespace Netclaw.Gui.ViewModels;

/// <summary>One row in the read-only session list.</summary>
public sealed partial class SessionListItemViewModel : ObservableObject
{
    public required string SessionId { get; init; }

    public required string Channel { get; init; }

    public long LastActivity { get; set; }

    [ObservableProperty]
    private string _title = string.Empty;

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? SessionId : Title;

    partial void OnTitleChanged(string value) => OnPropertyChanged(nameof(DisplayTitle));
}

/// <summary>
/// Read-only session list for the left pane: loads the daemon catalog on
/// start and reconnect, applies live <c>session_title</c> events, and offers
/// no mutation actions in this phase.
/// </summary>
public sealed class SessionListViewModel : ObservableObject
{
    public ObservableCollection<SessionListItemViewModel> Sessions { get; } = [];

    /// <summary>
    /// Replaces the list from a catalog load, ordered by activity (the
    /// daemon already orders by last activity descending).
    /// </summary>
    public void Load(IReadOnlyList<SessionCatalogEntryDto> entries)
    {
        Sessions.Clear();
        foreach (var entry in entries)
        {
            Sessions.Add(new SessionListItemViewModel
            {
                SessionId = entry.SessionId,
                Channel = entry.Channel,
                LastActivity = entry.LastActivity,
                Title = entry.Title ?? string.Empty
            });
        }
    }

    /// <summary>Applies a live title event without a manual refresh.</summary>
    public void ApplyTitle(string sessionId, string title)
    {
        var item = Sessions.FirstOrDefault(s =>
            string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        if (item is not null)
            item.Title = title;
    }
}
