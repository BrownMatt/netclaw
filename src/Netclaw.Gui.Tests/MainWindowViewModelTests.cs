// -----------------------------------------------------------------------
// <copyright file="MainWindowViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Protocol;
using Netclaw.Client;
using Netclaw.Gui.Services;
using Netclaw.Gui.ViewModels;
using Netclaw.Tools;
using R3;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Gui.Tests;

/// <summary>
/// Shell tests: queue-and-flush input, failed-send requeue, output routing
/// to the attached session only, and live session-list titles.
/// </summary>
public sealed class MainWindowViewModelTests : IDisposable
{
    private readonly FakeDaemonSessionService _service = new();
    private MainWindowViewModel? _vm;

    public void Dispose() => _vm?.Dispose();

    private MainWindowViewModel CreateViewModel()
    {
        _vm = new MainWindowViewModel(_service, new InlineDispatcher(), "http://127.0.0.1:5199", new FakeTimeProvider());
        return _vm;
    }

    [Fact]
    public async Task Messages_queued_while_disconnected_flush_in_order()
    {
        _service.ExpectedSends = 2;
        var vm = CreateViewModel();

        vm.InputText = "first";
        await vm.SendCommand.ExecuteAsync(null);
        vm.InputText = "second";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.QueuedCount);
        Assert.Empty(_service.SentMessages);

