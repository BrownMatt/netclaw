// -----------------------------------------------------------------------
// <copyright file="DaemonClientSessionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Cli.Daemon;
using Netclaw.Daemon.Gateway;
using R3;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

using Netclaw.Client;

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonClientSessionTests
{
    [Fact]
    public async Task ResumeSessionAsync_reattaches_to_existing_session_via_EnsureSession()
    {
        using var host = await StartFakeHubAsync();
        await using var client = InMemorySignalRClientFactory.Create(host);

        // Create an initial session to get a known session ID
        var originalSessionId = await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);
        Assert.StartsWith("signalr/", originalSessionId);

        // Simulate a "new client" by creating a fresh DaemonClient
        // that resumes the same session ID
        await using var client2 = InMemorySignalRClientFactory.Create(host);

        var outputReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = client2.SessionOutput.Subscribe(output =>
        {
            if (output is TextOutput { Text: "echo:hello-resumed" })
                outputReceived.TrySetResult();
        });

        var resumedSessionId = await client2.ResumeSessionAsync(originalSessionId, Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        // EnsureSession should return the same session ID, not create a new one
        Assert.Equal(originalSessionId, resumedSessionId);

        // Verify the session is functional — can send and receive messages
        await client2.SendAsync("hello-resumed", TestContext.Current.CancellationToken);

        await outputReceived.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Session_deleted_output_clears_the_attached_session()
    {
        using var host = await StartFakeHubAsync();
        await using var client = InMemorySignalRClientFactory.Create(host);
        var sessionId = await client.CreateSessionAsync(
            Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        var deletedSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var sub = client.SessionOutput.Subscribe(output =>
        {
            if (output is SessionDeletedOutput deleted && deleted.SessionId.Value == sessionId)
                deletedSeen.TrySetResult();
        });

        // The daemon's teardown pushes session_deleted directly through the
        // hub context — simulate that push server-side.
        var hubContext = host.Services
            .GetRequiredService<IHubContext<FakeResumeHub, Netclaw.Daemon.Gateway.ISessionHubClient>>();
        await hubContext.Clients.All.ReceiveOutput(new SessionOutputDto
        {
            Type = "session_deleted",
            SessionId = sessionId,
            TimestampMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds()
        });
        await deletedSeen.Task.WaitAsync(TestContext.Current.CancellationToken);

        // The client forgot the dead session — a send must fail loudly
        // instead of resuming (and thereby recreating) the deleted id.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SendAsync("into the void", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RespondToInteractionAsync_invokes_hub_method()
    {
        using var host = await StartFakeHubAsync();
        var state = host.Services.GetRequiredService<FakeSessionState>();

        await using var client = InMemorySignalRClientFactory.Create(host);
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        await client.RespondToInteractionAsync("call-1", ApprovalOptionKeys.ApproveOnce, TestContext.Current.CancellationToken);

        Assert.Equal(("call-1", ApprovalOptionKeys.ApproveOnce), state.LastInteractionResponse);
    }

    [Fact]
    public async Task RespondToInteractionAsync_supports_session_scope()
    {
        using var host = await StartFakeHubAsync();
        var state = host.Services.GetRequiredService<FakeSessionState>();

        await using var client = InMemorySignalRClientFactory.Create(host);
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        await client.RespondToInteractionAsync("call-2", ApprovalOptionKeys.ApproveSession, TestContext.Current.CancellationToken);

        Assert.Equal(("call-2", ApprovalOptionKeys.ApproveSession), state.LastInteractionResponse);
    }

    [Fact]
    public async Task AddFolderGrantAsync_invokes_hub_method()
    {
        using var host = await StartFakeHubAsync();
        var state = host.Services.GetRequiredService<FakeSessionState>();

        await using var client = InMemorySignalRClientFactory.Create(host);
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        await client.AddFolderGrantAsync("/home/user/projects/alpha", TestContext.Current.CancellationToken);

        Assert.Equal(("/home/user/projects/alpha", true), Assert.Single(state.FolderGrantCalls));
    }

    [Fact]
    public async Task RemoveFolderGrantAsync_invokes_hub_method()
    {
        using var host = await StartFakeHubAsync();
        var state = host.Services.GetRequiredService<FakeSessionState>();

        await using var client = InMemorySignalRClientFactory.Create(host);
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        await client.RemoveFolderGrantAsync("/home/user/projects/alpha", TestContext.Current.CancellationToken);

        Assert.Equal(("/home/user/projects/alpha", false), Assert.Single(state.FolderGrantCalls));
    }

    [Fact]
    public async Task AddFolderGrantAsync_surfaces_daemon_rejection()
    {
        using var host = await StartFakeHubAsync();

        await using var client = InMemorySignalRClientFactory.Create(host);
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<HubException>(
            () => client.AddFolderGrantAsync("/rejected/path", TestContext.Current.CancellationToken));

        Assert.Contains("not a directory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SetSessionModelAsync_invokes_hub_method()
    {
        using var host = await StartFakeHubAsync();
        var state = host.Services.GetRequiredService<FakeSessionState>();

        await using var client = InMemorySignalRClientFactory.Create(host);
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        await client.SetSessionModelAsync("local-ollama", "qwen3:30b", TestContext.Current.CancellationToken);

        Assert.Equal(("local-ollama", "qwen3:30b"), Assert.Single(state.ModelOverrideCalls));
    }

    [Fact]
    public async Task ClearSessionModelAsync_invokes_hub_method()
    {
        using var host = await StartFakeHubAsync();
        var state = host.Services.GetRequiredService<FakeSessionState>();

        await using var client = InMemorySignalRClientFactory.Create(host);
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        await client.ClearSessionModelAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, state.ClearModelCalls);
    }

    [Fact]
    public async Task SetSessionModelAsync_surfaces_daemon_rejection()
    {
        using var host = await StartFakeHubAsync();

        await using var client = InMemorySignalRClientFactory.Create(host);
        await client.CreateSessionAsync(Netclaw.Actors.Channels.ChannelType.Tui, TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<HubException>(
            () => client.SetSessionModelAsync("local-ollama", "rejected-model", TestContext.Current.CancellationToken));

        Assert.Contains("not available", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IHost> StartFakeHubAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSignalR();
        builder.Services.AddSingleton<FakeSessionState>();

        var app = builder.Build();
        app.MapHub<FakeResumeHub>("/hub/session");

        await app.StartAsync();
        return app;
    }

    private sealed class FakeSessionState
    {
        private readonly object _gate = new();
        private readonly HashSet<string> _sessions = [];
        private readonly Dictionary<string, string> _connectionSessions = [];
        public (string CallId, string SelectedKey)? LastInteractionResponse { get; private set; }

        public List<(string Path, bool Added)> FolderGrantCalls { get; } = [];

        public List<(string Provider, string ModelId)> ModelOverrideCalls { get; } = [];

        public int ClearModelCalls { get; private set; }

        public SessionEnsureResultDto Ensure(string connectionId, string? sessionId)
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(sessionId) && _sessions.Contains(sessionId))
                {
                    _connectionSessions[connectionId] = sessionId;
                    return new SessionEnsureResultDto(sessionId, false);
                }

                var created = $"signalr/{Guid.NewGuid():N}";
                _sessions.Add(created);
                _connectionSessions[connectionId] = created;
                return new SessionEnsureResultDto(created, true);
            }
        }

        public bool IsAttached(string connectionId, string sessionId)
            => _connectionSessions.TryGetValue(connectionId, out var attached)
               && string.Equals(attached, sessionId, StringComparison.Ordinal);

        public void RecordInteractionResponse(string callId, string selectedKey)
        {
            lock (_gate)
            {
                LastInteractionResponse = (callId, selectedKey);
            }
        }

        public void RecordFolderGrant(string path, bool added)
        {
            lock (_gate)
            {
                FolderGrantCalls.Add((path, added));
            }
        }

        public void RecordModelOverride(string provider, string modelId)
        {
            lock (_gate)
            {
                ModelOverrideCalls.Add((provider, modelId));
            }
        }

        public void RecordClearModel()
        {
            lock (_gate)
            {
                ClearModelCalls++;
            }
        }

        public void Disconnect(string connectionId)
        {
            lock (_gate)
            {
                _connectionSessions.Remove(connectionId);
            }
        }
    }

    private sealed class FakeResumeHub : Hub<ISessionHubClient>
    {
        private readonly FakeSessionState _state;

        public FakeResumeHub(FakeSessionState state)
        {
            _state = state;
        }

        public Task<SessionEnsureResultDto> EnsureSession(string? sessionId, string channelType)
            => Task.FromResult(_state.Ensure(Context.ConnectionId, sessionId));

        public async Task SendMessage(string sessionId, string text)
        {
            if (!_state.IsAttached(Context.ConnectionId, sessionId))
                throw new HubException("session not attached");

            await Clients.Caller.ReceiveOutput(new SessionOutputDto
            {
                Type = "text",
                SessionId = sessionId,
                TimestampMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
                Text = $"echo:{text}"
            });

            await Clients.Caller.ReceiveOutput(new SessionOutputDto
            {
                Type = "turn_completed",
                SessionId = sessionId,
                TimestampMs = TimeProvider.System.GetUtcNow().ToUnixTimeMilliseconds(),
                TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1)
            });
        }

        public Task RespondToInteraction(string sessionId, string callId, string selectedKey)
        {
            if (!_state.IsAttached(Context.ConnectionId, sessionId))
                throw new HubException("session not attached");

            _state.RecordInteractionResponse(callId, selectedKey);
            return Task.CompletedTask;
        }

        public Task AddFolderGrant(string sessionId, string path)
            => HandleFolderGrant(sessionId, path, added: true);

        public Task RemoveFolderGrant(string sessionId, string path)
            => HandleFolderGrant(sessionId, path, added: false);

        private Task HandleFolderGrant(string sessionId, string path, bool added)
        {
            if (!_state.IsAttached(Context.ConnectionId, sessionId))
                throw new HubException("session not attached");

            if (path.StartsWith("/rejected", StringComparison.Ordinal))
                throw new HubException("Grant path does not exist or is not a directory.");

            _state.RecordFolderGrant(path, added);
            return Task.CompletedTask;
        }

        public Task SetSessionModel(string sessionId, string provider, string modelId)
        {
            if (!_state.IsAttached(Context.ConnectionId, sessionId))
                throw new HubException("session not attached");

            if (modelId.StartsWith("rejected", StringComparison.Ordinal))
                throw new HubException($"Model '{modelId}' is not available from provider '{provider}'.");

            _state.RecordModelOverride(provider, modelId);
            return Task.CompletedTask;
        }

        public Task ClearSessionModel(string sessionId)
        {
            if (!_state.IsAttached(Context.ConnectionId, sessionId))
                throw new HubException("session not attached");

            _state.RecordClearModel();
            return Task.CompletedTask;
        }

        public override Task OnDisconnectedAsync(Exception? exception)
        {
            _state.Disconnect(Context.ConnectionId);
            return base.OnDisconnectedAsync(exception);
        }
    }
}
