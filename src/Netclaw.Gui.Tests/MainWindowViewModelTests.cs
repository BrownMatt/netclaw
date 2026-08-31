// -----------------------------------------------------------------------
// <copyright file="MainWindowViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
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
        _vm = new MainWindowViewModel(_service, new InlineDispatcher(), "http://127.0.0.1:5199");
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

        public Task<string> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        {
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

        public Task<List<SessionCatalogEntryDto>> ListSessionsAsync(CancellationToken cancellationToken = default)
        {
            ListCount++;
            SessionsListed.TrySetResult();
            return Task.FromResult(Sessions);
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