        // The daemon comes up: the gated connect completes and the queue
        // flushes in original order.
        _service.ConnectGate.SetResult();
        await _service.ExpectedSendsReached.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], _service.SentMessages);
        Assert.Equal(0, vm.QueuedCount);
    }

    private static async Task WaitForChatAsync(MainWindowViewModel vm)
    {
        if (vm.Chat is not null)
            return;

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.Chat) && vm.Chat is not null)
                ready.TrySetResult();
        }

        vm.PropertyChanged += Handler;
        try
        {
            if (vm.Chat is not null)
                return;
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            vm.PropertyChanged -= Handler;
        }
    }

    [Fact]
    public async Task Failed_send_returns_the_message_to_the_queue()
    {
        _service.ConnectGate.SetResult();
        var vm = CreateViewModel();
        await WaitForChatAsync(vm);

        _service.SendFailure = new InvalidOperationException("transport dropped");
        vm.InputText = "important";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(1, vm.QueuedCount);
        Assert.Empty(_service.SentMessages);
        Assert.Contains("Send failed", vm.Status);
    }

    [Fact]
    public async Task Outputs_for_a_detached_session_do_not_render_or_answer()
    {
        _service.ConnectGate.SetResult();
        var vm = CreateViewModel();
        await WaitForChatAsync(vm);

        // An approval for a DIFFERENT session must not become a card the
        // operator could answer.
        _service.Outputs.OnNext(Interaction("signalr/other-session", "c9"));
        Assert.Empty(vm.Chat!.Blocks);

        // The same event for the attached session renders.
        _service.Outputs.OnNext(Interaction(_service.SessionIdToReturn, "c1"));
        Assert.Single(vm.Chat!.Blocks);
    }

    [Fact]
    public async Task Session_joined_replay_renders_and_titles_update_live()
    {
        _service.Sessions =
        [
            new SessionCatalogEntryDto
            {
                PersistenceId = "session-signalr/fake-session",
                Channel = "tui",
                Status = "active",
                TurnCount = 1,
                CreatedAt = 1,
                LastActivity = 2
            }
        ];
        var vm = CreateViewModel();
        var listLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        vm.SessionList.Sessions.CollectionChanged += (_, _) => listLoaded.TrySetResult();

        _service.ConnectGate.SetResult();
        await WaitForChatAsync(vm);
        await listLoaded.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        _service.Outputs.OnNext(new SessionJoined
        {
            SessionId = new SessionId(_service.SessionIdToReturn),
            TurnCount = 1,
            RecentMessages = [new ChatMessageDto("user", "replayed line")]
        });
        var block = Assert.IsType<UserMessageBlockViewModel>(Assert.Single(vm.Chat!.Blocks));
        Assert.Equal("replayed line", block.Text);

        // Live title event updates the list without a refresh.
        _service.Outputs.OnNext(new SessionTitleOutput("Fresh title")
        {
            SessionId = new SessionId("signalr/fake-session")
        });
        Assert.Equal("Fresh title", Assert.Single(vm.SessionList.Sessions).Title);
    }

    [Fact]
    public async Task Deleting_the_attached_session_clears_the_chat_pane()
    {
        _service.ConnectGate.SetResult();
        var vm = CreateViewModel();
        await WaitForChatAsync(vm);

        _service.Outputs.OnNext(new SessionDeletedOutput
        {
            SessionId = new SessionId(_service.SessionIdToReturn)
        });

        Assert.Null(vm.Chat);
        Assert.Contains("deleted", vm.Status);
        Assert.False(vm.IsLoadingSession);
        Assert.DoesNotContain(vm.SessionList.Sessions, s => s.IsActive);
    }

    [Fact]
    public async Task Deletion_of_a_detached_session_keeps_the_chat_pane()
    {
        _service.ConnectGate.SetResult();
        var vm = CreateViewModel();
        await WaitForChatAsync(vm);

        _service.Outputs.OnNext(new SessionDeletedOutput
        {
            SessionId = new SessionId("signalr/some-other-session")
        });

        Assert.NotNull(vm.Chat);
    }

    [Fact]
    public async Task Transport_drop_after_a_deleted_session_reconnects_with_a_fresh_session()
    {
        _service.ConnectGate.SetResult();
        var vm = CreateViewModel();
        await WaitForChatAsync(vm);
        Assert.Equal(1, _service.EnsureCount);

        _service.Outputs.OnNext(new SessionDeletedOutput
        {
            SessionId = new SessionId(_service.SessionIdToReturn)
        });
        Assert.Null(vm.Chat);

        // The daemon restarts: the transport drops while no session is
        // ensured. The client reconnect authority has nothing to re-attach,
        // so the viewmodel must rearm its own connect loop.
        _service.Connections.OnNext(new DaemonConnectionEvent(
            DaemonConnectionState.TransportClosed, "http://127.0.0.1:5199", "dropped"));

        await WaitForChatAsync(vm);
        Assert.Equal(2, _service.EnsureCount);
    }

    [Fact]
    public async Task Transport_drop_with_an_attached_session_defers_to_the_client_reconnect()
    {
        _service.ConnectGate.SetResult();
        var vm = CreateViewModel();
        await WaitForChatAsync(vm);
        Assert.Equal(1, _service.ConnectCount);

        _service.Connections.OnNext(new DaemonConnectionEvent(
            DaemonConnectionState.TransportClosed, "http://127.0.0.1:5199", "dropped"));

        // Drain any accidentally started continuations before asserting the
        // viewmodel took no action of its own.
        for (var i = 0; i < 20; i++)
            await Task.Yield();

        Assert.Equal(1, _service.ConnectCount);
        Assert.Equal(1, _service.EnsureCount);
        Assert.Contains("Reconnecting", vm.Status);
    }

    [Fact]
    public async Task Attach_after_a_deleted_session_restores_direct_send_and_refreshes_the_list()
    {
        _service.ConnectGate.SetResult();
        _service.ExpectedSends = 1;
        var vm = CreateViewModel();
        await WaitForChatAsync(vm);

        _service.Outputs.OnNext(new SessionDeletedOutput
        {
            SessionId = new SessionId(_service.SessionIdToReturn)
        });
        Assert.Null(vm.Chat);
        var listCountBefore = _service.ListCount;

        await vm.AttachSessionCommand.ExecuteAsync(new SessionListItemViewModel
        {
            SessionId = "signalr/resumed-session",
            Channel = "tui"
        });

        Assert.NotNull(vm.Chat);
        Assert.True(_service.ListCount > listCountBefore);

        // Without the ensured flag restored by the attach, this send would
        // queue forever instead of dispatching.
        vm.InputText = "after recovery";
        await vm.SendCommand.ExecuteAsync(null);

        Assert.Equal(["after recovery"], _service.SentMessages);
        Assert.Equal(0, vm.QueuedCount);
    }

    [Fact]
    public async Task Attach_file_uploads_and_shows_a_pending_chip_until_the_next_send()
    {
        _service.ConnectGate.SetResult();
        _service.ExpectedSends = 1;
        var vm = CreateViewModel();
        await WaitForChatAsync(vm);

        var tempFile = Path.Combine(Path.GetTempPath(), $"netclaw-gui-attach-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(tempFile, "chip me", TestContext.Current.CancellationToken);
        try
        {
            await vm.AttachFileAsync(tempFile);

            var chip = Assert.Single(vm.PendingAttachments);
            Assert.Equal(Path.GetFileName(tempFile), chip.FileName);

            // The next send consumes the pending upload — the chip clears.
            vm.InputText = "here is the file";
            await vm.SendCommand.ExecuteAsync(null);
            Assert.Empty(vm.PendingAttachments);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ── Phase 6: new session, periodic refresh, loading state, title ──

    private static SessionCatalogEntryDto Catalog(string sessionId, string? title = null) => new()
    {
        PersistenceId = $"session-{sessionId}",
        Channel = "tui",
        Status = "active",
        TurnCount = 1,
        CreatedAt = 1,
        LastActivity = 2,
        Title = title
    };

    private async Task<MainWindowViewModel> ConnectedViewModelAsync()
    {
        _service.ConnectGate.SetResult();
        var vm = CreateViewModel();
        await WaitForChatAsync(vm);
        await _service.SessionsListed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        return vm;
    }

    [Fact]
    public async Task New_session_adopts_the_created_id_and_marks_it_active()
    {
        var vm = await ConnectedViewModelAsync();
        var original = vm.Chat!.SessionId;
        _service.Sessions = [Catalog(original), Catalog("signalr/new-1")];

        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.Equal(1, _service.CreateCount);
        Assert.Equal("signalr/new-1", vm.Chat!.SessionId);
        Assert.Empty(vm.Chat.Blocks);
        Assert.Equal("signalr/new-1", Assert.Single(vm.SessionList.Sessions, s => s.IsActive).SessionId);
        Assert.StartsWith("signalr/new-1 - Netclaw", vm.WindowTitle);

        // The fresh session's join ends the loading state with an empty history.
        Assert.True(vm.IsLoadingSession);
        _service.Outputs.OnNext(new SessionJoined { SessionId = new SessionId("signalr/new-1"), TurnCount = 0, RecentMessages = [] });
        Assert.False(vm.IsLoadingSession);
        Assert.Empty(vm.Chat.Blocks);
    }

    [Fact]
    public async Task Failed_new_session_keeps_the_current_session()
    {
        var vm = await ConnectedViewModelAsync();
        var original = vm.Chat!.SessionId;
        _service.CreateFailure = new InvalidOperationException("ingress closed");

        await vm.NewSessionCommand.ExecuteAsync(null);

        Assert.Equal(original, vm.Chat!.SessionId);
        Assert.Contains("New session failed", vm.Status);
        Assert.Contains("ingress closed", vm.Status);
    }

    [Fact]
    public async Task Timer_tick_refreshes_only_while_connected_and_idle()
    {
        var vm = CreateViewModel();

        // Not connected yet: a tick sends nothing.
        await vm.OnSessionListTimerTick();
        Assert.Equal(0, _service.ListCount);

        _service.ConnectGate.SetResult();
        await WaitForChatAsync(vm);
        await _service.SessionsListed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var baseline = _service.ListCount;

        await vm.OnSessionListTimerTick();
        Assert.Equal(baseline + 1, _service.ListCount);

        // An in-flight refresh blocks the next tick until it completes.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.ListGate = gate;
        var inFlight = vm.OnSessionListTimerTick();
        await vm.OnSessionListTimerTick();
        Assert.Equal(baseline + 2, _service.ListCount);

        _service.ListGate = null;
        gate.SetResult();
        await inFlight.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await vm.OnSessionListTimerTick();
        Assert.Equal(baseline + 3, _service.ListCount);
    }

    [Fact]
    public async Task Loading_state_spans_the_attach_until_the_replay_arrives()
    {
        var vm = await ConnectedViewModelAsync();
        Assert.True(vm.IsLoadingSession);

        _service.Outputs.OnNext(new SessionJoined
        {
            SessionId = new SessionId(_service.SessionIdToReturn),
            TurnCount = 0,
            RecentMessages = []
        });

        Assert.False(vm.IsLoadingSession);
        Assert.Empty(vm.Chat!.Blocks);
    }

    [Fact]
    public async Task Join_that_arrives_before_the_adopt_still_renders_and_ends_loading()
    {
        // The daemon pushes SessionJoined while the ensure call is in flight:
        // the snapshot reaches the shell before Chat exists.
        var vm = CreateViewModel();
        _service.Outputs.OnNext(new SessionJoined
        {
            SessionId = new SessionId(_service.SessionIdToReturn),
            TurnCount = 1,
            RecentMessages = [new ChatMessageDto("user", "early replay")]
        });
        Assert.Null(vm.Chat);

        _service.ConnectGate.SetResult();
        await WaitForChatAsync(vm);

        Assert.False(vm.IsLoadingSession);
        var block = Assert.IsType<UserMessageBlockViewModel>(Assert.Single(vm.Chat!.Blocks));
        Assert.Equal("early replay", block.Text);
    }

    [Fact]
    public async Task Failed_attach_ends_the_loading_state()
    {
        var vm = await ConnectedViewModelAsync();
        Assert.True(vm.IsLoadingSession);
        _service.ResumeFailure = new InvalidOperationException("no such session");

        await vm.AttachSessionCommand.ExecuteAsync(new SessionListItemViewModel
        {
            SessionId = "signalr/missing",
            Channel = "tui"
        });

        Assert.False(vm.IsLoadingSession);
        Assert.Contains("Attach failed", vm.Status);
    }

    [Fact]
    public async Task Connection_loss_ends_the_loading_state_and_suffixes_the_title()
    {
        var vm = await ConnectedViewModelAsync();
        Assert.True(vm.IsLoadingSession);
        Assert.EndsWith("- Netclaw", vm.WindowTitle);

        _service.Connections.OnNext(new DaemonConnectionEvent(
            DaemonConnectionState.Disconnected, "http://127.0.0.1:5199", "gone"));

        Assert.False(vm.IsLoadingSession);
        Assert.EndsWith("Netclaw (disconnected)", vm.WindowTitle);

        _service.Connections.OnNext(new DaemonConnectionEvent(
            DaemonConnectionState.Connected, "http://127.0.0.1:5199", "back"));

        Assert.EndsWith("- Netclaw", vm.WindowTitle);
    }

    [Fact]
    public async Task Window_title_follows_the_attached_session_title()
    {
        _service.Sessions = [Catalog("signalr/fake-session", "Deploy checklist")];
        var vm = await ConnectedViewModelAsync();

        Assert.Equal("Deploy checklist - Netclaw", vm.WindowTitle);

        _service.Outputs.OnNext(new SessionTitleOutput("Renamed live")
        {
            SessionId = new SessionId("signalr/fake-session")
        });
        Assert.Equal("Renamed live - Netclaw", vm.WindowTitle);
    }

    [Fact]
    public async Task Window_title_uses_the_id_when_the_session_has_no_title()
    {
        var vm = await ConnectedViewModelAsync();

        Assert.Equal("signalr/fake-session - Netclaw", vm.WindowTitle);
    }

    private static ToolInteractionRequest Interaction(string sessionId, string callId) => new()
    {
        SessionId = new SessionId(sessionId),
        Kind = "approval",
        CallId = new ToolCallId(callId),
        ToolName = new ToolName("shell_execute"),
        DisplayText = "Approve?",
        Options = [new ToolInteractionOption(new ApprovalOptionKey("approve_once"), "Once")]
    };

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class FakeDaemonSessionService : IDaemonSessionService
    {
        public Subject<SessionOutput> Outputs { get; } = new();

        public Subject<DaemonConnectionEvent> Connections { get; } = new();

        public TaskCompletionSource ConnectGate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SessionEnsured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SessionsListed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ExpectedSendsReached { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> SentMessages { get; } = [];

        public List<SessionCatalogEntryDto> Sessions { get; set; } = [];

        public Exception? SendFailure { get; set; }

        public int ExpectedSends { get; set; } = int.MaxValue;

        public string SessionIdToReturn { get; set; } = "signalr/fake-session";

        public int ConnectCount { get; private set; }

        public int EnsureCount { get; private set; }

        public int ListCount { get; private set; }

        public bool IsConnected { get; private set; }

        public Observable<SessionOutput> SessionOutput => Outputs;

        public Observable<DaemonConnectionEvent> ConnectionEvents => Connections;

        public async Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCount++;
            await ConnectGate.Task.WaitAsync(cancellationToken);
            IsConnected = true;
        }

        public Task<string> EnsureSessionAsync(CancellationToken cancellationToken = default)
        {
            EnsureCount++;
            SessionEnsured.TrySetResult();
            return Task.FromResult(SessionIdToReturn);
        }

        public int CreateCount { get; private set; }

        public Exception? CreateFailure { get; set; }

        public Exception? ResumeFailure { get; set; }

        /// <summary>When set, a list request waits on it (in-flight simulation).</summary>
        public TaskCompletionSource? ListGate { get; set; }

        public Task<string> CreateSessionAsync(CancellationToken cancellationToken = default)
        {
            if (CreateFailure is { } failure)
                return Task.FromException<string>(failure);

            CreateCount++;
            SessionIdToReturn = $"signalr/new-{CreateCount}";
            return Task.FromResult(SessionIdToReturn);
        }

        public Task<string> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            if (ResumeFailure is { } failure)
                return Task.FromException<string>(failure);

            SessionIdToReturn = sessionId;
            return Task.FromResult(sessionId);
        }

        public Task SendAsync(string text, CancellationToken cancellationToken = default)
        {
            if (SendFailure is { } failure)
                throw failure;

            SentMessages.Add(text);
            if (SentMessages.Count >= ExpectedSends)
                ExpectedSendsReached.TrySetResult();
            return Task.CompletedTask;
        }

        public Task RespondToInteractionAsync(string callId, string selectedKey, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task AddFolderGrantAsync(string path, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFolderGrantAsync(string path, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async Task<List<SessionCatalogEntryDto>> ListSessionsAsync(CancellationToken cancellationToken = default)
        {
            ListCount++;
            SessionsListed.TrySetResult();
            if (ListGate is { } gate)
                await gate.Task.WaitAsync(cancellationToken);
            return Sessions;
        }

        public Task<DaemonApi.SessionAttachmentUploadResultDto> UploadAttachmentAsync(
            string sessionId,
            string fileName,
            Stream content,
            string? contentType,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new DaemonApi.SessionAttachmentUploadResultDto(
                "att-1", fileName, $"inbox/{fileName}", "text/plain", 1));

        public Task<ModelCatalogResponseDto?> GetModelCatalogAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ModelCatalogResponseDto?>(new ModelCatalogResponseDto([]));

        public Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetSessionFlagsAsync(string sessionId, bool? pinned = null, bool? archived = null, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task SetSessionModelAsync(string provider, string modelId, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ClearSessionModelAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<LogTailResultDto?> GetDaemonLogTailAsync(int? tail = null, CancellationToken cancellationToken = default)
            => Task.FromResult<LogTailResultDto?>(null);

        public Task<LogTailResultDto?> GetSessionLogTailAsync(string sessionId, int? tail = null, CancellationToken cancellationToken = default)
            => Task.FromResult<LogTailResultDto?>(null);

        public Task<RunningModelsResponseDto?> GetRunningModelsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<RunningModelsResponseDto?>(null);

        public Task<Netclaw.Configuration.DaemonRuntimeStatus.Response?> GetDaemonStatusAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<Netclaw.Configuration.DaemonRuntimeStatus.Response?>(null);

        public Task<Netclaw.Configuration.DaemonStats.Response?> GetStatsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<Netclaw.Configuration.DaemonStats.Response?>(null);

        public Task<System.Text.Json.JsonElement> GetMcpServerStatusesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(default(System.Text.Json.JsonElement));
    }
}
