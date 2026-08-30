// -----------------------------------------------------------------------
// <copyright file="SessionAttachmentService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Media;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Result of one attachment upload attempt.
/// </summary>
public abstract record AttachmentUploadOutcome
{
    public sealed record Accepted(SessionAttachmentUploadResult Result) : AttachmentUploadOutcome;

    public sealed record UnknownSession(string Reason) : AttachmentUploadOutcome;

    public sealed record Rejected(string Reason) : AttachmentUploadOutcome;
}

/// <summary>
/// Wire result for an accepted upload. <c>AttachmentId</c> identifies the
/// pending reference; <c>RelativePath</c> is the session-directory-relative
/// inbox path the embed will reference.
/// </summary>
public sealed record SessionAttachmentUploadResult(
    string AttachmentId,
    string FileName,
    string RelativePath,
    string MimeType,
    long SizeBytes);

/// <summary>
/// Handles operator attachment uploads for <c>POST /api/sessions/attachments</c>.
///
/// Security shape: the endpoint shares the daemon control surface's exposure
/// and authentication (operator-authenticated callers only), and the upload is
/// gated by the Personal audience's <see cref="ChannelAttachmentPolicy"/> —
/// the same category and size policy that gates inbound channel attachments.
/// The file lands in the session inbox, which is already inside the session's
/// authorized directory; an attachment never adds path authority.
///
/// Known trade-off (operator-only surface): two operator clients attached to
/// one session can interleave uploads and sends; the pending list is consumed
/// by whichever message arrives next.
/// </summary>
public sealed class SessionAttachmentService
{
    private readonly ToolConfig _toolConfig;
    private readonly NetclawPaths _paths;
    private readonly ISessionPipeline _pipeline;
    private readonly SessionCatalogService _catalog;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionAttachmentService> _logger;

    public SessionAttachmentService(
        ToolConfig toolConfig,
        NetclawPaths paths,
        ISessionPipeline pipeline,
        SessionCatalogService catalog,
        TimeProvider timeProvider,
        ILogger<SessionAttachmentService> logger)
    {
        _toolConfig = toolConfig;
        _paths = paths;
        _pipeline = pipeline;
        _catalog = catalog;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<AttachmentUploadOutcome> UploadAsync(
        string sessionId,
        string fileName,
        string? declaredContentType,
        Stream content,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return new AttachmentUploadOutcome.UnknownSession("Session id is required.");

        if (string.IsNullOrWhiteSpace(fileName))
            return new AttachmentUploadOutcome.Rejected("A file name is required.");

        if (!_catalog.SessionExists(sessionId))
            return new AttachmentUploadOutcome.UnknownSession($"Session '{sessionId}' not found.");

        // Operator uploads are gated by the Personal audience attachment
        // policy — one policy for every inbound attachment surface.
        var policy = _toolConfig.AudienceProfiles.Personal.ChannelAttachments;
        var mimeType = MimeTypeCatalog.NormalizeDeclaredForExtension(
            declaredContentType, Path.GetExtension(fileName));
        var category = MimeTypeCatalog.GetCategory(mimeType);

        if (!policy.Allows(category))
            return new AttachmentUploadOutcome.Rejected(
                $"Attachment category '{category}' is not allowed by the Personal attachment policy.");

        var parsedSessionId = new SessionId(sessionId);
        var stagingDir = SessionDirectoryHelper.GetOrCreateAttachmentStagingDirectory(
            parsedSessionId, _paths.SessionsDirectory);
        var stagingPath = Path.Combine(stagingDir, $"upload-{Guid.NewGuid():N}.tmp");

        long bytesWritten;
        try
        {
            bytesWritten = await CopyBoundedAsync(content, stagingPath, policy.MaxFileBytes, cancellationToken);
        }
        catch (AttachmentSizeExceededException)
        {
            TryDeleteLogged(stagingPath);
            return new AttachmentUploadOutcome.Rejected(
                $"'{fileName}' exceeds the {policy.MaxFileBytes} byte per-file limit.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDeleteLogged(stagingPath);
            _logger.LogWarning(ex, "Attachment upload failed to stage for session {SessionId}", sessionId);
            return new AttachmentUploadOutcome.Rejected("The upload could not be stored. Try again.");
        }

        if (bytesWritten == 0)
        {
            TryDeleteLogged(stagingPath);
            return new AttachmentUploadOutcome.Rejected($"'{fileName}' uploaded as zero bytes.");
        }

        string inboxPath;
        try
        {
            var inboxDir = SessionDirectoryHelper.GetOrCreateInboxDirectory(
                parsedSessionId, _paths.SessionsDirectory);
            inboxPath = InboxWriter.SanitizeReserveAndMove(inboxDir, fileName, stagingPath);
        }
        catch (Exception ex) when (ex is InboxWriter.CollisionExhaustedException or IOException or UnauthorizedAccessException)
        {
            TryDeleteLogged(stagingPath);
            _logger.LogWarning(ex, "Attachment upload failed to move into inbox for session {SessionId}", sessionId);
            return new AttachmentUploadOutcome.Rejected("The upload could not be stored. Try again.");
        }

        var attachment = new PendingSessionAttachment
        {
            Id = Guid.NewGuid().ToString("N"),
            FileName = Path.GetFileName(inboxPath),
            RelativePath = $"{SessionDirectoryHelper.InboxSubdirectory}/{Path.GetFileName(inboxPath)}",
            MimeType = mimeType.Value,
            Category = category.ToString(),
            SizeBytes = bytesWritten,
            StoredAtMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
        };

        // The pending reference persists in the session actor before the
        // upload reports success — a restart between upload and send loses
        // nothing. If the actor rejects the reference, remove the stored
        // file so no orphan remains.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var response = await _pipeline.SendFeedbackAndWaitAsync(
            new AddPendingAttachment { SessionId = parsedSessionId, Attachment = attachment },
            linked.Token);

        if (response is CommandNack nack)
        {
            TryDeleteLogged(inboxPath);
            return new AttachmentUploadOutcome.Rejected(nack.Reason);
        }

        _logger.LogInformation(
            "Attachment stored for session {SessionId}: {FileName} ({Bytes} bytes, {Mime})",
            sessionId, attachment.FileName, bytesWritten, mimeType.Value);

        return new AttachmentUploadOutcome.Accepted(new SessionAttachmentUploadResult(
            attachment.Id,
            attachment.FileName,
            attachment.RelativePath,
            attachment.MimeType,
            attachment.SizeBytes));
    }

    private sealed class AttachmentSizeExceededException : Exception;

    private static async Task<long> CopyBoundedAsync(
        Stream source,
        string targetPath,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        long total = 0;
        var buffer = new byte[81920];
        await using var target = new FileStream(
            targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, buffer.Length, useAsync: true);

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
                throw new AttachmentSizeExceededException();
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await target.FlushAsync(cancellationToken);
        return total;
    }

    private void TryDeleteLogged(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup — the staging root is also swept by the
            // periodic staging cleanup. Log so an orphan is traceable.
            _logger.LogDebug(ex, "Failed to delete rejected attachment file {Path}", path);
        }
    }
}
