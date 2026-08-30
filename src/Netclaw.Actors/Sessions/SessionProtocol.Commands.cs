// -----------------------------------------------------------------------
// <copyright file="SessionProtocol.Commands.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Serialization;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

public static partial class SessionProtocol
{
    // ===== Commands =====

    /// <summary>
    /// Command delivering user input to a session actor. The proto mapping in
    /// <see cref="NetclawProtoMapper"/> intentionally drops <see cref="Source"/>
    /// (it's ephemeral channel metadata, not part of the persisted/wire form),
    /// so the local-dispatch path skips serialization via
    /// <see cref="INoSerializationVerificationNeeded"/> to preserve <c>Source</c>
    /// for in-process consumers (e.g. session-actor reminder dedup).
    /// </summary>
    public sealed record SendUserMessage : ISessionCommand, INetclawSerializableMessage, INoSerializationVerificationNeeded
    {
        public SessionId SessionId { get; init; }

        public string Content { get; init; } = string.Empty;

        /// <summary>
        /// Media references (images, audio, etc.) attached to this message.
        /// </summary>
        public IReadOnlyList<SerializableMediaReference> MediaReferences { get; init; } =
            Array.Empty<SerializableMediaReference>();

        /// <summary>
        /// Ephemeral channel metadata for ACL/audit. Not persisted.
        /// </summary>
        public MessageSource? Source { get; init; }
    }

    /// <summary>
    /// Channel-agnostic trusted-turn delivery for Mode B reminder re-entry.
    /// Issued by <c>ReminderExecutionActor</c> to the originating channel's
    /// gateway actor. Gateways route this message down their existing
    /// inbound-routing hierarchy via <c>Forward</c> (preserving the
    /// dispatcher's <c>Ask&lt;CommandAck&gt;</c> temp actor as <c>Sender</c>)
    /// until the leaf binding/session actor offers a <see cref="Channels.ChannelInput"/>
    /// to the pipeline with <see cref="Channels.MessageSource.AckTarget"/>
    /// populated from that <c>Sender</c>. No channel-level inbound ACL check
    /// is performed — the reminder's audience is validated at minting time
    /// by <c>reminder-audience-authorization</c>.
    /// </summary>
    public sealed record DeliverTrustedSessionTurn(
        SessionId SessionId,
        string Content,
        Channels.MessageSource Source) : ISessionCommand, INoSerializationVerificationNeeded;

    /// <summary>
    /// User's response to a <see cref="ToolInteractionRequest"/>.
    /// Routed from the channel adapter to the session actor to complete the
    /// blocked tool's <see cref="System.Threading.Tasks.TaskCompletionSource{T}"/>.
    /// </summary>
    public sealed record ToolInteractionResponse : ISessionCommand, INoSerializationVerificationNeeded
    {
        public required SessionId SessionId { get; init; }

        /// <summary>The tool call ID from the original <see cref="ToolInteractionRequest"/>.</summary>
        public required ToolCallId CallId { get; init; }

        /// <summary>The selected option key. See <see cref="ApprovalOptionKeys"/>.</summary>
        public required ApprovalOptionKey SelectedKey { get; init; }

        /// <summary>
        /// Identity of the user who selected the option. Used to bind approvals to
        /// the same principal that initiated the tool request.
        /// </summary>
        public required SenderId SenderId { get; init; }
    }

    /// <summary>
    /// Operator command that grants the session read, modify, and create
    /// access to one folder tree for first-party file tools. The session
    /// actor validates the path before persistence and replies with
    /// <see cref="CommandAck"/> only after the grant event is persisted, or
    /// <see cref="CommandNack"/> when validation rejects the path. A grant
    /// never joins the shell approval safe-space set.
    /// </summary>
    public sealed record AddFolderGrant : ISessionCommand, INoSerializationVerificationNeeded
    {
        public required SessionId SessionId { get; init; }

        /// <summary>Absolute path of an existing directory to grant.</summary>
        public required string Path { get; init; }
    }

    /// <summary>
    /// Daemon command that records an uploaded attachment as pending for the
    /// session's next user message. The upload endpoint sends this after it
    /// stored the file in the session inbox; the reply
    /// (<see cref="CommandAck"/>) follows the persisted event.
    /// </summary>
    public sealed record AddPendingAttachment : ISessionCommand, INoSerializationVerificationNeeded
    {
        public required SessionId SessionId { get; init; }

        public required PendingSessionAttachment Attachment { get; init; }
    }

    /// <summary>
    /// Operator command that removes a folder grant. Revocation is
    /// immediate: the ack follows the persisted removal event, and the next
    /// policy evaluation reads the updated grant list. Replies
    /// <see cref="CommandNack"/> when the path is not currently granted.
    /// </summary>
    public sealed record RemoveFolderGrant : ISessionCommand, INoSerializationVerificationNeeded
    {
        public required SessionId SessionId { get; init; }

        public required string Path { get; init; }
    }

    /// <summary>
    /// Text-only approval reply for a pending <see cref="ToolInteractionRequest"/>
    /// when the channel binding does not have the original prompt state locally.
    /// The session resolves the applicable pending interaction from its own
    /// journal-backed state and parses the text against that prompt's option order.
    /// </summary>
    public sealed record ToolInteractionTextResponse : ISessionCommand, INoSerializationVerificationNeeded
    {
        public required SessionId SessionId { get; init; }

        /// <summary>The raw user reply, e.g. <c>A</c> or <c>approve everywhere</c>.</summary>
        public required string Text { get; init; }

        /// <summary>
        /// Identity of the user who sent the text reply. Used to resolve which
        /// pending prompt they are allowed to answer.
        /// </summary>
        public required SenderId SenderId { get; init; }
    }
}
