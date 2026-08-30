// -----------------------------------------------------------------------
// <copyright file="SessionAttachmentServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Gateway;

/// <summary>
/// Upload-endpoint gate tests: unknown session, size limit before storage,
/// category policy, and the pending-reference handoff to the session actor.
/// </summary>
public sealed class SessionAttachmentServiceTests : IDisposable
{
    private readonly string _tempBase = Path.Combine(Path.GetTempPath(), $"netclaw-attach-svc-{Guid.NewGuid():N}");
    private readonly NetclawPaths _paths;
    private readonly SessionCatalogService _catalog;
    private readonly RecordingPipeline _pipeline = new();

    public SessionAttachmentServiceTests()
    {
        _paths = new NetclawPaths(_tempBase);
        _paths.EnsureDirectoriesExist();
        _catalog = new SessionCatalogService(_paths, TimeProvider.System, NullLogger<SessionCatalogService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempBase))
            Directory.Delete(_tempBase, recursive: true);
    }

    private SessionAttachmentService CreateService(ToolConfig? toolConfig = null)
        => new(
            toolConfig ?? new ToolConfig(),
            _paths,
            _pipeline,
            _catalog,
            TimeProvider.System,
            NullLogger<SessionAttachmentService>.Instance);

    private SessionId RegisterSession(string id)
    {
        var sessionId = new SessionId(id);
        _catalog.OnSessionActivated(sessionId, ChannelType.Tui);
        return sessionId;
    }

    private static MemoryStream Utf8(string content) => new(Encoding.UTF8.GetBytes(content));

    [Fact]
    public async Task Upload_succeeds_and_persists_pending_reference()
    {
        var sessionId = RegisterSession("signalr/upload-ok");
        var service = CreateService();

        var outcome = await service.UploadAsync(
            sessionId.Value, "notes.txt", "text/plain", Utf8("hello attachment"), CancellationToken.None);

        var accepted = Assert.IsType<AttachmentUploadOutcome.Accepted>(outcome);
        Assert.Equal("notes.txt", accepted.Result.FileName);
        Assert.Equal("inbox/notes.txt", accepted.Result.RelativePath);
        Assert.Equal("text/plain", accepted.Result.MimeType);

        // The file landed in the session inbox.
        var sessionDir = SessionDirectoryHelper.GetSessionDirectory(sessionId, _paths.SessionsDirectory);
        var storedPath = Path.Combine(sessionDir, "inbox", "notes.txt");
        Assert.Equal("hello attachment", await File.ReadAllTextAsync(storedPath, TestContext.Current.CancellationToken));

        // The pending reference reached the session actor with matching fields.
        var command = Assert.IsType<AddPendingAttachment>(Assert.Single(_pipeline.AwaitedCommands));
        Assert.Equal(sessionId, command.SessionId);
        Assert.Equal(accepted.Result.AttachmentId, command.Attachment.Id);
        Assert.Equal("inbox/notes.txt", command.Attachment.RelativePath);
    }

    [Fact]
    public async Task Unknown_session_is_rejected_and_nothing_is_stored()
    {
        var service = CreateService();

        var outcome = await service.UploadAsync(
            "signalr/does-not-exist", "notes.txt", "text/plain", Utf8("body"), CancellationToken.None);

        Assert.IsType<AttachmentUploadOutcome.UnknownSession>(outcome);
        Assert.Empty(_pipeline.AwaitedCommands);

        var sessionDir = SessionDirectoryHelper.GetSessionDirectory(
            new SessionId("signalr/does-not-exist"), _paths.SessionsDirectory);
        Assert.False(Directory.Exists(Path.Combine(sessionDir, "inbox")));
    }

    [Fact]
    public async Task Oversize_upload_is_rejected_before_storage()
    {
        var sessionId = RegisterSession("signalr/upload-oversize");
        var toolConfig = new ToolConfig();
        toolConfig.AudienceProfiles.Personal.ChannelAttachments.MaxFileBytes = 8;
        var service = CreateService(toolConfig);

        var outcome = await service.UploadAsync(
            sessionId.Value, "big.txt", "text/plain", Utf8("this is more than eight bytes"), CancellationToken.None);

        var rejected = Assert.IsType<AttachmentUploadOutcome.Rejected>(outcome);
        Assert.Contains("limit", rejected.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_pipeline.AwaitedCommands);

        // No partial attachment remains — inbox empty, staging cleaned.
        var sessionDir = SessionDirectoryHelper.GetSessionDirectory(sessionId, _paths.SessionsDirectory);
        var inboxDir = Path.Combine(sessionDir, "inbox");
        Assert.True(!Directory.Exists(inboxDir) || Directory.GetFiles(inboxDir).Length == 0);
        var stagingDir = SessionDirectoryHelper.GetOrCreateAttachmentStagingDirectory(
            sessionId, _paths.SessionsDirectory);
        Assert.Empty(Directory.GetFiles(stagingDir));
    }

    [Fact]
    public async Task Disallowed_category_is_rejected()
    {
        var sessionId = RegisterSession("signalr/upload-category");
        var toolConfig = new ToolConfig();
        toolConfig.AudienceProfiles.Personal.ChannelAttachments = ChannelAttachmentPolicy.Empty;
        var service = CreateService(toolConfig);

        var outcome = await service.UploadAsync(
            sessionId.Value, "notes.txt", "text/plain", Utf8("body"), CancellationToken.None);

        var rejected = Assert.IsType<AttachmentUploadOutcome.Rejected>(outcome);
        Assert.Contains("not allowed", rejected.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_pipeline.AwaitedCommands);
    }

    [Fact]
    public async Task Actor_rejection_removes_the_stored_file()
    {
        var sessionId = RegisterSession("signalr/upload-nack");
        _pipeline.Response = feedback => new CommandNack(feedback.SessionId, "Attachment reference is incomplete.");
        var service = CreateService();

        var outcome = await service.UploadAsync(
            sessionId.Value, "notes.txt", "text/plain", Utf8("body"), CancellationToken.None);

        Assert.IsType<AttachmentUploadOutcome.Rejected>(outcome);
        var sessionDir = SessionDirectoryHelper.GetSessionDirectory(sessionId, _paths.SessionsDirectory);
        Assert.Empty(Directory.GetFiles(Path.Combine(sessionDir, "inbox")));
    }

    private sealed class RecordingPipeline : ISessionPipeline
    {
        public List<IWithSessionId> AwaitedCommands { get; } = [];

        public Func<IWithSessionId, ISessionResponse>? Response { get; set; }

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            Akka.Streams.IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
        {
            AwaitedCommands.Add(feedback);
            return Task.FromResult(Response?.Invoke(feedback) ?? CommandAck.For(feedback.SessionId));
        }
    }
}
