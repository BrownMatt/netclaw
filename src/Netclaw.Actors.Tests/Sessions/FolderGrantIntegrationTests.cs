// -----------------------------------------------------------------------
// <copyright file="FolderGrantIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Memory;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tests.Tools;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Session-actor tests for the folder-grant lifecycle: validation before
/// persistence, durability across actor restart, immediate revocation, and
/// the grant-change output echo. The grant list is a security boundary —
/// these tests are the constitution's rejection-before-persistence and
/// runtime-matches-persisted-state proof classes.
/// </summary>
public class FolderGrantIntegrationTests : LlmSessionTestBase
{
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.Parse("2026-03-21T12:00:00Z"));

    public FolderGrantIntegrationTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_fakeChatClient));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            IdleTimeout = TimeSpan.FromMinutes(1),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
        services.AddSingleton<MemoryProposalGate>();
        services.AddSingleton<IMemoryCheckpointSink, NullMemoryCheckpointSink>();
        services.AddSingleton<SQLiteMemoryStore>(sp => new SQLiteMemoryStore(
            Path.Combine(Path.GetTempPath(), $"netclaw-grant-tests-{Guid.NewGuid():N}.db"),
            TimeProvider.System));
        services.AddSingleton<IMemoryRecallCoordinator>(sp => new SQLiteMemoryRecallCoordinator(
            sp.GetRequiredService<SQLiteMemoryStore>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SQLiteMemoryRecallCoordinator>.Instance,
            new MemoryConfig(),
            sp.GetRequiredService<TimeProvider>()));

        var registry = new ToolRegistry();
        var toolConfig = new ToolConfig();
        var toolAccessPolicy = TestToolAccessPolicy.Create(toolConfig);
        registry.RegisterCore(new SearchToolsTool(registry, toolAccessPolicy));
        registry.RegisterCore(new LoadToolTool(registry, toolAccessPolicy));
        registry.Register(
            AIFunctionFactory.Create((string path) => $"contents of {path}", "file_read"),
            "file_read");

        services.AddSingleton(registry);
        services.AddSingleton(toolAccessPolicy);
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);
        services.AddSingleton<TimeProvider>(_timeProvider);
        services.AddSingleton<IWorkingContextSnapshotProvider>(new ControllableWorkingContextSnapshotProvider());
        services.AddSingleton<ISessionPipeline>(new UnusedSessionPipeline());
    }

    private static string CreateGrantDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"netclaw-grant-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private async Task RestartSessionActorAsync(SessionId sessionId)
    {
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath)
            .ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        // A dedicated probe keeps the Terminated signal away from TestActor,
        // which also receives JoinSession reply copies.
        var deathWatch = CreateTestProbe("grant-death-watch");
        deathWatch.Watch(child);
        Sys.Stop(child);
        await deathWatch.ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Grant_persists_across_actor_restart_and_removal_revokes()
    {
        var grantDir = CreateGrantDirectory();
        try
        {
            var sessionId = new SessionId("test-channel/grant-restart");
            var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
            var subscriber = CreateTestProbe("grant-sub");
            await JoinSessionAsync(sessionManager, subscriber, sessionId);

            var addReply = await sessionManager.Ask<object>(new AddFolderGrant
            {
                SessionId = sessionId,
                Path = grantDir
            }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            var ack = Assert.IsType<CommandAck>(addReply);
            Assert.Equal(sessionId, ack.SessionId);

            var added = await subscriber.ExpectMsgAsync<FolderGrantOutput>(
                TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(added.IsGranted);
            Assert.Equal(Path.GetFullPath(grantDir), added.Path);
            Assert.Contains(Path.GetFullPath(grantDir), added.GrantedFolders);

            // Restart: the recovered actor must still hold the grant, so a
            // duplicate add is rejected against the persisted list.
            await RestartSessionActorAsync(sessionId);
            var recoverSub = CreateTestProbe("grant-sub-2");
            await JoinSessionAsync(sessionManager, recoverSub, sessionId);

            var duplicateReply = await sessionManager.Ask<object>(new AddFolderGrant
            {
                SessionId = sessionId,
                Path = grantDir
            }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            var duplicateNack = Assert.IsType<CommandNack>(duplicateReply);
            Assert.Contains("already granted", duplicateNack.Reason, StringComparison.OrdinalIgnoreCase);

            // Removal acks after persistence and echoes the emptied list.
            var removeReply = await sessionManager.Ask<object>(new RemoveFolderGrant
            {
                SessionId = sessionId,
                Path = grantDir
            }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            Assert.IsType<CommandAck>(removeReply);

            var removed = await recoverSub.ExpectMsgAsync<FolderGrantOutput>(
                TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(removed.IsGranted);
            Assert.Empty(removed.GrantedFolders);

            // Revocation is durable state: a second removal has nothing to remove.
            var removeAgainReply = await sessionManager.Ask<object>(new RemoveFolderGrant
            {
                SessionId = sessionId,
                Path = grantDir
            }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            var removeAgainNack = Assert.IsType<CommandNack>(removeAgainReply);
            Assert.Contains("not granted", removeAgainNack.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(grantDir, recursive: true);
        }
    }

    [Fact]
    public async Task Runtime_tool_context_reads_the_persisted_grant_list()
    {
        // No-second-store proof: the tool run scope carries exactly the
        // grant list the lifecycle commands persisted — present on the turn
        // after the add, gone on the turn after the remove, no restart.
        var grantDir = CreateGrantDirectory();
        try
        {
            _fakeChatClient.ToolCallsOnFirstCall =
            [
                new FunctionCallContent("grant-call", "file_read",
                    new Dictionary<string, object?> { ["path"] = Path.Combine(grantDir, "a.txt") })
            ];
            _fakeChatClient.PlannedToolCallDecisions.Enqueue(true);   // turn 1: tool call
            _fakeChatClient.PlannedToolCallDecisions.Enqueue(false);  // turn 1: final text
            _fakeChatClient.PlannedToolCallDecisions.Enqueue(true);   // turn 2: tool call
            _fakeChatClient.PlannedToolCallDecisions.Enqueue(false);  // turn 2: final text

            var sessionId = new SessionId("test-channel/grant-runtime");
            var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
            var subscriber = CreateTestProbe("runtime-grant-sub");
            await JoinSessionAsync(sessionManager, subscriber, sessionId);

            var addReply = await sessionManager.Ask<object>(new AddFolderGrant
            {
                SessionId = sessionId,
                Path = grantDir
            }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            Assert.IsType<CommandAck>(addReply);
            await subscriber.ExpectMsgAsync<FolderGrantOutput>(
                TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = "Read the granted file"
            }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            await subscriber.ExpectMsgAsync<TextOutput>(
                TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
            await subscriber.ExpectMsgAsync<TurnCompleted>(
                TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

            var removeReply = await sessionManager.Ask<object>(new RemoveFolderGrant
            {
                SessionId = sessionId,
                Path = grantDir
            }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            Assert.IsType<CommandAck>(removeReply);
            await subscriber.ExpectMsgAsync<FolderGrantOutput>(
                TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = "Read it again"
            }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            await subscriber.ExpectMsgAsync<TextOutput>(
                TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
            await subscriber.ExpectMsgAsync<TurnCompleted>(
                TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

            Assert.Equal(2, _fakeToolExecutor.ObservedGrantedFolders.Count);
            Assert.Contains(Path.GetFullPath(grantDir), _fakeToolExecutor.ObservedGrantedFolders[0]);
            Assert.Empty(_fakeToolExecutor.ObservedGrantedFolders[1]);
        }
        finally
        {
            Directory.Delete(grantDir, recursive: true);
        }
    }

    [Fact]
    public async Task Invalid_grant_is_rejected_before_persistence()
    {
        var sessionId = new SessionId("test-channel/grant-invalid");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("invalid-grant-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        var missingPath = Path.Combine(Path.GetTempPath(), $"netclaw-missing-{Guid.NewGuid():N}");
        var addReply = await sessionManager.Ask<object>(new AddFolderGrant
        {
            SessionId = sessionId,
            Path = missingPath
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<CommandNack>(addReply);

        // Nothing persisted: after an actor restart the recovered grant list
        // does not contain the rejected path.
        await RestartSessionActorAsync(sessionId);
        var recoverSub = CreateTestProbe("invalid-grant-sub-2");
        await JoinSessionAsync(sessionManager, recoverSub, sessionId);

        var removeReply = await sessionManager.Ask<object>(new RemoveFolderGrant
        {
            SessionId = sessionId,
            Path = missingPath
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var nack = Assert.IsType<CommandNack>(removeReply);
        Assert.Contains("not granted", nack.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Grant_for_a_file_is_rejected()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"netclaw-grant-file-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(filePath, "not a directory", TestContext.Current.CancellationToken);
        try
        {
            var sessionId = new SessionId("test-channel/grant-file");
            var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
            var subscriber = CreateTestProbe("file-grant-sub");
            await JoinSessionAsync(sessionManager, subscriber, sessionId);

            var reply = await sessionManager.Ask<object>(new AddFolderGrant
            {
                SessionId = sessionId,
                Path = filePath
            }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            var nack = Assert.IsType<CommandNack>(reply);
            Assert.Contains("not a directory", nack.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(filePath);
        }
    }

    [Theory]
    [InlineData("relative/path")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Malformed_grant_path_is_rejected(string badPath)
    {
        var sessionId = new SessionId("test-channel/grant-malformed");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("malformed-grant-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        var reply = await sessionManager.Ask<object>(new AddFolderGrant
        {
            SessionId = sessionId,
            Path = badPath
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<CommandNack>(reply);
    }

    [Fact]
    public async Task Grant_path_with_control_characters_is_rejected()
    {
        var sessionId = new SessionId("test-channel/grant-control");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("control-grant-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        var grantDir = CreateGrantDirectory();
        try
        {
            var reply = await sessionManager.Ask<object>(new AddFolderGrant
            {
                SessionId = sessionId,
                Path = grantDir + "\n/injected"
            }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            var nack = Assert.IsType<CommandNack>(reply);
            Assert.Contains("control", nack.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(grantDir, recursive: true);
        }
    }
}
