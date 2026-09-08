// -----------------------------------------------------------------------
// <copyright file="SessionRenameIntegrationTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
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
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Session-actor tests for the manual rename: persist + ack, recovery of the
/// locked title, empty-title rejection, and both title-lock layers (skip
/// firing the generator; drop an in-flight generation result).
/// </summary>
public class SessionRenameIntegrationTests : LlmSessionTestBase
{
    private readonly FakeChatClient _chatClient = new();
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.Parse("2026-08-31T12:00:00Z"));
    private readonly RecordingSessionLifecycleObserver _lifecycleObserver = new();

    public SessionRenameIntegrationTests(ITestOutputHelper output) : base(output) { }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_chatClient));
        services.AddSingleton(new ModelCapabilities { ModelId = "configured-model" });
        services.AddSingleton(new SessionConfig
        {
            IdleTimeout = TimeSpan.FromMinutes(1),
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                // Interval 1 arms generation on every turn, so the lock is
                // load-bearing in every test that sends a message.
                TitleGenerationInterval = 1,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
        services.AddSingleton<MemoryProposalGate>();
        services.AddSingleton<IMemoryCheckpointSink, NullMemoryCheckpointSink>();
        services.AddSingleton<SQLiteMemoryStore>(sp => new SQLiteMemoryStore(
            Path.Combine(Path.GetTempPath(), $"netclaw-rename-tests-{Guid.NewGuid():N}.db"),
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

        services.AddSingleton(registry);
        services.AddSingleton(toolAccessPolicy);
        services.AddSingleton<IToolExecutor>(new FakeToolExecutor());
        services.AddSingleton<TimeProvider>(_timeProvider);
        services.AddSingleton<IWorkingContextSnapshotProvider>(new ControllableWorkingContextSnapshotProvider());
        services.AddSingleton<ISessionPipeline>(new UnusedSessionPipeline());
        services.AddSingleton<ISessionLifecycleObserver>(_lifecycleObserver);
    }

    private async Task SendMessageAsync(
        IActorRef sessionManager, SessionId sessionId, string content)
    {
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = content,
            Source = new MessageSource
            {
                ChannelType = ChannelType.SignalR,
                SenderId = new SenderId("synthetic-operator"),
                ChannelId = "synthetic-channel",
                MessageId = Guid.NewGuid().ToString("N"),
                TurnId = new TurnId(Guid.NewGuid().ToString("N")),
                Audience = TrustAudience.Personal,
                Boundary = TrustBoundary.Personal,
                Principal = PrincipalClassification.Operator,
                Provenance = new SourceProvenance(
                    TransportAuthenticity.Verified,
                    PayloadTaint.Trusted),
                ReceivedAt = _timeProvider.GetUtcNow()
            }
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task<IActorRef> ResolveSessionChildAsync(SessionId sessionId)
    {
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        return await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    private async Task RestartSessionActorAsync(SessionId sessionId)
    {
        var child = await ResolveSessionChildAsync(sessionId);
        var deathWatch = CreateTestProbe("rename-death-watch");
        deathWatch.Watch(child);
        Sys.Stop(child);
        await deathWatch.ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Rename_acks_after_persist_emits_the_title_and_survives_recovery()
    {
        var sessionId = new SessionId("signalr/rename-persist");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("rename-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        var reply = await sessionManager.Ask<object>(new RenameSession
        {
            SessionId = sessionId,
            Title = "  My manual title  "
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(reply);

        var titleOutput = await subscriber.FishForMessageAsync(
            m => m is SessionTitleOutput,
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("My manual title", Assert.IsType<SessionTitleOutput>(titleOutput).Title);

        await RestartSessionActorAsync(sessionId);

        var joined = await JoinSessionAsync(
            sessionManager, CreateTestProbe("rename-rejoin"), sessionId);
        Assert.Equal("My manual title", joined.Title);
    }

    [Fact]
    public async Task Rename_with_no_subscriber_still_reaches_the_lifecycle_observer()
    {
        // The daemon's session catalog is the lifecycle observer. A rename
        // from the REST API lands on a session with no attached client, so
        // the title output must reach the observer from the actor itself,
        // not through a client pipeline that does not exist.
        var sessionId = new SessionId("signalr/rename-unattached");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();

        var reply = await sessionManager.Ask<object>(new RenameSession
        {
            SessionId = sessionId,
            Title = "Renamed from the API"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(reply);

        // The ack follows the persist callback that emits the output, so the
        // observer has seen it by the time the ask completes.
        var titleOutput = Assert.Single(
            _lifecycleObserver.Outputs.OfType<SessionTitleOutput>(),
            o => o.SessionId == sessionId);
        Assert.Equal("Renamed from the API", titleOutput.Title);
    }

    [Fact]
    public async Task Empty_rename_nacks_and_leaves_the_title_unchanged()
    {
        var sessionId = new SessionId("signalr/rename-empty");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        await JoinSessionAsync(sessionManager, CreateTestProbe("empty-sub"), sessionId);

        var reply = await sessionManager.Ask<object>(new RenameSession
        {
            SessionId = sessionId,
            Title = "   "
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var nack = Assert.IsType<CommandNack>(reply);
        Assert.Contains("empty", nack.Reason);

        var joined = await JoinSessionAsync(
            sessionManager, CreateTestProbe("empty-rejoin"), sessionId);
        Assert.Null(joined.Title);
    }

    [Fact]
    public async Task Unlocked_session_still_generates_a_title()
    {
        // Control case for the lock tests: with interval 1 and no rename,
        // the generator fires after the first turn and sets a title.
        var sessionId = new SessionId("signalr/rename-control");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("control-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        await SendMessageAsync(sessionManager, sessionId, "hello");

        var titleOutput = await subscriber.FishForMessageAsync(
            m => m is SessionTitleOutput,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(
            Assert.IsType<SessionTitleOutput>(titleOutput).Title));
    }

    [Fact]
    public async Task Locked_title_skips_generation_entirely()
    {
        // Layer 1: the actor must not even fire the sidecar call. The apply
        // path logs "Title generated"; expecting zero of those through the
        // turn proves no generation result was applied.
        var sessionId = new SessionId("signalr/rename-lock-skip");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("lock-skip-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        await sessionManager.Ask<CommandAck>(new RenameSession
        {
            SessionId = sessionId,
            Title = "Locked title"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        await EventFilter.Info(contains: "Title generated")
            .ExpectAsync(0, async () =>
            {
                await SendMessageAsync(sessionManager, sessionId, "hello");
                await subscriber.FishForMessageAsync(
                    m => m is TurnCompleted,
                    TimeSpan.FromSeconds(10),
                    cancellationToken: TestContext.Current.CancellationToken);
            }, cancellationToken: TestContext.Current.CancellationToken);

        var joined = await JoinSessionAsync(
            sessionManager, CreateTestProbe("lock-skip-rejoin"), sessionId);
        Assert.Equal("Locked title", joined.Title);
    }

    [Fact]
    public async Task In_flight_generation_result_is_dropped_after_a_rename()
    {
        // Layer 2: a generation completion that was already in flight when
        // the rename landed must not overwrite the locked title. Delivered
        // straight to the child to simulate the late arrival.
        var sessionId = new SessionId("signalr/rename-lock-drop");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        await JoinSessionAsync(sessionManager, CreateTestProbe("lock-drop-sub"), sessionId);

        await sessionManager.Ask<CommandAck>(new RenameSession
        {
            SessionId = sessionId,
            Title = "Locked title"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var child = await ResolveSessionChildAsync(sessionId);
        await EventFilter.Info(contains: "Title generated")
            .ExpectAsync(0, async () =>
            {
                child.Tell(new TitleGenerationCompleted { Title = "Late generated title" });
                // A follow-up ask through the same mailbox proves the
                // completion above was processed before we assert.
                await child.Ask<CommandNack>(new RenameSession
                {
                    SessionId = sessionId,
                    Title = "  "
                }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
            }, cancellationToken: TestContext.Current.CancellationToken);

        var joined = await JoinSessionAsync(
            sessionManager, CreateTestProbe("lock-drop-rejoin"), sessionId);
        Assert.Equal("Locked title", joined.Title);
    }

    private sealed class SingleClientProvider(Microsoft.Extensions.AI.IChatClient client) : IChatClientProvider
    {
        public Microsoft.Extensions.AI.IChatClient GetClient(ModelRole role) => client;
    }
}
