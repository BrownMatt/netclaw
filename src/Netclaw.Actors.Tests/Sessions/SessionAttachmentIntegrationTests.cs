// -----------------------------------------------------------------------
// <copyright file="SessionAttachmentIntegrationTests.cs" company="Petabridge, LLC">
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
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Session-actor tests for pending uploaded attachments: durable pending
/// state, embed-once at the next user message, restart survival, and the
/// loud failure when the stored file cannot be read at send time.
/// </summary>
public class SessionAttachmentIntegrationTests : LlmSessionTestBase
{
    private readonly FakeChatClient _fakeChatClient = new();
    private readonly FakeToolExecutor _fakeToolExecutor = new();
    private readonly FakeTimeProvider _timeProvider = new(DateTimeOffset.Parse("2026-03-21T12:00:00Z"));

    public SessionAttachmentIntegrationTests(ITestOutputHelper output) : base(output)
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
            Path.Combine(Path.GetTempPath(), $"netclaw-attach-tests-{Guid.NewGuid():N}.db"),
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
            Microsoft.Extensions.AI.AIFunctionFactory.Create((string path) => $"contents of {path}", "file_read"),
            "file_read");

        services.AddSingleton(registry);
        services.AddSingleton(toolAccessPolicy);
        services.AddSingleton<IToolExecutor>(_fakeToolExecutor);
        services.AddSingleton<TimeProvider>(_timeProvider);
        services.AddSingleton<IWorkingContextSnapshotProvider>(new ControllableWorkingContextSnapshotProvider());
        services.AddSingleton<ISessionPipeline>(new UnusedSessionPipeline());
    }

    private string GetSessionDirectory(SessionId sessionId)
    {
        var paths = Host.Services.GetRequiredService<NetclawPaths>();
        return SessionDirectoryHelper.GetSessionDirectory(sessionId, paths.SessionsDirectory);
    }

    private PendingSessionAttachment WriteInboxFile(SessionId sessionId, string fileName, string content)
    {
        var sessionDir = GetSessionDirectory(sessionId);
        var inboxDir = Path.Combine(sessionDir, SessionDirectoryHelper.InboxSubdirectory);
        Directory.CreateDirectory(inboxDir);
        var path = Path.Combine(inboxDir, fileName);
        File.WriteAllText(path, content);
        return new PendingSessionAttachment
        {
            Id = Guid.NewGuid().ToString("N"),
            FileName = fileName,
            RelativePath = $"{SessionDirectoryHelper.InboxSubdirectory}/{fileName}",
            MimeType = "text/plain",
            Category = "Document",
            SizeBytes = content.Length,
            StoredAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };
    }

    private async Task SendAndDrainTurnAsync(
        IActorRef sessionManager,
        Akka.TestKit.TestProbe subscriber,
        SessionId sessionId,
        string content)
    {
        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = content
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TextOutput>(
            TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
    }

    // Sidecar calls (observer, memory extraction) share the fake chat
    // client, so tests locate their turn by content instead of call index.
    private string FindUserContent(string marker)
        => _fakeChatClient.ReceivedMessages
            .SelectMany(call => call)
            .Where(m => m.Role == Microsoft.Extensions.AI.ChatRole.User)
            .Select(m => m.Text)
            .First(text => text.Contains(marker, StringComparison.Ordinal));

    private bool AnyUserContentContains(string marker)
        => _fakeChatClient.ReceivedMessages
            .SelectMany(call => call)
            .Any(m => m.Role == Microsoft.Extensions.AI.ChatRole.User
                      && m.Text.Contains(marker, StringComparison.Ordinal));

    [Fact]
    public async Task Pending_attachment_rides_the_next_message_only()
    {
        var sessionId = new SessionId("test-channel/attach-once");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("attach-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        var attachment = WriteInboxFile(sessionId, "notes.txt", "attachment body");
        var reply = await sessionManager.Ask<object>(new AddPendingAttachment
        {
            SessionId = sessionId,
            Attachment = attachment
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(reply);

        await SendAndDrainTurnAsync(sessionManager, subscriber, sessionId, "Look at this file");
        await SendAndDrainTurnAsync(sessionManager, subscriber, sessionId, "And a follow-up");

        var firstUser = FindUserContent("Look at this file");
        Assert.Contains("[attachment] name=\"notes.txt\"", firstUser);
        Assert.Contains("inbox/notes.txt", firstUser);

        // The second turn's NEW user message carries no attachment line. The
        // first turn's message stays in history unchanged — embed-once means
        // no re-embed, not history rewriting.
        var secondUser = FindUserContent("And a follow-up");
        Assert.DoesNotContain("[attachment]", secondUser);
    }

    [Fact]
    public async Task Pending_attachment_survives_actor_restart()
    {
        var sessionId = new SessionId("test-channel/attach-restart");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("attach-restart-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        var attachment = WriteInboxFile(sessionId, "report.txt", "durable body");
        var reply = await sessionManager.Ask<object>(new AddPendingAttachment
        {
            SessionId = sessionId,
            Attachment = attachment
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(reply);

        // Kill and recover the session actor between upload and send.
        var escapedId = Uri.EscapeDataString(sessionId.Value);
        var child = await Sys.ActorSelection($"/user/session-manager/{escapedId}")
            .ResolveOne(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var deathWatch = CreateTestProbe("attach-death-watch");
        deathWatch.Watch(child);
        Sys.Stop(child);
        await deathWatch.ExpectTerminatedAsync(child, cancellationToken: TestContext.Current.CancellationToken);

        var recoverSub = CreateTestProbe("attach-restart-sub-2");
        await JoinSessionAsync(sessionManager, recoverSub, sessionId);

        await SendAndDrainTurnAsync(sessionManager, recoverSub, sessionId, "Send after restart");

        var userContent = FindUserContent("Send after restart");
        Assert.Contains("[attachment] name=\"report.txt\"", userContent);
    }

    [Fact]
    public async Task Attachment_grants_no_filesystem_authority()
    {
        // The pending record carries an inbox-relative path only. The tool
        // run scope's granted-folder list stays empty, so the policy's
        // default-deny outcome for the upload's source directory is
        // unchanged (denial itself is proven in the policy tests).
        var sessionId = new SessionId("test-channel/attach-no-authority");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("attach-authority-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        var attachment = WriteInboxFile(sessionId, "private-report.txt", "uploaded content");
        await sessionManager.Ask<object>(new AddPendingAttachment
        {
            SessionId = sessionId,
            Attachment = attachment
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        _fakeChatClient.ToolCallsOnFirstCall =
        [
            new Microsoft.Extensions.AI.FunctionCallContent("attach-call", "file_read",
                new Dictionary<string, object?> { ["path"] = "/home/user/private/other.txt" })
        ];
        _fakeChatClient.PlannedToolCallDecisions.Enqueue(true);
        _fakeChatClient.PlannedToolCallDecisions.Enqueue(false);

        await SendAndDrainTurnAsync(sessionManager, subscriber, sessionId, "Read the other file");

        var observed = Assert.Single(_fakeToolExecutor.ObservedGrantedFolders);
        Assert.Empty(observed);
    }

    [Fact]
    public async Task Unreadable_attachment_fails_the_send_loudly()
    {
        var sessionId = new SessionId("test-channel/attach-unreadable");
        var sessionManager = ActorRegistry.Get<SessionManagerActorKey>();
        var subscriber = CreateTestProbe("attach-fail-sub");
        await JoinSessionAsync(sessionManager, subscriber, sessionId);

        // Pending reference whose inbox file was never written.
        var reply = await sessionManager.Ask<object>(new AddPendingAttachment
        {
            SessionId = sessionId,
            Attachment = new PendingSessionAttachment
            {
                Id = Guid.NewGuid().ToString("N"),
                FileName = "missing.txt",
                RelativePath = $"{SessionDirectoryHelper.InboxSubdirectory}/missing.txt",
                MimeType = "text/plain",
                Category = "Document",
                SizeBytes = 12,
                StoredAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            }
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.IsType<CommandAck>(reply);

        await sessionManager.Ask<CommandAck>(new SendUserMessage
        {
            SessionId = sessionId,
            Content = "This should fail"
        }, TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);

        var error = await subscriber.ExpectMsgAsync<ErrorOutput>(
            TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Contains("missing.txt", error.Message);

        var completed = await subscriber.ExpectMsgAsync<TurnCompleted>(
            TimeSpan.FromSeconds(3), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(TurnOutcome.Skipped, completed.Outcome);

        // The message never reached the model.
        Assert.False(AnyUserContentContains("This should fail"));

        // Loud failure keeps the pending reference: once the file exists,
        // the next send embeds it.
        WriteInboxFile(sessionId, "missing.txt", "now present");
        await SendAndDrainTurnAsync(sessionManager, subscriber, sessionId, "Retry now");
        var userContent = FindUserContent("Retry now");
        Assert.Contains("[attachment] name=\"missing.txt\"", userContent);
    }
}
