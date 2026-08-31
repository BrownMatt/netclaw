// -----------------------------------------------------------------------
// <copyright file="ChatRoutingContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Configuration;

/// <summary>
/// The inputs a chat-client router uses to select which composed pipeline to invoke
/// for a call. Minimal today — only <see cref="Role"/> is consulted — but shaped so
/// the per-role / per-agent / per-provider routing tracked in netclaw-dev/netclaw#648
/// slots in later as a new router policy without changing this type's shape or any caller.
/// </summary>
public sealed record ChatRoutingContext
{
    /// <summary>The model role being requested (today's only routing signal).</summary>
    public required ModelRole Role { get; init; }

    /// <summary>
    /// Owning session id. Populated by session actors so per-session routing
    /// decisions and diagnostics can correlate to the session.
    /// </summary>
    public string? SessionId { get; init; }

    /// <summary>
    /// Per-session model override. When set on a Main-role context, the
    /// router routes to this model as a single candidate — the configured
    /// fallback chain does not apply, so an override failure is loud.
    /// Compaction contexts never carry an override.
    /// </summary>
    public ModelReference? OverrideModel { get; init; }
}
