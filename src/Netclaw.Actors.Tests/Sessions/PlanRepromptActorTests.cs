// -----------------------------------------------------------------------
// <copyright file="PlanRepromptActorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Runtime.CompilerServices;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Configuration;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using AiChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Session-actor coverage for the plan-without-action re-prompt
/// (turn-loop-governance): enabled, a plan-only reply is re-prompted and only
/// the executed answer reaches the user; disabled (the default), the plan-only
/// reply passes through unchanged and a would-have-fired event is logged.
/// </summary>
public class PlanRepromptActorTests : LlmSessionTestBase
{
    private const string PlanOnlyReply = "I'll search the web for that now.";
    private const string FinalReply = "The answer is 42.";
    private const string NudgeMarker = "Execute the plan now";

    private readonly ScriptedTextChatClient _client = new(PlanOnlyReply, FinalReply);

    public PlanRepromptActorTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_client));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            PlanRepromptEnabled = true,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
    }

    [Fact]
    public async Task Enabled_reprompts_plan_only_reply_and_delivers_the_executed_answer()
    {
        var sessionId = new SessionId("test-channel/plan-reprompt");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("plan-reprompt-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Find the answer"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var text = await subscriber.FishForMessageAsync(
            m => m is TextOutput,
            TimeSpan.FromSeconds(10),
            cancellationToken: TestContext.Current.CancellationToken) as TextOutput;
        Assert.NotNull(text);
        Assert.Equal(FinalReply, text.Text);

        await subscriber.FishForMessageAsync(
            m => m is TurnCompleted,
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, _client.CallCount);
        Assert.Contains(_client.ReceivedMessages[1],
            m => m.Text.Contains(NudgeMarker, StringComparison.Ordinal));
    }

    /// <summary>
    /// Streams one scripted text reply per call; further calls repeat the last
    /// script entry.
    /// </summary>
    internal sealed class ScriptedTextChatClient(params string[] replies) : IChatClient
    {
        private readonly object _gate = new();
        private readonly List<IReadOnlyList<AiChatMessage>> _receivedMessages = [];
        private int _callCount;

        public int CallCount => _callCount;

        public IReadOnlyList<IReadOnlyList<AiChatMessage>> ReceivedMessages
        {
            get { lock (_gate) { return _receivedMessages.ToArray(); } }
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<AiChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            lock (_gate)
            {
                _receivedMessages.Add(messages.ToList());
            }

            var reply = replies[Math.Min(call, replies.Length) - 1];
            yield return new ChatResponseUpdate
            {
                Role = Microsoft.Extensions.AI.ChatRole.Assistant,
                Contents = [new TextContent(reply)]
            };
            await Task.Yield();
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<AiChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Streaming only.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}

/// <summary>
/// Default-config variant: with <see cref="SessionConfig.PlanRepromptEnabled"/>
/// false, the plan-only reply is delivered unchanged and the would-have-fired
/// observability event is logged.
/// </summary>
public class PlanRepromptDisabledActorTests : LlmSessionTestBase
{
    private const string PlanOnlyReply = "I'll search the web for that now.";

    private readonly PlanRepromptActorTests.ScriptedTextChatClient _client = new(PlanOnlyReply);

    public PlanRepromptDisabledActorTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_client));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant."));
    }

    [Fact]
    public async Task Disabled_delivers_plan_only_reply_and_logs_observe_event()
    {
        var sessionId = new SessionId("test-channel/plan-reprompt-disabled");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("plan-reprompt-disabled-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        TextOutput? text = null;
        await EventFilter.Info(contains: "turn_plan_without_action").ExpectAsync(1, async () =>
        {
            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = "Find the answer"
            }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            text = await subscriber.FishForMessageAsync(
                m => m is TextOutput,
                TimeSpan.FromSeconds(10),
                cancellationToken: TestContext.Current.CancellationToken) as TextOutput;
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(text);
        Assert.Equal(PlanOnlyReply, text.Text);
        Assert.Equal(1, _client.CallCount);
    }
}
