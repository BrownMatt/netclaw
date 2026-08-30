// -----------------------------------------------------------------------
// <copyright file="ToolErrorNudgeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Tools;
using Netclaw.Configuration;
using Netclaw.Tools;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Covers the tool-error follow-up nudge (turn-loop-governance): an error tool
/// result appends one system nudge before the next model invocation, and the
/// nudge carries no tool arguments. The disabled variant proves the kill
/// switch restores current behavior.
/// </summary>
public class ToolErrorNudgeTests : LlmSessionTestBase
{
    private const string NudgeMarker = "Do not retry the same tool with identical arguments";
    private const string SecretArgumentValue = "arg-value-should-not-leak";

    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();

    public ToolErrorNudgeTests(ITestOutputHelper output) : base(output)
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
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);

        var registry = new ToolRegistry();
        registry.RegisterCore(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Error_receipt_appends_nudge_before_next_invocation_without_arguments()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = SecretArgumentValue })
        ];
        _fakeToolExecutor.Results["web_search"] = "Error: MCP tool 'web_search' reported a failure";
        _fakeToolExecutor.Receipts["web_search"] =
            new ToolInvocationReceipt(ToolInvocationOutcomeCategory.TransientFailure);

        var sessionId = new SessionId("test-channel/tool-error-nudge");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("tool-error-nudge-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for something"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, _fakeChatClient.CallCount);

        // The second invocation must contain exactly one nudge message, and the
        // nudge must not echo the tool's arguments.
        var secondCall = _fakeChatClient.ReceivedMessages[1];
        var nudges = secondCall
            .Where(m => m.Text.Contains(NudgeMarker, StringComparison.Ordinal))
            .ToList();
        var nudge = Assert.Single(nudges);
        Assert.DoesNotContain(SecretArgumentValue, nudge.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Success_receipt_appends_no_nudge()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "ok" })
        ];
        _fakeToolExecutor.Results["web_search"] = "search result";
        _fakeToolExecutor.Receipts["web_search"] =
            new ToolInvocationReceipt(ToolInvocationOutcomeCategory.Success);

        var sessionId = new SessionId("test-channel/tool-success-no-nudge");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("tool-success-no-nudge-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for something"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain(_fakeChatClient.ReceivedMessages[1],
            m => m.Text.Contains(NudgeMarker, StringComparison.Ordinal));
    }
}

/// <summary>
/// Kill-switch variant: with <see cref="SessionConfig.ToolErrorNudgeEnabled"/>
/// false, an error receipt appends no nudge (fake-failure proof that disabling
/// restores current behavior).
/// </summary>
public class ToolErrorNudgeDisabledTests : LlmSessionTestBase
{
    private const string NudgeMarker = "Do not retry the same tool with identical arguments";

    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();

    public ToolErrorNudgeDisabledTests(ITestOutputHelper output) : base(output)
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
            ToolErrorNudgeEnabled = false,
            Tuning = new SessionTuning
            {
                SnapshotInterval = 5,
                TitleGenerationInterval = 0,
            }
        });
        services.AddSingleton<ISystemPromptProvider>(new StaticSystemPromptProvider(
            "You are a test assistant with tools."));
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);

        var registry = new ToolRegistry();
        registry.RegisterCore(
            AIFunctionFactory.Create(() => "search result", "web_search"),
            "web_search");
        services.AddSingleton(registry);
    }

    [Fact]
    public async Task Disabled_switch_appends_no_nudge_on_error_receipt()
    {
        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new FunctionCallContent("call-1", "web_search",
                new Dictionary<string, object?> { ["query"] = "test" })
        ];
        _fakeToolExecutor.Results["web_search"] = "Error: MCP tool 'web_search' reported a failure";
        _fakeToolExecutor.Receipts["web_search"] =
            new ToolInvocationReceipt(ToolInvocationOutcomeCategory.TransientFailure);

        var sessionId = new SessionId("test-channel/tool-error-nudge-disabled");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("tool-error-nudge-disabled-sub");

        await sessionManager.Ask<SessionJoined>(new JoinSession(subscriber)
        {
            SessionId = sessionId,
            Filter = OutputFilter.Full
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<SessionJoined>(cancellationToken: TestContext.Current.CancellationToken);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "Search for something"
        }, TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await subscriber.ExpectMsgAsync<ToolCallOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<ToolResultOutput>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, _fakeChatClient.CallCount);
        Assert.DoesNotContain(_fakeChatClient.ReceivedMessages[1],
            m => m.Text.Contains(NudgeMarker, StringComparison.Ordinal));
    }
}
