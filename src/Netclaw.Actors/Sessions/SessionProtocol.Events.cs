// -----------------------------------------------------------------------
// <copyright file="SessionProtocol.Events.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Jobs;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Reminders;
using Netclaw.Actors.Serialization;
using Netclaw.Configuration;
using Netclaw.Security;
using Netclaw.Tools;

namespace Netclaw.Actors.Sessions;

public static partial class SessionProtocol
{
    // ===== Events (persisted to the session journal) =====

    /// <summary>
    /// Persisted event recording a completed turn (user message + assistant reply).
    /// </summary>
    public sealed record TurnRecorded : ISessionEvent
    {
        public SessionId SessionId { get; init; }

        public SerializableChatMessage UserMessage { get; init; } = new();

        public SerializableChatMessage AssistantReply { get; init; } = new();

        public long RecordedAtMs { get; init; }

        /// <summary>
        /// Populated when this turn originated from a reminder firing.
        /// Format is <c>"{reminderId}:{fireTimestampMs}"</c>, matching the value
        /// placed on <see cref="Channels.MessageSource.ReminderId"/> by the
        /// reminder dispatcher. Null for regular user turns. Used for forensics
        /// and to rebuild the in-memory reminder dedup ledger
        /// (<see cref="SessionState.ProcessedReminderIds"/>) from
        /// event replay.
        /// </summary>
        public ReminderId? SourceReminderId { get; init; }

        /// <summary>
        /// Populated when this turn originated from a background job result delivery.
        /// Format is <c>"bg-job:{jobId}"</c>, matching the value placed on
        /// <see cref="Channels.MessageSource.BackgroundJobId"/> by the
        /// background job manager. Null for regular user turns.
        /// </summary>
        public BackgroundJobId? SourceBackgroundJobId { get; init; }

        public DateTimeOffset RecordedAt => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);

