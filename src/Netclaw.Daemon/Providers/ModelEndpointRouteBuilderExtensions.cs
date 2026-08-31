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
        // [FromServices] everywhere: without it, a host that maps these routes
        // but lacks one registration turns the parameter into an inferred GET
        // body and fails ALL routes at startup (the Phase 3 lesson).
        app.MapGet("/api/models", async ValueTask<Ok<ModelCatalogResponse>> (
                [Microsoft.AspNetCore.Mvc.FromServices] ModelCatalogService modelCatalog, CancellationToken ct) =>
            TypedResults.Ok(await modelCatalog.GetCatalogAsync(ct)))
            .WithName("ListModels")
            .WithSummary("List the models selectable for a session, per configured provider, " +
                         "with tri-state tool-call support. A failed provider probe appears " +
                         "as a failed entry instead of an empty list.")
            .WithTags("Models")
            .RequireAuthorization();

        app.MapGet("/api/models/running", async Task<Ok<RunningModelsResponse>> (
                [Microsoft.AspNetCore.Mvc.FromServices] RunningModelsService runningModels, CancellationToken ct) =>
            TypedResults.Ok(await runningModels.GetRunningModelsAsync(ct)))
            .WithName("ListRunningModels")
            .WithSummary("List the models currently loaded by providers that report them " +
                         "(Ollama /api/ps). Providers without the concept contribute nothing; " +
                         "a failed probe appears as a failed entry.")
            .WithTags("Models")
            .RequireAuthorization();

        return app;
    }
}
