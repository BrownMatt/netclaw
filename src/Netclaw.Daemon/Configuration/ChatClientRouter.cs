// -----------------------------------------------------------------------
// <copyright file="ChatClientRouter.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Selects which composed chat-client pipeline(s) to invoke for a given routing
/// context. Returns candidates in priority order; <see cref="RoutingChatClient"/>
/// walks them, so failover is "the candidate list has more than one entry" rather
/// than a bespoke decorator. The router is the seam where the future per-role /
/// per-agent / per-provider routing tracked in netclaw-dev/netclaw#648 slots in as
/// a different policy.
/// </summary>
public interface IChatClientRouter
{
    /// <summary>
    /// Returns the composed pipelines to try, in order, for this context. Never empty.
    /// </summary>
    IReadOnlyList<IChatClient> Route(ChatRoutingContext context);
}

/// <summary>
/// Today's policy: route by <see cref="ModelRole"/> with primary→fallback failover.
/// Builds the main/fallback/compaction pipelines once via
/// <see cref="PipelineChatClientFactory"/> and maps each role to an ordered candidate
/// list. Reproduces the previous resilient-decorator wiring:
/// <list type="bullet">
/// <item>Main / Fallback → <c>[main]</c>, plus <c>fallback</c> when a distinct
/// fallback model is configured (failover across both).</item>
/// <item>Compaction → its own single pipeline when a distinct compaction model is
/// configured; otherwise it reuses the main candidate list (inheriting failover).</item>
/// </list>
/// </summary>
public sealed class RoleBasedFailoverRouter : IChatClientRouter
{
    private readonly IReadOnlyList<IChatClient> _mainCandidates;
    private readonly IReadOnlyList<IChatClient> _compactionCandidates;

    public RoleBasedFailoverRouter(PipelineChatClientFactory factory, ModelSelection models)
        : this(factory.Create, models)
    {
    }

    // Test seam: build candidates from any create function, independent of the provider
    // plumbing PipelineChatClientFactory needs.
    internal RoleBasedFailoverRouter(Func<ModelReference, IChatClient> create, ModelSelection models)
    {
        var main = create(models.Main);
        _mainCandidates = models.Fallback is not null
            ? [main, create(models.Fallback)]
            : [main];

        // A distinct compaction model gets its own (single-candidate) pipeline; without
        // one, compaction reuses the main candidates so it inherits failover.
        _compactionCandidates = models.Compaction is not null
            ? [create(models.Compaction)]
            : _mainCandidates;
    }

    public IReadOnlyList<IChatClient> Route(ChatRoutingContext context) => context.Role switch
    {
        ModelRole.Compaction => _compactionCandidates,
        // Main and Fallback both resolve to the main candidate list (which already
        // contains the fallback when configured), matching the prior provider contract.
        _ => _mainCandidates
    };
}

/// <summary>
/// Router policy that honors <see cref="ChatRoutingContext.OverrideModel"/>
/// on Main-role contexts: a single lazily-built pipeline per overridden
/// model, memoized by (provider, model id). The configured fallback chain
/// deliberately does not apply to an override — a failed override call must
/// be loud, not silently served by a different model. Every other context
/// delegates to the wrapped role-based policy unchanged.
/// </summary>
public sealed class OverrideAwareRouter : IChatClientRouter
{
    private readonly IChatClientRouter _inner;
    private readonly Func<ModelReference, IChatClient> _create;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Provider, string ModelId), IChatClient> _overridePipelines = new();

    public OverrideAwareRouter(IChatClientRouter inner, PipelineChatClientFactory factory)
        : this(inner, factory.Create)
    {
    }

    // Test seam: build override pipelines from any create function.
    internal OverrideAwareRouter(IChatClientRouter inner, Func<ModelReference, IChatClient> create)
    {
        _inner = inner;
        _create = create;
    }

    public IReadOnlyList<IChatClient> Route(ChatRoutingContext context)
    {
        // Only Main honors the override; Compaction (and any future role)
        // keeps its configured routing even if a caller sets the field.
        if (context.OverrideModel is not { } overrideModel || context.Role == ModelRole.Compaction)
            return _inner.Route(context);

        var pipeline = _overridePipelines.GetOrAdd(
            (overrideModel.Provider, overrideModel.ModelId),
            static (_, state) => state.Create(state.Model),
            (Create: _create, Model: overrideModel));
        return [pipeline];
    }
}
