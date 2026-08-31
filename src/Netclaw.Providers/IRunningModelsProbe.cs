// -----------------------------------------------------------------------
// <copyright file="IRunningModelsProbe.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;

namespace Netclaw.Providers;

/// <summary>One model a provider backend currently holds loaded.</summary>
public sealed record RunningModel(
    string ModelId,
    long? SizeBytes,
    DateTimeOffset? ExpiresAt);

/// <summary>Result of a running-models probe against one provider.</summary>
public sealed record RunningModelsProbeResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<RunningModel> Models);

/// <summary>
/// Optional descriptor capability: list the models the backend currently has
/// loaded (Ollama's <c>/api/ps</c>). A provider type without the concept of
/// loaded models simply does not implement this interface — callers check
/// with an <c>is</c> test, so absence is type-level, never a null dependency.
/// </summary>
public interface IRunningModelsProbe
{
    Task<RunningModelsProbeResult> ProbeRunningAsync(ProviderEntry entry, CancellationToken ct = default);
}
