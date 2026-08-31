// -----------------------------------------------------------------------
// <copyright file="ModelCatalogService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Tri-state tool-call support for a catalog entry. String-valued on the
/// wire so clients cannot misread an absent report as "unsupported".
/// </summary>
public static class ModelToolSupport
{
    public const string Supported = "supported";
    public const string Unsupported = "unsupported";
    public const string Unknown = "unknown";

    public static string FromResolved(bool? supportsToolCalls) => supportsToolCalls switch
    {
        true => Supported,
        false => Unsupported,
        null => Unknown
    };
}

/// <summary>One selectable model from one configured provider.</summary>
public sealed record ModelCatalogEntry(
    string Id,
    string ToolSupport,
    int? ContextWindowTokens);

/// <summary>
/// One configured provider's slice of the catalog. A failed probe keeps the
/// provider visible with <see cref="Ok"/> false and a reason — a dead
/// provider must be diagnosable, never an empty merged list.
/// </summary>
public sealed record ModelCatalogProvider(
    string ProviderKey,
    string Type,
    bool Ok,
    string? Error,
    IReadOnlyList<ModelCatalogEntry> Models);

/// <summary>Catalog response served by <c>GET /api/models</c>.</summary>
public sealed record ModelCatalogResponse(
    IReadOnlyList<ModelCatalogProvider> Providers);

/// <summary>
/// Serves the models an operator can select for a session. Composes the
/// existing provider probe (model listing) with per-model capability
/// enrichment, and backs both the <c>GET /api/models</c> endpoint and the
/// set-time validation of a session model override. Results are cached
/// briefly so a dropdown open and a following set-validation do not
/// double-probe.
/// </summary>
public sealed class ModelCatalogService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PerProviderTimeout = TimeSpan.FromSeconds(10);
    private const int EnrichmentParallelism = 4;

    private readonly IReadOnlyDictionary<string, ProviderEntry> _providers;
    private readonly IProviderProbe _probe;
    private readonly Func<ProviderEntry, IModelCapabilityResolver?> _enricherFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ModelCatalogService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ModelCatalogResponse? _cached;
    private DateTimeOffset _cachedAt;

    public ModelCatalogService(
        IReadOnlyDictionary<string, ProviderEntry> providers,
        IProviderProbe probe,
        Func<ProviderEntry, IModelCapabilityResolver?> enricherFactory,
        TimeProvider timeProvider,
        ILogger<ModelCatalogService> logger)
    {
        _providers = providers;
        _probe = probe;
        _enricherFactory = enricherFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Returns the catalog, serving a cached copy inside the TTL window.
    /// </summary>
    public async Task<ModelCatalogResponse> GetCatalogAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_cached is not null && _timeProvider.GetUtcNow() - _cachedAt < CacheTtl)
                return _cached;

            var fresh = await BuildCatalogAsync(ct);
            _cached = fresh;
            _cachedAt = _timeProvider.GetUtcNow();
            return fresh;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Validates a session model selection before any routing changes.
    /// Returns null when the selection is valid; otherwise a rejection
    /// reason. Fails closed: a provider whose probe failed rejects the
    /// selection rather than trusting an unverifiable model id.
    /// </summary>
    public async Task<string?> ValidateSelectionAsync(
        string providerKey, string modelId, CancellationToken ct = default)
    {
        if (!_providers.ContainsKey(providerKey))
            return $"Provider '{providerKey}' is not configured.";

        var catalog = await GetCatalogAsync(ct);
        var provider = catalog.Providers.FirstOrDefault(p =>
            string.Equals(p.ProviderKey, providerKey, StringComparison.OrdinalIgnoreCase));

        if (provider is null || !provider.Ok)
        {
            var detail = provider?.Error ?? "provider probe unavailable";
            return $"Cannot verify model '{modelId}': {detail}";
        }

        return provider.Models.Any(m => string.Equals(m.Id, modelId, StringComparison.Ordinal))
            ? null
            : $"Model '{modelId}' is not available from provider '{providerKey}'.";
    }

    private async Task<ModelCatalogResponse> BuildCatalogAsync(CancellationToken ct)
    {
        var probes = _providers
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => ProbeProviderAsync(p.Key, p.Value, ct))
            .ToList();

        var providers = await Task.WhenAll(probes);
        return new ModelCatalogResponse(providers);
    }

    private async Task<ModelCatalogProvider> ProbeProviderAsync(
        string providerKey, ProviderEntry entry, CancellationToken ct)
    {
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PerProviderTimeout);

            var result = await _probe.ProbeAsync(entry, timeoutCts.Token);
            if (!result.Success)
            {
                _logger.LogWarning(
                    "Model catalog probe failed for provider {ProviderKey}: {Error}",
                    providerKey, result.ErrorMessage);
                return new ModelCatalogProvider(
                    providerKey, entry.Type, Ok: false,
                    result.ErrorMessage ?? "probe failed", []);
            }

            var entries = await EnrichAsync(entry, result.Models, timeoutCts.Token);
            return new ModelCatalogProvider(providerKey, entry.Type, Ok: true, null, entries);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Model catalog probe timed out for provider {ProviderKey}", providerKey);
            return new ModelCatalogProvider(
                providerKey, entry.Type, Ok: false, "probe timed out", []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Model catalog probe threw for provider {ProviderKey}", providerKey);
            return new ModelCatalogProvider(
                providerKey, entry.Type, Ok: false, ex.Message, []);
        }
    }

    private async Task<IReadOnlyList<ModelCatalogEntry>> EnrichAsync(
        ProviderEntry entry, IReadOnlyList<DiscoveredModel> models, CancellationToken ct)
    {
        var enricher = _enricherFactory(entry);
        if (enricher is null)
        {
            // No per-model capability source for this provider type: tool
            // support stays unknown — never "unsupported" (spec: absent
            // capability report maps to unknown).
            return [.. models.Select(m => new ModelCatalogEntry(
                m.ModelId.Value, ModelToolSupport.Unknown, m.ContextWindowTokens))];
        }

        using var throttle = new SemaphoreSlim(EnrichmentParallelism, EnrichmentParallelism);
        var tasks = models.Select(async m =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                var resolved = await enricher.ResolveAsync(m.ModelId.Value, ct);
                return new ModelCatalogEntry(
                    m.ModelId.Value,
                    ModelToolSupport.FromResolved(resolved?.SupportsToolCalls),
                    resolved?.ContextWindowTokens ?? m.ContextWindowTokens);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex,
                    "Capability enrichment failed for model {ModelId}", m.ModelId.Value);
                return new ModelCatalogEntry(
                    m.ModelId.Value, ModelToolSupport.Unknown, m.ContextWindowTokens);
            }
            finally
            {
                throttle.Release();
            }
        }).ToList();

        return await Task.WhenAll(tasks);
    }
}
