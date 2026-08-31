// -----------------------------------------------------------------------
// <copyright file="ModelCatalogDto.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Client;

/// <summary>
/// Client mirror of the daemon's <c>GET /api/models</c> response. One entry
/// per configured provider; a failed probe keeps the provider visible with
/// <see cref="Ok"/> false and a reason.
/// </summary>
public sealed record ModelCatalogResponseDto(
    IReadOnlyList<ModelCatalogProviderDto> Providers);

/// <summary>One configured provider's slice of the model catalog.</summary>
public sealed record ModelCatalogProviderDto(
    string ProviderKey,
    string Type,
    bool Ok,
    string? Error,
    IReadOnlyList<ModelCatalogEntryDto> Models);

/// <summary>
/// One selectable model. <see cref="ToolSupport"/> is tri-state:
/// "supported", "unsupported", or "unknown" — clients must not render
/// "unknown" as "unsupported".
/// </summary>
public sealed record ModelCatalogEntryDto(
    string Id,
    string ToolSupport,
    int? ContextWindowTokens);
