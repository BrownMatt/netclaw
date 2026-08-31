// -----------------------------------------------------------------------
// <copyright file="DiagnosticsDto.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Client;

/// <summary>Tail window of one log file, from the daemon log endpoints.</summary>
public sealed record LogTailResultDto(
    string FileName,
    IReadOnlyList<string> Lines);

/// <summary>Response of <c>GET /api/models/running</c>.</summary>
public sealed record RunningModelsResponseDto(
    IReadOnlyList<RunningModelsProviderDto> Providers);

/// <summary>
/// One provider's running-models slice. A provider without the loaded-models
/// concept never appears; a failed probe appears with <see cref="Ok"/> false.
/// </summary>
public sealed record RunningModelsProviderDto(
    string ProviderKey,
    string Type,
    bool Ok,
    string? Error,
    IReadOnlyList<RunningModelEntryDto> Models);

/// <summary>One loaded model.</summary>
public sealed record RunningModelEntryDto(
    string Id,
    long? SizeBytes,
    DateTimeOffset? ExpiresAt);
