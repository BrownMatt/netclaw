// -----------------------------------------------------------------------
// <copyright file="SessionTeardownService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Gateway;

/// <summary>
/// Permanently deletes a session across every store it touches: the actor,
/// the Akka persistence rows, the catalog row, the session directory, the
/// attachment staging directory, and the session log. Every path derives
/// from the daemon-side persistence identity — never from client text.
/// Steps continue through individual failures and the report never claims
/// success when any step failed.
/// </summary>
public sealed class SessionTeardownService
{
    public sealed record TeardownStep(string Name, bool Ok, string? Error);

    public sealed record TeardownReport(bool Deleted, IReadOnlyList<TeardownStep> Steps);

    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);

    private readonly SessionRegistry _registry;
    private readonly SessionCatalogService _catalog;
    private readonly IRequiredActor<SessionManagerActorKey> _sessionManagerProvider;
    private readonly IRequiredActor<SessionLogDispatcherActorKey> _logDispatcherProvider;
    private readonly ActorSystem _actorSystem;
    private readonly IHubContext<SessionHub, ISessionHubClient> _hubContext;
    private readonly NetclawPaths _paths;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SessionTeardownService> _logger;

    public SessionTeardownService(
        SessionRegistry registry,
        SessionCatalogService catalog,
        IRequiredActor<SessionManagerActorKey> sessionManagerProvider,
        IRequiredActor<SessionLogDispatcherActorKey> logDispatcherProvider,
        ActorSystem actorSystem,
        IHubContext<SessionHub, ISessionHubClient> hubContext,
        NetclawPaths paths,
        TimeProvider timeProvider,
        ILogger<SessionTeardownService> logger)
    {
        _registry = registry;
        _catalog = catalog;
        _sessionManagerProvider = sessionManagerProvider;
        _logDispatcherProvider = logDispatcherProvider;
        _actorSystem = actorSystem;
        _hubContext = hubContext;
        _paths = paths;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Runs the teardown. Returns null when the catalog does not know the
    /// session — the caller rejects with 404 and nothing changes.
    /// </summary>
    public async Task<TeardownReport?> DeleteSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_catalog.SessionExists(sessionId))
            return null;

        var parsed = new SessionId(sessionId);
        var steps = new List<TeardownStep>();

        try
        {
            // 1. Block revival, detach clients, notify them, shut the binding.
            var deletedDto = SessionOutputDtoMapper.ToDto(new SessionDeletedOutput
            {
                SessionId = parsed,
                TimestampMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds()
            });
            try
            {
                var detached = await _registry.BeginTeardownAsync(
                    sessionId,
                    conn => _hubContext.Clients.Client(conn.Value).ReceiveOutput(deletedDto));
                steps.Add(new TeardownStep("detach-clients", true, null));
                _logger.LogInformation(
                    "Session teardown detached {Count} client(s) from {SessionId}.",
                    detached.Count, sessionId);
            }
            catch (Exception ex)
            {
                steps.Add(new TeardownStep("detach-clients", false, ex.Message));
                LogStep(sessionId, "detach-clients", ex);
            }

            // 2. Stop the session actor, then its log actor (releases the
            //    session.log handle before the file delete below).
            steps.Add(await StopChildAsync(
                await _sessionManagerProvider.GetAsync(ct), "stop-session-actor", sessionId));
            steps.Add(await StopChildAsync(
                await _logDispatcherProvider.GetAsync(ct), "stop-session-log-actor", sessionId));

            // 3. Persistence rows — the actor is stopped, so no writer races.
            steps.Add(DeletePersistenceRows(sessionId));

            // 4. Catalog row.
            try
            {
                _catalog.DeleteCatalogRow(sessionId);
                steps.Add(new TeardownStep("delete-catalog-row", true, null));
                LogStep(sessionId, "delete-catalog-row", null);
            }
            catch (Exception ex)
            {
                steps.Add(new TeardownStep("delete-catalog-row", false, ex.Message));
                LogStep(sessionId, "delete-catalog-row", ex);
            }

            // 5. Files — all paths derive from the sanitized session id.
            steps.Add(await DeleteDirectoryAsync("delete-session-directory",
                SessionDirectoryHelper.GetSessionDirectory(parsed, _paths.SessionsDirectory)));
            steps.Add(await DeleteDirectoryAsync("delete-staging-directory",
                Path.Combine(
                    _paths.SessionsDirectory,
                    SessionDirectoryHelper.AttachmentStagingRootSubdirectory,
                    SessionDirectoryHelper.SanitizeSessionId(parsed))));
            steps.Add(await DeleteDirectoryAsync("delete-session-log",
                SessionLogFile.GetLogsDirectory(parsed, _paths.SessionLogsDirectory)));
        }
        finally
        {
            await _registry.EndTeardownAsync(sessionId);
        }

        var deleted = steps.All(s => s.Ok);
        if (deleted)
            _logger.LogInformation("Session {SessionId} deleted; all teardown steps completed.", sessionId);
        else
            _logger.LogError(
                "Session {SessionId} teardown incomplete: {Failed}",
                sessionId,
                string.Join(", ", steps.Where(s => !s.Ok).Select(s => $"{s.Name} ({s.Error})")));

        return new TeardownReport(deleted, steps);
    }

    private async Task<TeardownStep> StopChildAsync(IActorRef parent, string step, string sessionId)
    {
        var childName = Uri.EscapeDataString(sessionId);
        try
        {
            var child = await _actorSystem
                .ActorSelection(parent.Path / childName)
                .ResolveOne(ResolveTimeout);
            var stopped = await child.GracefulStop(StopTimeout);
            var result = new TeardownStep(step, stopped, stopped ? null : $"actor did not stop within {StopTimeout.TotalSeconds}s");
            LogStep(sessionId, step, stopped ? null : new TimeoutException(result.Error));
            return result;
        }
        catch (ActorNotFoundException)
        {
            // Not materialized — nothing to stop.
            LogStep(sessionId, step, null);
            return new TeardownStep(step, true, null);
        }
        catch (Exception ex)
        {
            LogStep(sessionId, step, ex);
            return new TeardownStep(step, false, ex.Message);
        }
    }

    private TeardownStep DeletePersistenceRows(string sessionId)
    {
        const string step = "delete-persistence-rows";
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _paths.SqliteDbPath,
                Mode = SqliteOpenMode.ReadWriteCreate
            }.ToString();

            using var conn = new SqliteConnection(connectionString);
            conn.Open();
            using var tx = conn.BeginTransaction();
            foreach (var table in new[] { "journal", "journal_metadata", "tags", "snapshot" })
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                // Table names come from the fixed list above, never input.
                cmd.CommandText = $"DELETE FROM {table} WHERE persistence_id = $pid";
                cmd.Parameters.AddWithValue("$pid", $"session-{sessionId}");
                cmd.ExecuteNonQuery();
            }

            tx.Commit();
            LogStep(sessionId, step, null);
            return new TeardownStep(step, true, null);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // "no such table" — a store that never existed has nothing to delete.
            LogStep(sessionId, step, null);
            return new TeardownStep(step, true, null);
        }
        catch (Exception ex)
        {
            LogStep(sessionId, step, ex);
            return new TeardownStep(step, false, ex.Message);
        }
    }

    private async Task<TeardownStep> DeleteDirectoryAsync(string step, string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                LogStep(path, step, null);
                return new TeardownStep(step, true, null);
            }
            catch (IOException ex) when (attempt == 0)
            {
                // Windows: a just-released handle can linger briefly.
                _logger.LogDebug(ex, "Teardown step {Step} hit a locked file; retrying once.", step);
                await Task.Delay(TimeSpan.FromMilliseconds(250), _timeProvider);
            }
            catch (Exception ex)
            {
                LogStep(path, step, ex);
                return new TeardownStep(step, false, ex.Message);
            }
        }
    }

    private void LogStep(string subject, string step, Exception? error)
    {
        if (error is null)
            _logger.LogInformation("Session teardown step {Step} completed for {Subject}.", step, subject);
        else
            _logger.LogWarning(error, "Session teardown step {Step} failed for {Subject}.", step, subject);
    }
}
