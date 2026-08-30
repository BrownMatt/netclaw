// -----------------------------------------------------------------------
// <copyright file="AttachmentLineFormat.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;

namespace Netclaw.Actors.Protocol;

/// <summary>
/// Canonical <c>[attachment]</c> line format for structural attachment
/// references in user messages. One implementation shared by the channel
/// ingress pipeline (Slack/Discord/Mattermost) and the session actor's
/// pending-attachment embed, so the structural form cannot drift between
/// upload paths.
/// </summary>
public static class AttachmentLineFormat
{
    public static string BuildAttachmentLine(
        string name,
        string mimeType,
        long size,
        string relativePath,
        bool inlined,
        string? note)
    {
        var inlinedWire = inlined ? "true" : "false";
        var sb = new StringBuilder(128);
        sb.Append("[attachment] name=\"").Append(EscapeQuoted(name)).Append('"');
        sb.Append(" mime=\"").Append(EscapeQuoted(mimeType)).Append('"');
        sb.Append(" size=").Append(size);
        sb.Append(" path=\"").Append(EscapeQuoted(relativePath)).Append('"');
        sb.Append(" inlined=\"").Append(inlinedWire).Append('"');
        if (!string.IsNullOrEmpty(note))
            sb.Append(" note=\"").Append(EscapeQuoted(note)).Append('"');
        return sb.ToString();
    }

    public static string EscapeQuoted(string value)
    {
        var needsProcessing = false;
        foreach (var c in value)
        {
            if (c < ' ' || c == '\\' || c == '"')
            {
                needsProcessing = true;
                break;
            }
        }

        if (!needsProcessing)
            return value;

        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c < ' ')
                sb.Append(' ');
            else if (c == '\\')
                sb.Append("\\\\");
            else if (c == '"')
                sb.Append("\\\"");
            else
                sb.Append(c);
        }

        return sb.ToString();
    }
}
