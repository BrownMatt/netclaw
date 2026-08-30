// -----------------------------------------------------------------------
// <copyright file="ThinkingCapActorTests.cs" company="Petabridge, LLC">
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
/// Session-actor coverage for the per-response thinking cap
/// (turn-loop-governance): a runaway thinking stream is cut at the cap and
/// re-prompted once; a second breach in the same turn fails the turn with an
/// operator-visible message that names the cap. Models the 2026-08-27
/// production runaway (session signalr_f25ac78233a84eda93f745edcf2a3868).
/// </summary>
public class ThinkingCapActorTests : LlmSessionTestBase
{
    private const string ActOrReportMarker = "Act now";
    private const string FailureMarker = "thinking cap";

    private readonly EndlessThinkingChatClient _endlessThinking = new();

    public ThinkingCapActorTests(ITestOutputHelper output) : base(output)
    {
    }

    protected override void ConfigureSessionServices(IServiceCollection services)
    {
        services.AddSingleton<IChatClientProvider>(new SingleClientProvider(_endlessThinking));
        services.AddSingleton(new ModelCapabilities
        {
            ModelId = "fake-model",
            ContextWindowTokens = 128_000,
        });
        services.AddSingleton(new SessionConfig
        {
            ThinkingCapEnabled = true,
            ThinkingCapChars = 200,
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
    public async Task Runaway_thinking_is_cut_reprompted_once_then_fails_the_turn()
    {
        var sessionId = new SessionId("test-channel/thinking-cap");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("thinking-cap-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        ErrorOutput? error = null;

        // One breach log record per breach: the first breach re-prompts, the
        // second fails the turn — exactly two turn_thinking_cap_breach records.
        await EventFilter.Warning(contains: "turn_thinking_cap_breach").ExpectAsync(2, async () =>
        {
            await sessionManager.Ask<CommandAck>(new SendUserMessage
            {
                SessionId = sessionId,
                Content = "Think forever"
            }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

            // The turn must terminate with an operator-visible error naming the
            // cap (thinking deltas stream first; fish past them).
            error = await subscriber.FishForMessageAsync(
                m => m is ErrorOutput,
                TimeSpan.FromSeconds(10),
                cancellationToken: TestContext.Current.CancellationToken) as ErrorOutput;
        }, cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(error);
        Assert.Contains(FailureMarker, error.Message, StringComparison.OrdinalIgnoreCase);

        await subscriber.FishForMessageAsync(
            m => m is TurnCompleted,
            TimeSpan.FromSeconds(5),
            cancellationToken: TestContext.Current.CancellationToken);

        // Exactly two model invocations: the breached first response and the
        // one cap re-prompt, whose breach ends the turn.
        Assert.Equal(2, _endlessThinking.CallCount);

        // The re-prompt carried the act-or-report nudge.
        var secondCall = _endlessThinking.ReceivedMessages[1];
        Assert.Contains(secondCall, m => m.Text.Contains(ActOrReportMarker, StringComparison.Ordinal));
    }

    /// <summary>
    /// Streams 50-char thinking chunks until the consumer stops enumerating.
    /// No Task.Delay: latency is irrelevant, the cap is char-driven.
    /// </summary>
    private sealed class EndlessThinkingChatClient : IChatClient
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
            Interlocked.Increment(ref _callCount);
            lock (_gate)
            {
                _receivedMessages.Add(messages.ToList());
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                yield return new ChatResponseUpdate
                {
                    Role = Microsoft.Extensions.AI.ChatRole.Assistant,
                    Contents = [new TextReasoningContent(new string('t', 50))]
                };
                await Task.Yield();
            }
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
