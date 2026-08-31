// -----------------------------------------------------------------------
// <copyright file="SessionManagementEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Operator-authenticated session management routes: rename, pin, and
/// archive. Session ids contain '/' (for example <c>signalr/{guid}</c>), so
/// every route takes the id as a query parameter — the same convention as
/// the attachment upload endpoint — never as a path segment.
/// </summary>
public static class SessionManagementEndpointRouteBuilderExtensions
{
    public sealed record RenameSessionRequest(string Title);

    public sealed record SessionFlagsRequest(bool? Pinned, bool? Archived);

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    public static IEndpointRouteBuilder MapSessionManagementEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/sessions/rename", async Task<Results<Ok, NotFound<string>, BadRequest<string>>> (
                string sessionId,
                RenameSessionRequest request,
                SessionCatalogService catalog,
                ISessionPipeline pipeline,
                CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(request.Title))
                    return TypedResults.BadRequest("A session title cannot be empty.");

                // The command pipeline creates a session actor on demand, so
                // an unknown id must be rejected here — otherwise a rename
                // would silently create an empty session.
                if (!catalog.SessionExists(sessionId))
                    return TypedResults.NotFound($"Session '{sessionId}' not found.");

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(CommandTimeout);
                var response = await pipeline.SendFeedbackAndWaitAsync(new RenameSession
                {
                    SessionId = new SessionId(sessionId),
                    Title = request.Title
                }, timeout.Token);

                return response is CommandNack nack
                    ? TypedResults.BadRequest(nack.Reason)
                    : TypedResults.Ok();
            })
            .WithName("RenameSession")
            .WithSummary("Set a manual session title. The title locks against the automatic title generator.")
            .WithTags("Sessions")
            .RequireAuthorization();

        app.MapPatch("/api/sessions/flags", Results<Ok, NotFound<string>, BadRequest<string>> (
                string sessionId,
                SessionFlagsRequest request,
                SessionCatalogService catalog) =>
            {
                if (request.Pinned is null && request.Archived is null)
                    return TypedResults.BadRequest("Provide pinned, archived, or both.");

                if (!catalog.SessionExists(sessionId))
                    return TypedResults.NotFound($"Session '{sessionId}' not found.");

                if (request.Pinned is { } pinned && !catalog.SetPinned(sessionId, pinned))
                    return TypedResults.NotFound($"Session '{sessionId}' not found.");

                if (request.Archived is { } archived && !catalog.SetArchived(sessionId, archived))
                    return TypedResults.NotFound($"Session '{sessionId}' not found.");

                return TypedResults.Ok();
            })
            .WithName("SetSessionFlags")
            .WithSummary("Set the pinned and archived catalog flags for a session.")
            .WithTags("Sessions")
            .RequireAuthorization();

        app.MapDelete("/api/sessions", async Task<Results<Ok<SessionTeardownService.TeardownReport>, NotFound<string>, JsonHttpResult<SessionTeardownService.TeardownReport>>> (
                string sessionId,
                [Microsoft.AspNetCore.Mvc.FromServices] SessionTeardownService teardown,
                CancellationToken ct) =>
            {
                var report = await teardown.DeleteSessionAsync(sessionId, ct);
                if (report is null)
                    return TypedResults.NotFound($"Session '{sessionId}' not found.");

                // A partial teardown must not read as success — the report
                // names the completed and failed steps either way.
                return report.Deleted
                    ? TypedResults.Ok(report)
                    : TypedResults.Json(report, statusCode: StatusCodes.Status500InternalServerError);
            })
            .WithName("DeleteSession")
            .WithSummary("Permanently delete a session across every store. Irreversible.")
            .WithTags("Sessions")
            .RequireAuthorization();

        return app;
    }
}
