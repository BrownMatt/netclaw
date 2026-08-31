// -----------------------------------------------------------------------
// <copyright file="MainWindowViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netclaw.Actors.Protocol;
using Netclaw.Client;
using Netclaw.Gui.Services;
using R3;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Gui.ViewModels;

/// <summary>
/// Marshals work onto the UI thread. An interface so viewmodel tests run
/// callbacks inline without an Avalonia dispatcher.
/// </summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Name chip for an uploaded file that rides the next message.</summary>
public sealed record PendingAttachmentChip(string AttachmentId, string FileName);

/// <summary>
/// The application shell: connection lifecycle, the managed session list,
/// the attached chat session, queue-and-flush input, and the <c>+</c> flows
/// (attach file, grant folder). All session-output handling is delegated to
/// <see cref="ChatSessionViewModel"/>; this class owns routing and transport.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(3);

    private readonly IDaemonSessionService _service;
    private readonly IUiDispatcher _dispatcher;
    private readonly string _endpoint;
    private readonly List<string> _queue = [];
    private bool _recovering;
    private bool _connectLoopRunning;
    private readonly IDisposable _outputSubscription;
    private readonly IDisposable _connectionSubscription;
    private bool _sessionEnsured;

    [ObservableProperty]
    private string _status = "Connecting...";

    [ObservableProperty]
    private string _inputText = string.Empty;

    [ObservableProperty]
    private ChatSessionViewModel? _chat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueued))]
    private int _queuedCount;

    public bool HasQueued => QueuedCount > 0;

    public SessionListViewModel SessionList { get; }

    public ObservableCollection<PendingAttachmentChip> PendingAttachments { get; } = [];

    public MainWindowViewModel(IDaemonSessionService service, IUiDispatcher dispatcher, string endpoint)
    {
        _service = service;
        _dispatcher = dispatcher;
        _endpoint = endpoint;
        SessionList = new SessionListViewModel(new SessionManagementActions(
            Rename: (sessionId, title) => _service.RenameSessionAsync(sessionId, title),
            SetPinned: (sessionId, pinned) => _service.SetSessionFlagsAsync(sessionId, pinned: pinned),
            SetArchived: (sessionId, archived) => _service.SetSessionFlagsAsync(sessionId, archived: archived),
            Delete: sessionId => _service.DeleteSessionAsync(sessionId),
            Refresh: RefreshSessionListAsync));

        _outputSubscription = _service.SessionOutput
            .Subscribe(this, static (output, self) =>
                self._dispatcher.Post(() => self.RouteOutput(output)));
        _connectionSubscription = _service.ConnectionEvents
            .Subscribe(this, static (evt, self) =>
                self._dispatcher.Post(() => self.HandleConnectionEvent(evt)));

        _ = ConnectLoopAsync();
    }

    /// <summary>The view's ~80 ms timer calls this to flush streamed deltas.</summary>
    public void FlushStreamingDeltas() => Chat?.FlushStreamingDeltas();

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0)
            return;

        InputText = string.Empty;

        if (_service.IsConnected && _sessionEnsured)
        {
            await DispatchAsync(text);
            return;
        }

        _queue.Add(text);
        QueuedCount = _queue.Count;
        Status = $"Queued ({_queue.Count}) - waiting for daemon at {_endpoint}";
    }

    [RelayCommand]
    private async Task AttachSessionAsync(SessionListItemViewModel? item)
    {
        if (item is null || Chat?.SessionId == item.SessionId)
            return;

        try
        {
            var sessionId = await _service.ResumeSessionAsync(item.SessionId);
            AdoptSession(sessionId);
            // Resume is a foreground command, so a successful attach proves
            // the transport and a session — without this, a send after a
            // deleted-session recovery would queue forever.
            _sessionEnsured = true;
            Status = $"Connected - {_endpoint}";
            // A queued message (e.g. from a send the daemon rejected) flushes
            // on re-attach too, not only on transport reconnect.
            await FlushQueueAsync();
            // The attach may be the first traffic after a reconnect; the
            // cached list can be stale, so refresh it from the daemon.
            await RefreshSessionListAsync();
        }
        catch (Exception ex)
        {
            Status = $"Attach failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RemoveGrantAsync(string path)
    {
        try
        {
            await _service.RemoveFolderGrantAsync(path);
        }
        catch (Exception ex)
        {
            Status = $"Grant removal failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Grants a folder picked by the operator. The chip appears when the
    /// daemon's <c>folder_grant</c> event echoes back — never optimistically.
    /// </summary>
    public async Task AddGrantAsync(string folderPath)
    {
        try
        {
            await _service.AddFolderGrantAsync(folderPath);
        }
        catch (Exception ex)
        {
            Status = $"Grant failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Uploads a picked file; it rides the next message once.
    /// </summary>
    public async Task AttachFileAsync(string filePath)
    {
        if (Chat is null)
        {
            Status = "Attach a session before uploading a file.";
            return;
        }

        try
        {
            await using var stream = File.OpenRead(filePath);
            var result = await _service.UploadAttachmentAsync(
                Chat.SessionId, Path.GetFileName(filePath), stream, contentType: null);
            PendingAttachments.Add(new PendingAttachmentChip(result.AttachmentId, result.FileName));
        }
        catch (Exception ex)
        {
            Status = $"Upload failed: {ex.Message}";
        }
    }

    public void Dispose()
    {
        _outputSubscription.Dispose();
        _connectionSubscription.Dispose();
    }

    private async Task ConnectLoopAsync()
    {
        // Both call sites run on the UI thread (constructor and dispatched
        // connection events), so this flag needs no lock. It stops a second
        // loop from racing the first when connection events arrive while the
        // startup loop is still retrying.
        if (_connectLoopRunning)
            return;
        _connectLoopRunning = true;
        try
        {
            while (true)
            {
                try
                {
                    await _service.ConnectAsync();
                    await EnsureSessionAndFlushAsync();
                    await RefreshSessionListAsync();
                    return;
                }
                catch (Exception ex)
                {
                    _dispatcher.Post(() => Status = $"Disconnected - retrying {_endpoint} ({ex.Message})");
                    await Task.Delay(ConnectRetryDelay);
                }
            }
        }
        finally
        {
            _dispatcher.Post(() => _connectLoopRunning = false);
        }
    }

    private async Task EnsureSessionAndFlushAsync()
    {
        var sessionId = await _service.EnsureSessionAsync();
        _sessionEnsured = true;
        _dispatcher.Post(() =>
        {
            if (Chat?.SessionId != sessionId)
                AdoptSession(sessionId);
            Status = $"Connected - {_endpoint}";
        });

        await FlushQueueAsync();
    }

    private async Task FlushQueueAsync()
    {
        while (true)
        {
            string? next = null;
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispatcher.Post(() =>
            {
                if (_queue.Count > 0)
                {
                    next = _queue[0];
                    _queue.RemoveAt(0);
                    QueuedCount = _queue.Count;
                }

                completion.SetResult(true);
            });
            await completion.Task;
            if (next is null)
                return;

            // A failed dispatch requeued the message at the front — stop
            // flushing instead of spinning on the same failure.
            if (!await DispatchAsync(next))
                return;
        }
    }

    private async Task RefreshSessionListAsync()
    {
        try
        {
            var entries = await _service.ListSessionsAsync();
            _dispatcher.Post(() => SessionList.Load(entries));
        }
        catch (Exception ex)
        {
            // The list refreshes again on the next reconnect; the chat pane
            // stays fully usable without it. Surface the failure only when
            // the pane would otherwise be inexplicably blank.
            _dispatcher.Post(() =>
            {
                if (SessionList.Sessions.Count == 0)
                    Status = $"Session list unavailable: {ex.Message}";
            });
        }
    }

    private void AdoptSession(string sessionId)
    {
        var selector = new ModelSelectorViewModel((provider, modelId) =>
            provider is null || modelId is null
                ? _service.ClearSessionModelAsync()
                : _service.SetSessionModelAsync(provider, modelId));
        Chat = new ChatSessionViewModel(
            sessionId,
            (callId, key) => _service.RespondToInteractionAsync(callId, key),
            selector);
        PendingAttachments.Clear();
        _ = LoadModelCatalogAsync(selector);
    }

    private async Task LoadModelCatalogAsync(ModelSelectorViewModel selector)
    {
        _dispatcher.Post(selector.BeginCatalogLoad);
        try
        {
            var catalog = await _service.GetModelCatalogAsync();
            _dispatcher.Post(() =>
            {
                if (catalog is null)
                    selector.SetCatalogError("The daemon returned an empty model catalog response.");
                else
                    selector.LoadCatalog(catalog);
            });
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() => selector.SetCatalogError($"Model catalog unavailable: {ex.Message}"));
        }
    }

    private async Task<bool> DispatchAsync(string text)
    {
        try
        {
            await _service.SendAsync(text);
            _dispatcher.Post(() =>
            {
                Chat?.OnMessageSent(text);
                // The daemon consumed the pending uploads with this message.
                PendingAttachments.Clear();
            });
            return true;
        }
        catch (Exception ex)
        {
            _dispatcher.Post(() =>
            {
                // A failed send returns to the FRONT of the queue so flush
                // order stays the original send order.
                Status = $"Send failed: {ex.Message}";
                _queue.Insert(0, text);
                QueuedCount = _queue.Count;
            });

            // One recovery attempt: the daemon may have dropped this
            // session's registry binding (e.g. another operator client
            // detached). Re-ensuring re-registers it and flushes the queue.
            // Guarded so a genuinely dead daemon does not loop — further
            // retries wait for the next connection event or attach.
            if (!_recovering)
            {
                _recovering = true;
                try
                {
                    await EnsureSessionAndFlushAsync();
                }
                catch (Exception recoverEx)
                {
                    _dispatcher.Post(() => Status = $"Send failed: {recoverEx.Message}");
                }
                finally
                {
                    _recovering = false;
                }
            }

            return false;
        }
    }

    private void RouteOutput(SessionOutput output)
    {
        // Live title events update the list for any session.
        if (output is SessionTitleOutput title)
            SessionList.ApplyTitle(output.SessionId.Value, title.Title);

        if (output is SessionDeletedOutput)
        {
            // The attached session's chat pane clears; the list refreshes
            // for a deletion of any session (another client may have done it).
            if (Chat is not null && string.Equals(Chat.SessionId, output.SessionId.Value, StringComparison.Ordinal))
            {
                Chat = null;
                _sessionEnsured = false;
                Status = "Session deleted.";
            }

            _ = RefreshSessionListAsync();
            return;
        }

        // Everything else renders only for the attached session — a
        // detached session's events (approvals included) never reach a
        // card the operator could answer.
        if (Chat is null || !string.Equals(Chat.SessionId, output.SessionId.Value, StringComparison.Ordinal))
            return;

        if (output is SessionJoined joined)
        {
            Chat.LoadReplay(joined);
            return;
        }

        Chat.HandleOutput(output);

        if (output is ErrorOutput error)
            Status = $"Error - {error.Message}";
    }

    private void HandleConnectionEvent(DaemonConnectionEvent evt)
    {
        Status = evt.State switch
        {
            DaemonConnectionState.Connected => $"Connected - {_endpoint}",
            DaemonConnectionState.Connecting => $"Connecting to {_endpoint}...",
            DaemonConnectionState.Reconnecting or DaemonConnectionState.TransportClosed
                => $"Reconnecting to {_endpoint}...",
            DaemonConnectionState.Disconnected => $"Disconnected - {evt.Message}",
            _ => evt.Message,
        };

        if (evt.State == DaemonConnectionState.Connected && _sessionEnsured)
        {
            if (_queue.Count > 0)
                _ = EnsureSessionAndFlushAsync();
            _ = RefreshSessionListAsync();
        }

        // The client's reconnect authority only re-attaches an existing
        // session; with none ensured (the attached session was deleted)
        // it leaves recovery "for the next command" and no command ever
        // comes. Rearm the connect loop here so the GUI recovers on its
        // own instead of showing "Reconnecting..." forever.
        if (!_sessionEnsured
            && evt.State is DaemonConnectionState.TransportClosed or DaemonConnectionState.Disconnected)
        {
            _ = ConnectLoopAsync();
        }
    }
}