        public DateTimeOffset Timestamp => RecordedAt;
    }

    /// <summary>
    /// Persisted event recording an assistant tool-call batch before any tool is
    /// executed or approval prompt is emitted. This makes in-flight tool history
    /// replayable from the journal instead of relying on snapshots to carry
    /// unjournaled current-turn state.
    /// </summary>
    public sealed record ToolBatchStarted : ISessionEvent
    {
        public SessionId SessionId { get; init; }

        public SerializableChatMessage UserMessage { get; init; } = new();

        public SerializableChatMessage AssistantMessage { get; init; } = new();

        public long StartedAtMs { get; init; }

        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(StartedAtMs);
    }

    /// <summary>
    /// Persisted event recording a single tool result as soon as it completes.
    /// This lets recovery avoid re-running completed sibling calls when another
    /// call in the same assistant batch is still pending approval.
    /// </summary>
    public sealed record ToolCallRecorded : ISessionEvent
    {
        public SessionId SessionId { get; init; }

        public SerializableChatMessage ToolResult { get; init; } = new();

        public long RecordedAtMs { get; init; }

        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);
    }

    public sealed record TurnContextRecord
    {
        public SessionId SessionId { get; init; }

        public string TurnId { get; init; } = string.Empty;

        public TrustAudience Audience { get; init; }

        public TrustBoundary? Boundary { get; init; }

        public string? ChannelType { get; init; }

        public SenderId? RequesterSenderId { get; init; }

        public PrincipalClassification? RequesterPrincipal { get; init; }

        public TransportAuthenticity TransportAuthenticity { get; init; }

        public PayloadTaint PayloadTaint { get; init; }

        public string? SourceScope { get; init; }

        public string? SourceKind { get; init; }

        public ChannelDeliveryTargetInfo? DefaultDeliveryTarget { get; init; }

        public ChannelDeliveryTargetInfo? RequestedDeliveryTarget { get; init; }

        public bool HasAdoptedContext { get; init; }

        public bool HasThirdPartyAdoptedContext { get; init; }

        public IReadOnlyList<string> AdoptedSpeakerIds { get; init; } = Array.Empty<string>();

        public bool SupportsInteractiveApproval { get; init; }
    }

    public sealed record ToolApprovalRequested : ISessionEvent
    {
        public SessionId SessionId { get; init; }

        public string CallId { get; init; } = string.Empty;

        public string? AuthorizationAttemptId { get; init; }

        public string ToolName { get; init; } = string.Empty;

        public IReadOnlyList<string> Patterns { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> CandidateVerbs { get; init; } = Array.Empty<string>();

        public TrustAudience Audience { get; init; }

        public TrustBoundary? Boundary { get; init; }

        public string? ChannelType { get; init; }

        public bool? SupportsInteractiveApproval { get; init; }

        public SenderId? RequesterSenderId { get; init; }

        public PrincipalClassification? RequesterPrincipal { get; init; }

        public bool HasThirdPartyAdoptedContext { get; init; }

        public IReadOnlyList<string> AdoptedSpeakerIds { get; init; } = Array.Empty<string>();

        public string? Cwd { get; init; }

        public IReadOnlyList<string> OptionKeys { get; init; } = Array.Empty<string>();

        public IReadOnlyList<ApprovalCandidate> Candidates { get; init; } =
            Array.Empty<ApprovalCandidate>();

        public string? SessionScratchDirectory { get; init; }

        public TurnContextRecord? TurnContext { get; init; }

        public long RequestedAtMs { get; init; }

        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(RequestedAtMs);
    }

    public sealed record ToolApprovalResolved : ISessionEvent
    {
        public SessionId SessionId { get; init; }

        public string CallId { get; init; } = string.Empty;

        public string? AuthorizationAttemptId { get; init; }

        public string Decision { get; init; } = string.Empty;

        public long ResolvedAtMs { get; init; }

        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(ResolvedAtMs);
    }

    public sealed record ToolBatchAbandoned : ISessionEvent
    {
        public SessionId SessionId { get; init; }

        public IReadOnlyList<SerializableChatMessage> ToolResults { get; init; } =
            Array.Empty<SerializableChatMessage>();

        public long AbandonedAtMs { get; init; }

        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(AbandonedAtMs);
    }

    /// <summary>
    /// Persisted event recording that the session's active background jobs were
    /// killed (reaped) at passivation. Reap marks are normally captured by the
    /// passivation snapshot, but that snapshot is skipped while an approval batch
    /// is parked (<c>SaveSnapshotIfSafe</c>); this event persists the marks
    /// independently so recovery cannot rehydrate the killed jobs as "running".
    /// Idempotent on replay — applies <c>MarkAllBackgroundJobsReaped</c>.
    /// </summary>
    public sealed record SessionBackgroundJobsReaped : ISessionEvent
    {
        public SessionId SessionId { get; init; }

        public long ReapedAtMs { get; init; }

        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(ReapedAtMs);
    }

    public sealed record AdoptedContextRecorded : ISessionEvent
    {
        public sealed record AdoptedMessageRecord
        {
            public string MessageId { get; init; } = string.Empty;

            public SenderId SenderId { get; init; } = new(string.Empty);

            public long TimestampMs { get; init; }

            public string AuthorityAtInclusion { get; init; } = string.Empty;
        }

        public SessionId SessionId { get; init; }

        public string AuthorizedMessageId { get; init; } = string.Empty;

        public SenderId? AuthorizerSenderId { get; init; }

        public string? LowerBound { get; init; }

        public string? UpperBound { get; init; }

        public string Projection { get; init; } = string.Empty;

        public bool HasAdoptedContext { get; init; }

        public bool HasThirdPartyAdoptedContext { get; init; }

        public IReadOnlyList<string> AdoptedSpeakerIds { get; init; } = Array.Empty<string>();

        public IReadOnlyList<AdoptedMessageRecord> Messages { get; init; } = Array.Empty<AdoptedMessageRecord>();

        public bool ProjectionPersisted { get; init; }

        public long RecordedAtMs { get; init; }

        public DateTimeOffset RecordedAt => DateTimeOffset.FromUnixTimeMilliseconds(RecordedAtMs);

        public DateTimeOffset Timestamp => RecordedAt;
    }

    /// <summary>
    /// Persisted event recording that the session title was set or updated.
    /// </summary>
    public sealed record SessionTitleSet : ISessionEvent
    {
        public SessionId SessionId { get; init; }

        public string Title { get; init; } = string.Empty;

        public long SetAtMs { get; init; }

        public DateTimeOffset SetAt => DateTimeOffset.FromUnixTimeMilliseconds(SetAtMs);

        public DateTimeOffset Timestamp => SetAt;
    }

    /// <summary>
    /// Durable reference to an uploaded file that waits for the session's
    /// next user message. The file bytes live in the session inbox
    /// (<see cref="Protocol.SessionDirectoryHelper.InboxSubdirectory"/>);
    /// this record carries only metadata.
    /// </summary>
    public sealed record PendingSessionAttachment
    {
        /// <summary>Opaque unique id assigned at upload time.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>Original client file name, for display and errors.</summary>
        public string FileName { get; init; } = string.Empty;

        /// <summary>Session-directory-relative path (e.g. <c>inbox/report.txt</c>).</summary>
        public string RelativePath { get; init; } = string.Empty;

        /// <summary>Verified MIME type from the upload classification.</summary>
        public string MimeType { get; init; } = string.Empty;

        /// <summary>Media-catalog attachment category name.</summary>
        public string Category { get; init; } = string.Empty;

        public long SizeBytes { get; init; }

        public long StoredAtMs { get; init; }
    }

    /// <summary>
    /// Persisted event recording that an uploaded attachment is pending for
    /// the session's next user message. Durable so a daemon restart between
    /// upload and send loses nothing.
    /// </summary>
    public sealed record SessionAttachmentStored : ISessionEvent
    {
        public SessionId SessionId { get; init; }

        public PendingSessionAttachment Attachment { get; init; } = new();

        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(Attachment.StoredAtMs);
    }

    /// <summary>
    /// Persisted event recording that pending attachments were embedded in a
    /// user message. Applying it clears the consumed entries, so later turns
    /// never re-embed the content.
    /// </summary>
    public sealed record SessionAttachmentsConsumed : ISessionEvent
    {
        public SessionId SessionId { get; init; }

        public IReadOnlyList<string> AttachmentIds { get; init; } = Array.Empty<string>();

        public long ConsumedAtMs { get; init; }

        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(ConsumedAtMs);
    }

    /// <summary>
    /// Persisted event recording an operator folder grant. Applied to
    /// <see cref="WorkingContext.GrantedFolders"/>; the grant survives actor
    /// recovery and daemon restart and ends with the session.
    /// </summary>
    public sealed record SessionFolderGrantAdded : ISessionEvent
    {
        public SessionId SessionId { get; init; }

        /// <summary>Normalized absolute path of the granted directory.</summary>
        public string Path { get; init; } = string.Empty;

        public long GrantedAtMs { get; init; }

        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(GrantedAtMs);
    }

    /// <summary>
    /// Persisted event recording a folder grant removal. Revocation is
    /// effective as soon as the event applies — no cache sits between the
    /// grant list and the file access policy.
    /// </summary>
    public sealed record SessionFolderGrantRemoved : ISessionEvent
    {
        public SessionId SessionId { get; init; }

        public string Path { get; init; } = string.Empty;

        public long RemovedAtMs { get; init; }

        public DateTimeOffset Timestamp => DateTimeOffset.FromUnixTimeMilliseconds(RemovedAtMs);
    }

    /// <summary>
    /// Persisted event recording that a session's conversation history was compacted.
    /// A snapshot is also taken after this event to avoid replaying the full journal.
    /// </summary>
    public sealed record SessionCompacted : ISessionEvent
    {
        public SessionId SessionId { get; init; }

        public string Summary { get; init; } = string.Empty;

        public IReadOnlyList<SerializableChatMessage> CompactedMessages { get; init; } =
            Array.Empty<SerializableChatMessage>();

        public int TurnCountBefore { get; init; }

        public long CompactedAtMs { get; init; }

        /// <summary>
        /// Updated working-context state carried on the event so
        /// <see cref="SessionState.Apply(SessionCompacted)"/> can
        /// preserve it across compaction. Null means "no update — retain
        /// the existing <see cref="WorkingContext"/> unchanged."
        /// </summary>
        public WorkingContext? WorkingContext { get; init; }

        public DateTimeOffset CompactedAt => DateTimeOffset.FromUnixTimeMilliseconds(CompactedAtMs);

        public DateTimeOffset Timestamp => CompactedAt;
    }
}
