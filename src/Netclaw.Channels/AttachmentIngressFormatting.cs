// -----------------------------------------------------------------------
// <copyright file="AttachmentIngressFormatting.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Media;
using Netclaw.Security;
using System.Text;

namespace Netclaw.Channels;

public static class AttachmentIngressFormatting
{
    // The [attachment] line format lives in AttachmentLineFormat
    // (Netclaw.Actors.Protocol) so the session actor's pending-attachment
    // embed and channel ingress share one implementation. These forwarders
    // keep the existing channel-facing surface.
    public static string BuildAttachmentLine(
        string name,
        string mimeType,
        long size,
        string relativePath,
        bool inlined,
        string? note)
        => AttachmentLineFormat.BuildAttachmentLine(name, mimeType, size, relativePath, inlined, note);

    public static string EscapeQuoted(string value)
        => AttachmentLineFormat.EscapeQuoted(value);

    public static (bool Inlined, string? Note) ResolveInlineDecision(
        MimeType mimeType,
        AttachmentCategory category,
        bool inlineImages)
        => AttachmentInlineDecision.Resolve(mimeType, category, inlineImages);

    public static async Task<AttachmentIngressProjection> BuildAcceptedProjectionAsync(
        string inboxPath,
        string filename,
        string mimeType,
        AttachmentCategory category,
        bool inlineImages,
        long size,
        CancellationToken cancellationToken)
    {
        var relativePath = $"{SessionDirectoryHelper.InboxSubdirectory}/{Path.GetFileName(inboxPath)}";
        var (inlined, note) = ResolveInlineDecision(new MimeType(mimeType), category, inlineImages);
        var line = BuildAttachmentLine(filename, mimeType, size, relativePath, inlined, note);

        if (!inlined)
            return new AttachmentIngressProjection(line, InlineContent: null, Inlined: false);

        var bytes = await File.ReadAllBytesAsync(inboxPath, cancellationToken);
        return new AttachmentIngressProjection(line, new DataContent(bytes, mimeType), Inlined: true);
    }

    public static async Task<IReadOnlyList<AIContent>> BuildAcceptedContentsAsync(
        string inboxPath,
        string filename,
        string mimeType,
        AttachmentCategory category,
        bool inlineImages,
        long size,
        CancellationToken cancellationToken)
    {
        var projection = await BuildAcceptedProjectionAsync(
            inboxPath, filename, mimeType, category, inlineImages, size, cancellationToken);
        var line = new TextContent(projection.Line);
        return projection.InlineContent is null
            ? [line]
            : [line, projection.InlineContent];
    }

    public static string FormatBytes(long size)
        => ByteSizeFormatter.Format(size);
}

public readonly record struct AttachmentIngressProjection(
    string Line,
    DataContent? InlineContent,
    bool Inlined);
