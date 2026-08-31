// -----------------------------------------------------------------------
// <copyright file="LogEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Operator-authenticated log tail routes. Log content can contain anything a
/// session did, so these carry the same authentication as the rest of the
/// API. Paths derive only from daemon-owned configuration and the sanitized
/// session id — client input never contributes a path segment. Session ids
/// contain '/', so the session route takes the id as a query parameter.
/// </summary>
public static class LogEndpointRouteBuilderExtensions
{
    public static IEndpointRouteBuilder MapLogEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/logs/daemon", async Task<Results<Ok<LogTailResult>, NotFound<string>>> (
                [Microsoft.AspNetCore.Mvc.FromServices] NetclawPaths paths,
                int? tail,
                CancellationToken ct) =>
            {
                var path = ResolveCurrentDaemonLogPath(paths);
                if (path is null)
                    return TypedResults.NotFound("No daemon log file found.");

                var result = await LogTailReader.ReadTailAsync(path, LogTailReader.ClampTail(tail), ct);
                return result is null
                    ? TypedResults.NotFound("No daemon log file found.")
                    : TypedResults.Ok(result);
            })
            .WithName("GetDaemonLogTail")
            .WithSummary("Tail the current daemon log (capped at 2000 lines).")
            .WithTags("Logs")
            .RequireAuthorization();

        app.MapGet("/api/logs/session", async Task<Results<Ok<LogTailResult>, NotFound<string>>> (
                string sessionId,
                [Microsoft.AspNetCore.Mvc.FromServices] NetclawPaths paths,
                int? tail,
                CancellationToken ct) =>
            {
                var path = SessionLogFile.GetLogPath(new SessionId(sessionId), paths.SessionLogsDirectory);
                var result = await LogTailReader.ReadTailAsync(path, LogTailReader.ClampTail(tail), ct);
                return result is null
                    ? TypedResults.NotFound($"Session '{sessionId}' has no session log.")
                    : TypedResults.Ok(result);
            })
            .WithName("GetSessionLogTail")
            .WithSummary("Tail a session's log by session id (capped at 2000 lines).")
            .WithTags("Logs")
            .RequireAuthorization();

        return app;
    }

    /// <summary>
    /// The daemon log rolls daily (daemon-YYYY-MM-DD.log), so the current file
    /// is the lexically newest match — the date format sorts correctly.
    /// Re-resolved per request so a midnight roll switches files on the next
    /// refresh.
    /// </summary>
    private static string? ResolveCurrentDaemonLogPath(NetclawPaths paths)
    {
        if (!Directory.Exists(paths.LogsDirectory))
            return null;

        var name = Path.GetFileNameWithoutExtension(paths.DaemonLogPath);
        var ext = Path.GetExtension(paths.DaemonLogPath);
        return Directory.EnumerateFiles(paths.LogsDirectory, $"{name}-*{ext}")
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();
    }
}
