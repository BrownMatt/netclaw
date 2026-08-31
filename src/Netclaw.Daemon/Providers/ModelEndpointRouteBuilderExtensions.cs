// -----------------------------------------------------------------------
// <copyright file="ModelEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace Netclaw.Daemon.Providers;

/// <summary>
/// Maps the model catalog endpoint. Operator-authenticated like the rest of
/// the daemon API.
/// </summary>
public static class ModelEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapModelEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/models", async ValueTask<Ok<ModelCatalogResponse>> (
                ModelCatalogService modelCatalog, CancellationToken ct) =>
            TypedResults.Ok(await modelCatalog.GetCatalogAsync(ct)))
            .WithName("ListModels")
            .WithSummary("List the models selectable for a session, per configured provider, " +
                         "with tri-state tool-call support. A failed provider probe appears " +
                         "as a failed entry instead of an empty list.")
            .WithTags("Models")
            .RequireAuthorization();

        return app;
    }
}
