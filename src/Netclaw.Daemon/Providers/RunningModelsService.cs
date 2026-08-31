// -----------------------------------------------------------------------
// <copyright file="RunningModelsService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Daemon.Providers;

/// <summary>One loaded model reported by one provider.</summary>
public sealed record RunningModelEntry(
    string Id,
    long? SizeBytes,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// One provider's slice of the running-models listing. Mirrors the catalog's
/// failure contract: a provider whose probe fails stays visible with
/// <see cref="Ok"/> false and a reason.
/// </summary>
public sealed record RunningModelsProvider(
    string ProviderKey,
    string Type,
    bool Ok,
    string? Error,
    IReadOnlyList<RunningModelEntry> Models);

/// <summary>Response served by <c>GET /api/models/running</c>.</summary>
public sealed record RunningModelsResponse(
    IReadOnlyList<RunningModelsProvider> Providers);

/// <summary>
/// Composes the running-models probe across configured providers. Only
/// descriptors that implement <see cref="IRunningModelsProbe"/> participate —
/// a provider type without the concept of loaded models contributes nothing
/// and is not failed. Uncached: running state changes with every model
/// load/unload, and the GUI polls at its own interval.
/// </summary>
public sealed class RunningModelsService
{
    private static readonly TimeSpan PerProviderTimeout = TimeSpan.FromSeconds(10);

    private readonly IReadOnlyDictionary<string, ProviderEntry> _providers;
    private readonly ProviderDescriptorRegistry _registry;
    private readonly ILogger<RunningModelsService> _logger;

    public RunningModelsService(
        IReadOnlyDictionary<string, ProviderEntry> providers,
        ProviderDescriptorRegistry registry,
        ILogger<RunningModelsService> logger)
    {
        _providers = providers;
        _registry = registry;
        _logger = logger;
    }

    public async Task<RunningModelsResponse> GetRunningModelsAsync(CancellationToken ct = default)
    {
        var probes = _providers
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => ProbeProviderAsync(p.Key, p.Value, ct))
            .ToList();

        var results = await Task.WhenAll(probes);
        return new RunningModelsResponse([.. results.Where(r => r is not null).Select(r => r!)]);
    }

    private async Task<RunningModelsProvider?> ProbeProviderAsync(
        string providerKey, ProviderEntry entry, CancellationToken ct)
    {
        if (!_registry.TryGet(entry.Type, out var descriptor)
            || descriptor is not IRunningModelsProbe probe)
        {
            return null; // No loaded-models concept for this provider type.
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(PerProviderTimeout);

            var result = await probe.ProbeRunningAsync(entry, timeoutCts.Token);
            if (!result.Success)
            {
                _logger.LogWarning(
                    "Running-models probe failed for provider {ProviderKey}: {Error}",
                    providerKey, result.ErrorMessage);
                return new RunningModelsProvider(
                    providerKey, entry.Type, Ok: false,
                    result.ErrorMessage ?? "probe failed", []);
            }

            var models = result.Models
                .Select(m => new RunningModelEntry(m.ModelId, m.SizeBytes, m.ExpiresAt))
                .ToList();
            return new RunningModelsProvider(providerKey, entry.Type, Ok: true, null, models);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Running-models probe timed out for provider {ProviderKey}", providerKey);
            return new RunningModelsProvider(
                providerKey, entry.Type, Ok: false, "probe timed out", []);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Running-models probe threw for provider {ProviderKey}", providerKey);
            return new RunningModelsProvider(
                providerKey, entry.Type, Ok: false, ex.Message, []);
        }
    }
}
