// -----------------------------------------------------------------------
// <copyright file="OllamaDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Provider descriptor for Ollama (local inference server).
/// </summary>
public sealed class OllamaDescriptor : IProviderDescriptor, IRunningModelsProbe
{
    public const string DefaultEndpointValue = "http://localhost:11434";

    private readonly HttpClient _httpClient;

    public OllamaDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "ollama";
    public string DisplayName => "Ollama";
    public string DefaultEndpoint => DefaultEndpointValue;
    public string ModelListingPath => "/api/tags";
    public IProviderAuth Auth { get; } = new EndpointOnlyAuth();

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        return ProbeHelpers.ExecuteProbeAsync(
            _httpClient,
            TypeKey,
            DefaultEndpoint,
            ModelListingPath,
            entry.Endpoint,
            configureRequest: _ => { }, // No auth headers needed
            parseResponse: ParseOllamaModels,
            ct,
            timeout: ProbeTimeouts.SelfHosted);
    }

    /// <summary>
    /// Lists the models Ollama currently holds loaded (<c>/api/ps</c>).
    /// Fields Ollama does not report map to absent, never to failure — the
    /// endpoint shape varies across Ollama versions.
    /// </summary>
    public async Task<RunningModelsProbeResult> ProbeRunningAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        var baseUrl = string.IsNullOrWhiteSpace(entry.Endpoint)
            ? DefaultEndpoint
            : entry.Endpoint.TrimEnd('/');
        var url = $"{baseUrl}/api/ps";

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ProbeTimeouts.SelfHosted);

        try
        {
            using var response = await _httpClient.GetAsync(url, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return new RunningModelsProbeResult(
                    false, $"{DisplayName} returned HTTP {(int)response.StatusCode}.", []);
            }

            var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return ParseRunningModels(json);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new RunningModelsProbeResult(false, $"No response from {baseUrl}.", []);
        }
        catch (HttpRequestException ex)
        {
            return new RunningModelsProbeResult(false, $"Connection failed: {ex.Message}", []);
        }
    }

    private static RunningModelsProbeResult ParseRunningModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var models = new List<RunningModel>();

        if (doc.RootElement.TryGetProperty("models", out var modelsArray)
            && modelsArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var model in modelsArray.EnumerateArray())
            {
                if (!model.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String)
                    continue;

                long? size = model.TryGetProperty("size", out var sizeProp)
                             && sizeProp.ValueKind == JsonValueKind.Number
                             && sizeProp.TryGetInt64(out var bytes)
                    ? bytes
                    : null;

                DateTimeOffset? expires = model.TryGetProperty("expires_at", out var expiresProp)
                                          && expiresProp.ValueKind == JsonValueKind.String
                                          && DateTimeOffset.TryParse(expiresProp.GetString(), out var parsed)
                    ? parsed
                    : null;

                models.Add(new RunningModel(name.GetString()!, size, expires));
            }
        }

        return new RunningModelsProbeResult(true, null, models);
    }

    private static ProviderProbeResult ParseOllamaModels(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var models = new List<DiscoveredModel>();

        if (doc.RootElement.TryGetProperty("models", out var modelsArray))
        {
            foreach (var model in modelsArray.EnumerateArray())
            {
                if (IsEmbeddingOnly(model))
                    continue;

                if (model.TryGetProperty("name", out var name))
                {
                    models.Add(new DiscoveredModel { ModelId = new(name.GetString()!) });
                }
            }
        }

        return new ProviderProbeResult(true, null, models);
    }

    private static bool IsEmbeddingOnly(JsonElement model)
    {
        if (!model.TryGetProperty("capabilities", out var capabilities)
            || capabilities.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var sawCapability = false;
        foreach (var capability in capabilities.EnumerateArray())
        {
            if (capability.ValueKind != JsonValueKind.String)
                continue;

            sawCapability = true;
            var value = capability.GetString();
            if (string.Equals(value, "completion", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "chat", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return sawCapability;
    }
}
