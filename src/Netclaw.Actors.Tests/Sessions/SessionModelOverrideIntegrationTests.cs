// -----------------------------------------------------------------------
// <copyright file="SessionModelOverrideIntegrationTests.cs" company="Petabridge, LLC">
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
using Netclaw.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Session-actor tests for the per-session model override: routing swap on
/// set, restore on clear, loud rejection without a routing change, the
/// restart-clears lifetime, join-snapshot mirroring, and the capability
/// re-resolve that follows the active model.
/// </summary>
public class SessionModelOverrideIntegrationTests : LlmSessionTestBase
{
    private readonly FakeChatClient _defaultClient = new();
    private readonly FakeChatClient _overrideClient = new();
    private readonly RecordingClientProvider _clientProvider;
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.Parse("2026-08-31T12:00:00Z"));

    public SessionModelOverrideIntegrationTests(ITestOutputHelper output) : base(output)
    {
        _clientProvider = new RecordingClientProvider(_defaultClient, _overrideClient);
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(_clientProvider);
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "configured-model",
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
            Path.Combine(Path.GetTempPath(), $"netclaw-model-override-tests-{Guid.NewGuid():N}.db"),
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

    private async Task AwaitTurnAsync(Akka.TestKit.TestProbe subscriber)
    {
        await subscriber.FishForMessageAsync(
            m => m is TurnCompleted,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private async Task RestartSessionActorAsync(SessionId sessionId)
    {
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var childPath = $"/user/session-manager/{escapedId}";
        var child = await Sys.ActorSelection(childPath)
            .ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var deathWatch = CreateTestProbe("override-death-watch");
        deathWatch.Watch(child);
        Sys.Stop(child);
        await deathWatch.ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Set_routes_the_next_main_call_to_the_override_client()
    {
        var sessionId = new SessionId("signalr/model-override-routing");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("override-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        await SendMessageAsync(sessionManager, sessionId, "before override");
        await AwaitTurnAsync(subscriber);
        Assert.Equal(1, _defaultClient.CallCount);
        Assert.Equal(0, _overrideClient.CallCount);

        var reply = await sessionManager.Ask<object>(new SetSessionModel
        {
            SessionId = sessionId,
            Provider = "local-ollama",
            ModelId = "override-model"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(reply);

        var overrideOutput = await subscriber.FishForMessageAsync(
            m => m is ModelOverrideOutput,
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        var output = Assert.IsType<ModelOverrideOutput>(overrideOutput);
        Assert.Equal("local-ollama", output.Provider);
        Assert.Equal("override-model", output.ModelId);

        await SendMessageAsync(sessionManager, sessionId, "after override");
        await AwaitTurnAsync(subscriber);
        Assert.Equal(1, _defaultClient.CallCount);
        Assert.Equal(1, _overrideClient.CallCount);

        // A late joiner learns the active override from the join snapshot.
        var lateJoiner = CreateTestProbe("override-late-joiner");
        var joined = await JoinSessionAsync(sessionManager, lateJoiner, sessionId);
        Assert.Equal("local-ollama", joined.ModelOverrideProvider);
        Assert.Equal("override-model", joined.ModelOverrideId);
    }

    [Fact]
    public async Task Restart_clears_the_override()
    {
        var sessionId = new SessionId("signalr/model-override-restart");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("restart-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        var reply = await sessionManager.Ask<object>(new SetSessionModel
        {
            SessionId = sessionId,
            Provider = "local-ollama",
            ModelId = "override-model"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(reply);

        await RestartSessionActorAsync(sessionId);

        var recoverSub = CreateTestProbe("restart-sub-2");
        var joined = await JoinSessionAsync(sessionManager, recoverSub, sessionId);
        Assert.Null(joined.ModelOverrideProvider);
        Assert.Null(joined.ModelOverrideId);

        await SendMessageAsync(sessionManager, sessionId, "after restart");
        await AwaitTurnAsync(recoverSub);
        Assert.Equal(1, _defaultClient.CallCount);
        Assert.Equal(0, _overrideClient.CallCount);
    }

    [Fact]
    public async Task Clear_restores_configured_routing_and_double_clear_nacks()
    {
        var sessionId = new SessionId("signalr/model-override-clear");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("clear-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        await sessionManager.Ask<object>(new SetSessionModel
        {
            SessionId = sessionId,
            Provider = "local-ollama",
            ModelId = "override-model"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var clearReply = await sessionManager.Ask<object>(new ClearSessionModel
        {
            SessionId = sessionId
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(clearReply);

        var cleared = await subscriber.FishForMessageAsync(
            m => m is ModelOverrideOutput { Provider: null, ModelId: null },
            TimeSpan.FromSeconds(3),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<ModelOverrideOutput>(cleared);

        await SendMessageAsync(sessionManager, sessionId, "after clear");
        await AwaitTurnAsync(subscriber);
        Assert.Equal(1, _defaultClient.CallCount);
        Assert.Equal(0, _overrideClient.CallCount);

        var doubleClear = await sessionManager.Ask<object>(new ClearSessionModel
        {
            SessionId = sessionId
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var nack = Assert.IsType<CommandNack>(doubleClear);
        Assert.Contains("No model override", nack.Reason);
    }

    [Fact]
    public async Task Rejected_set_leaves_routing_unchanged()
    {
        var sessionId = new SessionId("signalr/model-override-reject");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("reject-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        var blankReply = await sessionManager.Ask<object>(new SetSessionModel
        {
            SessionId = sessionId,
            Provider = "local-ollama",
            ModelId = "   "
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<CommandNack>(blankReply);

        // The provider throws for this selector — pipeline creation failure
        // must reject loudly, naming the model, with routing unchanged.
        var throwReply = await sessionManager.Ask<object>(new SetSessionModel
        {
            SessionId = sessionId,
            Provider = RecordingClientProvider.ThrowingProvider,
            ModelId = "ghost-model"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        var nack = Assert.IsType<CommandNack>(throwReply);
        Assert.Contains("ghost-model", nack.Reason);

        await SendMessageAsync(sessionManager, sessionId, "still configured");
        await AwaitTurnAsync(subscriber);
        Assert.Equal(1, _defaultClient.CallCount);
        Assert.Equal(0, _overrideClient.CallCount);

        var joined = await JoinSessionAsync(
            sessionManager, CreateTestProbe("reject-joiner"), sessionId);
        Assert.Null(joined.ModelOverrideProvider);
    }

    [Fact]
    public async Task Capability_re_resolution_swaps_the_context_window()
    {
        // The base test kit's FakeCapabilityResolver reports modalities but no
        // context window, so the override falls back to the conservative
        // default (32_768) — observable on the usage line, and distinct from
        // the configured model's 128_000.
        _overrideClient.IncludeThinking = true;
        _defaultClient.IncludeThinking = true;

        var sessionId = new SessionId("signalr/model-override-caps");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("caps-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId, OutputFilter.TextAndUsage);

        await SendMessageAsync(sessionManager, sessionId, "before override");
        var beforeUsage = await subscriber.FishForMessageAsync(
            m => m is UsageOutput,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(128_000, Assert.IsType<UsageOutput>(beforeUsage).ContextWindowTokens);
        await AwaitTurnAsync(subscriber);

        // The capability response lands asynchronously after the ack; the
        // actor logs when it applies, so gate on that log for determinism.
        await EventFilter.Info(contains: "Override model capabilities active")
            .ExpectAsync(1, async () =>
            {
                var reply = await sessionManager.Ask<object>(new SetSessionModel
                {
                    SessionId = sessionId,
                    Provider = "local-ollama",
                    ModelId = "override-model"
                }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
                Assert.IsType<CommandAck>(reply);
            }, cancellationToken: TestContext.Current.CancellationToken);

        await SendMessageAsync(sessionManager, sessionId, "after override");
        var afterUsage = await subscriber.FishForMessageAsync(
            m => m is UsageOutput,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(32_768, Assert.IsType<UsageOutput>(afterUsage).ContextWindowTokens);
    }

    /// <summary>
    /// Role-based resolution returns the default client; a context that
    /// carries an override returns the override client. The
    /// <see cref="ThrowingProvider"/> provider key simulates a pipeline
    /// creation failure (unknown provider).
    /// </summary>
    private sealed class RecordingClientProvider(IChatClient defaultClient, IChatClient overrideClient)
        : IChatClientProvider
    {
        public const string ThrowingProvider = "ghost-provider";

        public IChatClient GetClient(ModelRole role) => defaultClient;

        public IChatClient GetClient(ChatRoutingContext context)
        {
            if (context.OverrideModel is not { } model)
                return defaultClient;
            if (model.Provider == ThrowingProvider)
                throw new InvalidOperationException($"Provider '{model.Provider}' not found.");
            return overrideClient;
        }
    }
}
