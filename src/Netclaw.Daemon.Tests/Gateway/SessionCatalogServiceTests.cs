// -----------------------------------------------------------------------
// <copyright file="SessionCatalogServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Actors.Telemetry;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Gateway;

public sealed class SessionCatalogServiceTests : IDisposable
{
    private readonly string _tempBase = Path.Combine(Path.GetTempPath(), $"netclaw-catalog-test-{Guid.NewGuid():N}");

    private NetclawPaths CreatePaths()
    {
        var paths = new NetclawPaths(_tempBase);
        paths.EnsureDirectoriesExist();
        return paths;
    }

    private SessionCatalogService CreateService(NetclawPaths paths, ISessionMetrics? metrics = null, TimeProvider? timeProvider = null)
        => new(paths, timeProvider ?? TimeProvider.System, NullLogger<SessionCatalogService>.Instance, metrics);

    public void Dispose()
    {
        // Drain the SQLite connection pool before deleting the temp directory.
        // On Windows, pooled connections keep file handles open, causing Directory.Delete to fail.
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(_tempBase))
            Directory.Delete(_tempBase, recursive: true);
    }

    [Fact]
    public void ListRecent_AutoCreatesTable_WhenTableIsMissing()
    {
        var paths = CreatePaths();
        var service = CreateService(paths);

        // Database file exists (created by SqliteOpenMode.ReadWriteCreate) but sessions table does not.
        var entries = service.ListRecent();

        Assert.Empty(entries);

        // Table should now exist — verify by querying it directly.
        using var conn = OpenConn(paths);
        Assert.True(TableExists(conn, "sessions"));
        Assert.True(ColumnExists(conn, "sessions", "persistence_id"));
    }

    [Fact]
    public void OnSessionActivated_AutoCreatesTable_WhenTableIsMissing()
    {
        var paths = CreatePaths();
        var service = CreateService(paths);

        service.OnSessionActivated(new Netclaw.Actors.Protocol.SessionId("signalr/test-123"), ChannelType.SignalR);

        using var conn = OpenConn(paths);
        Assert.True(TableExists(conn, "sessions"));

        var rows = ReadAllSessions(conn);
        Assert.Single(rows);
        Assert.Equal("session-signalr/test-123", rows[0].persistenceId);
        Assert.Equal("signalr", rows[0].channel);
    }

    [Fact]
    public void ListRecent_MigratesLegacySchema_AndReturnsRows()
    {
        var paths = CreatePaths();

        // Seed legacy schema
        using (var conn = OpenConn(paths))
        {
            RunSql(conn,
                """
                CREATE TABLE sessions (
                    session_id    TEXT PRIMARY KEY,
                    last_activity INTEGER,
                    message_count INTEGER,
                    created       INTEGER,
                    display_name  TEXT
                )
                """);
            RunSql(conn,
                """
                INSERT INTO sessions (session_id, last_activity, message_count, created, display_name)
                VALUES ('signalr/abc-123', 1000, 5, 500, 'My Session')
                """);
        }

        var service = CreateService(paths);
        var entries = service.ListRecent();

        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal("session-signalr/abc-123", e.PersistenceId);
        Assert.Equal("signalr", e.Channel);
        Assert.Equal("My Session", e.Title);
        Assert.Equal(5, e.TurnCount);
        Assert.Equal(1000, e.LastActivity);
        Assert.Equal(500, e.CreatedAt);
        Assert.Equal("active", e.Status);

        // Verify the table now has the current schema
        using var verifyConn = OpenConn(paths);
        Assert.True(ColumnExists(verifyConn, "sessions", "persistence_id"));
        Assert.False(ColumnExists(verifyConn, "sessions", "session_id"));
    }

    [Fact]
    public void ListRecent_IsNoOp_WhenSchemaIsCurrent()
    {
        var paths = CreatePaths();

        // Seed current schema with a row
        using (var conn = OpenConn(paths))
        {
            RunSql(conn,
                """
                CREATE TABLE sessions (
                    persistence_id    TEXT NOT NULL PRIMARY KEY,
                    channel           TEXT NOT NULL,
                    created_at        INTEGER NOT NULL,
                    last_activity     INTEGER NOT NULL,
                    status            TEXT NOT NULL DEFAULT 'active',
                    turn_count        INTEGER NOT NULL DEFAULT 0,
                    title             TEXT,
                    description       TEXT,
                    last_input_tokens INTEGER,
                    log_path          TEXT,
                    metadata          TEXT
                )
                """);
            RunSql(conn,
                """
                INSERT INTO sessions (persistence_id, channel, created_at, last_activity, status, turn_count)
                VALUES ('session-signalr/xyz', 'signalr', 100, 200, 'active', 3)
                """);
        }

        var service = CreateService(paths);
        var entries = service.ListRecent();

        Assert.Single(entries);
        Assert.Equal("session-signalr/xyz", entries[0].PersistenceId);
        Assert.Equal(3, entries[0].TurnCount);
    }

    [Fact]
    public void ListRecent_AppliesLimitAndOffset()
    {
        var paths = CreatePaths();

        using (var conn = OpenConn(paths))
        {
            RunSql(conn,
                """
                CREATE TABLE sessions (
                    persistence_id    TEXT NOT NULL PRIMARY KEY,
                    channel           TEXT NOT NULL,
                    created_at        INTEGER NOT NULL,
                    last_activity     INTEGER NOT NULL,
                    status            TEXT NOT NULL DEFAULT 'active',
                    turn_count        INTEGER NOT NULL DEFAULT 0,
                    title             TEXT,
                    description       TEXT,
                    last_input_tokens INTEGER,
                    log_path          TEXT,
                    metadata          TEXT
                )
                """);

            for (var i = 1; i <= 5; i++)
            {
                RunSql(conn,
                    $"""
                    INSERT INTO sessions (persistence_id, channel, created_at, last_activity, status, turn_count)
                    VALUES ('session-{i}', 'signalr', {i}, {i * 100}, 'inactive', {i})
                    """);
            }
        }

        var service = CreateService(paths);
        var entries = service.ListRecent(limit: 2, offset: 1);

        Assert.Collection(entries,
            first => Assert.Equal("session-4", first.PersistenceId),
            second => Assert.Equal("session-3", second.PersistenceId));
    }

    [Fact]
    public void LegacyMigration_PreservesSlackChannelInference()
    {
        var paths = CreatePaths();

        using (var conn = OpenConn(paths))
        {
            RunSql(conn,
                """
                CREATE TABLE sessions (
                    session_id    TEXT PRIMARY KEY,
                    last_activity INTEGER,
                    message_count INTEGER,
                    created       INTEGER,
                    display_name  TEXT
                )
                """);
            RunSql(conn, "INSERT INTO sessions VALUES ('C12345/1234567890.000100', 1000, 2, 500, NULL)");
            RunSql(conn, "INSERT INTO sessions VALUES ('signalr/session-1', 2000, 1, 800, 'Hub Session')");
            RunSql(conn, "INSERT INTO sessions VALUES ('unknown-format', 3000, 0, 900, NULL)");
        }

        var service = CreateService(paths);
        var entries = service.ListRecent().OrderBy(e => e.LastActivity).ToList();

        Assert.Equal(3, entries.Count);
        Assert.Equal("slack", entries[0].Channel);    // C12345/...
        Assert.Equal("signalr", entries[1].Channel);  // signalr/...
        Assert.Equal("unknown", entries[2].Channel);  // unknown-format
    }

    [Fact]
    public void LegacyMigration_InfersSlackChannel_ForPrivateChannelIds()
    {
        var paths = CreatePaths();

        using (var conn = OpenConn(paths))
        {
            RunSql(conn,
                """
                CREATE TABLE sessions (
                    session_id    TEXT PRIMARY KEY,
                    last_activity INTEGER,
                    message_count INTEGER,
                    created       INTEGER,
                    display_name  TEXT
                )
                """);
            RunSql(conn, "INSERT INTO sessions VALUES ('G12345/1234567890.000100', 1000, 1, 500, NULL)");
        }

        var service = CreateService(paths);
        var entries = service.ListRecent();

        Assert.Single(entries);
        Assert.Equal("slack", entries[0].Channel);
    }

    [Fact]
    public void GetStats_ReturnsAggregates_AcrossMultipleSessions()
    {
        var paths = CreatePaths();

        // Seed current schema with multiple sessions
        using (var conn = OpenConn(paths))
        {
            RunSql(conn,
                """
                CREATE TABLE sessions (
                    persistence_id    TEXT NOT NULL PRIMARY KEY,
                    channel           TEXT NOT NULL,
                    created_at        INTEGER NOT NULL,
                    last_activity     INTEGER NOT NULL,
                    status            TEXT NOT NULL DEFAULT 'active',
                    turn_count        INTEGER NOT NULL DEFAULT 0,
                    title             TEXT,
                    description       TEXT,
                    last_input_tokens INTEGER,
                    log_path          TEXT,
                    metadata          TEXT
                )
                """);
            RunSql(conn,
                "INSERT INTO sessions (persistence_id, channel, created_at, last_activity, status, turn_count) VALUES ('session-1', 'slack', 100, 200, 'active', 10)");
            RunSql(conn,
                "INSERT INTO sessions (persistence_id, channel, created_at, last_activity, status, turn_count) VALUES ('session-2', 'slack', 100, 300, 'active', 5)");
            RunSql(conn,
                "INSERT INTO sessions (persistence_id, channel, created_at, last_activity, status, turn_count) VALUES ('session-3', 'signalr', 100, 400, 'finished', 3)");
        }

        var service = CreateService(paths);
        var stats = service.GetStats();

        Assert.Equal(3, stats.TotalSessions);
        Assert.Equal(2, stats.ActiveSessions);
        Assert.Equal(18, stats.TotalTurns);
    }

    [Fact]
    public void GetStats_ReturnsZeros_WhenNoSessions()
    {
        var paths = CreatePaths();
        var service = CreateService(paths);

        var stats = service.GetStats();

        Assert.Equal(0, stats.TotalSessions);
        Assert.Equal(0, stats.ActiveSessions);
        Assert.Equal(0, stats.TotalTurns);
    }

    [Fact]
    public void OnOutput_UpdatesLastInputTokens_WhenInputTokensPresent()
    {
        var paths = CreatePaths();
        var metrics = new FakeMetrics();
        var service = CreateService(paths, metrics);
        var sessionId = new SessionId("signalr/test-usage-2");

        service.OnSessionActivated(sessionId, ChannelType.SignalR);

        service.OnOutput(new UsageOutput
        {
            SessionId = sessionId,
            InputTokens = 1000,
            OutputTokens = 250
        });

        // Token recording is now done by LlmSessionActor, not SessionCatalogService
        Assert.Empty(metrics.TokenUsageCalls);

        // Verify last_input_tokens was updated in SQLite
        using var conn = OpenConn(paths);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_input_tokens FROM sessions WHERE persistence_id = $pid";
        cmd.Parameters.AddWithValue("$pid", $"session-{sessionId.Value}");
        var result = cmd.ExecuteScalar();
        Assert.Equal(1000L, result);
    }

    [Fact]
    public void OnOutput_DoesNotUpdateLastInputTokens_WhenInputTokensNull()
    {
        var paths = CreatePaths();
        var metrics = new FakeMetrics();
        var service = CreateService(paths, metrics);
        var sessionId = new SessionId("signalr/test-usage-3");

        service.OnSessionActivated(sessionId, ChannelType.SignalR);

        service.OnOutput(new UsageOutput
        {
            SessionId = sessionId,
            InputTokens = null,
            OutputTokens = 500
        });

        Assert.Empty(metrics.TokenUsageCalls);

        // last_input_tokens should remain null
        using var conn = OpenConn(paths);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_input_tokens FROM sessions WHERE persistence_id = $pid";
        cmd.Parameters.AddWithValue("$pid", $"session-{sessionId.Value}");
        var result = cmd.ExecuteScalar();
        Assert.Equal(DBNull.Value, result);
    }

    [Fact]
    public void OnSessionDeactivated_SetsStatusToInactive()
    {
        var paths = CreatePaths();
        var service = CreateService(paths);
        var sessionId = new SessionId("signalr/test-end-1");

        service.OnSessionActivated(sessionId, ChannelType.SignalR);

        // Verify initially active
        var stats = service.GetStats();
        Assert.Equal(1, stats.ActiveSessions);

        service.OnSessionDeactivated(sessionId);

        // Should now be inactive
        stats = service.GetStats();
        Assert.Equal(1, stats.TotalSessions);
        Assert.Equal(0, stats.ActiveSessions);
        Assert.Equal("inactive", service.ListRecent().Single().Status);
    }

    [Fact]
    public void OnSessionActivated_ResetsInactiveToActive()
    {
        var paths = CreatePaths();
        var service = CreateService(paths);
        var sessionId = new SessionId("signalr/test-resume-1");

        service.OnSessionActivated(sessionId, ChannelType.SignalR);
        service.OnSessionDeactivated(sessionId);

        // Resume — should set back to active
        service.OnSessionActivated(sessionId, ChannelType.SignalR);

        var stats = service.GetStats();
        Assert.Equal(1, stats.TotalSessions);
        Assert.Equal(1, stats.ActiveSessions);
    }

    [Fact]
    public void OnOutput_TurnCompleted_Skipped_DoesNotIncrementTurnCount()
    {
        var paths = CreatePaths();
        var metrics = new FakeMetrics();
        var service = CreateService(paths, metrics: metrics);
        var sessionId = new SessionId("slack/skipped-turn-test");
        service.OnSessionActivated(sessionId, ChannelType.Slack);

        service.OnOutput(new TurnCompleted
        {
            SessionId = sessionId,
            TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(0),
            Outcome = TurnOutcome.Skipped
        });

        var stats = service.GetStats();
        Assert.Equal(0, stats.TotalTurns);
        Assert.Equal(0, metrics.TurnCompletedCalls);
    }

    [Fact]
    public void OnOutput_TurnCompleted_Failed_IncrementsTurnCount()
    {
        var paths = CreatePaths();
        var metrics = new FakeMetrics();
        var service = CreateService(paths, metrics: metrics);
        var sessionId = new SessionId("slack/failed-turn-test");
        service.OnSessionActivated(sessionId, ChannelType.Slack);

        service.OnOutput(new TurnCompleted
        {
            SessionId = sessionId,
            TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1),
            Outcome = TurnOutcome.Failed
        });

        var stats = service.GetStats();
        Assert.Equal(1, stats.TotalTurns);
        Assert.Equal(1, metrics.TurnCompletedCalls);
    }

    [Fact]
    public void OnSessionActivated_DoesNotRewriteLastActivity_ForExistingSession()
    {
        var paths = CreatePaths();
        var fakeTime = new FakeTimeProvider(DateTimeOffset.Parse("2026-03-21T10:00:00Z"));
        var service = CreateService(paths, timeProvider: fakeTime);
        var sessionId = new SessionId("signalr/test-active-last-activity");

        service.OnSessionActivated(sessionId, ChannelType.SignalR);
        service.OnOutput(new TurnCompleted
        {
            SessionId = sessionId,
            TurnNumber = new Netclaw.Actors.Protocol.TurnNumber(1)
        });

        var beforeResume = service.ListRecent().Single().LastActivity;

        fakeTime.Advance(TimeSpan.FromMinutes(10));
        service.OnSessionDeactivated(sessionId);
        service.OnSessionActivated(sessionId, ChannelType.SignalR);

        var entry = service.ListRecent().Single();
        Assert.Equal("active", entry.Status);
        Assert.Equal(beforeResume, entry.LastActivity);
    }

    private sealed class FakeMetrics : ISessionMetrics
    {
        public List<(long Input, long Output)> TokenUsageCalls { get; } = [];
        public int TurnCompletedCalls { get; private set; }

        public void RecordTokenUsage(long inputTokens, long outputTokens)
            => TokenUsageCalls.Add((inputTokens, outputTokens));

        public void RecordTurnCompleted() => TurnCompletedCalls++;
        public void RecordSessionCreated() { }
        public void RecordMemoriesFormed(int count) { }
        public void RecordMemoriesRecalled(int count) { }
        public void RecordSkillsLoaded(int count) { }
        public void RecordSkillLoaded(string skillName, SkillLoadMethod method) { }
    }

    [Fact]
    public void EnsureSchema_AddsManagementColumns_ToPreChangeSchema()
    {
        var paths = CreatePaths();

        // Seed the pre-change current schema (persistence_id, no pinned/archived)
        // with one row, then let any service call migrate it.
        using (var conn = OpenConn(paths))
        {
            RunSql(conn,
                """
                CREATE TABLE sessions (
                    persistence_id    TEXT NOT NULL PRIMARY KEY,
                    channel           TEXT NOT NULL,
                    created_at        INTEGER NOT NULL,
                    last_activity     INTEGER NOT NULL,
                    status            TEXT NOT NULL DEFAULT 'active',
                    turn_count        INTEGER NOT NULL DEFAULT 0,
                    title             TEXT,
                    description       TEXT,
                    last_input_tokens INTEGER,
                    log_path          TEXT,
                    metadata          TEXT
                )
                """);
            RunSql(conn,
                """
                INSERT INTO sessions (persistence_id, channel, created_at, last_activity, status, turn_count, title)
                VALUES ('session-signalr/old-row', 'signalr', 1, 2, 'inactive', 3, 'Kept title')
                """);
        }

        var service = CreateService(CreatePaths());
        var entries = service.ListRecent();

        var entry = Assert.Single(entries);
        Assert.Equal("session-signalr/old-row", entry.PersistenceId);
        Assert.Equal("Kept title", entry.Title);
        Assert.False(entry.Pinned);
        Assert.False(entry.Archived);

        using var verify = OpenConn(paths);
        Assert.True(ColumnExists(verify, "sessions", "pinned"));
        Assert.True(ColumnExists(verify, "sessions", "archived"));
    }

    [Fact]
    public void ListRecent_Filters_ExcludeArchived_ByDefault_AndSupportPinnedOnly()
    {
        var paths = CreatePaths();
        var service = CreateService(paths);
        service.OnSessionActivated(new SessionId("signalr/plain"), ChannelType.SignalR);
        service.OnSessionActivated(new SessionId("signalr/pinned"), ChannelType.SignalR);
        service.OnSessionActivated(new SessionId("signalr/archived"), ChannelType.SignalR);

        Assert.True(service.SetPinned("signalr/pinned", pinned: true));
        Assert.True(service.SetArchived("signalr/archived", archived: true));

        var defaults = service.ListRecent();
        Assert.Equal(2, defaults.Count);
        Assert.DoesNotContain(defaults, e => e.PersistenceId == "session-signalr/archived");

        var withArchived = service.ListRecent(includeArchived: true);
        Assert.Equal(3, withArchived.Count);
        Assert.True(Assert.Single(withArchived, e => e.PersistenceId == "session-signalr/archived").Archived);

        var pinnedOnly = service.ListRecent(pinnedOnly: true);
        Assert.Equal("session-signalr/pinned", Assert.Single(pinnedOnly).PersistenceId);
        Assert.True(pinnedOnly[0].Pinned);
    }

    [Fact]
    public void Unarchive_And_Unpin_RoundTrip()
    {
        var paths = CreatePaths();
        var service = CreateService(paths);
        service.OnSessionActivated(new SessionId("signalr/toggle"), ChannelType.SignalR);

        Assert.True(service.SetArchived("signalr/toggle", archived: true));
        Assert.Empty(service.ListRecent());

        Assert.True(service.SetArchived("signalr/toggle", archived: false));
        Assert.Single(service.ListRecent());

        Assert.True(service.SetPinned("signalr/toggle", pinned: true));
        Assert.True(Assert.Single(service.ListRecent()).Pinned);
        Assert.True(service.SetPinned("signalr/toggle", pinned: false));
        Assert.False(Assert.Single(service.ListRecent()).Pinned);
    }

    [Fact]
    public void ManagementFlags_RejectUnknownSession()
    {
        var paths = CreatePaths();
        var service = CreateService(paths);

        Assert.False(service.SetPinned("signalr/ghost", pinned: true));
        Assert.False(service.SetArchived("signalr/ghost", archived: true));
    }

    [Fact]
    public void CatalogEntry_WireShape_CarriesManagementFlags_InCamelCase()
    {
        // The GUI's SessionCatalogEntryDto binds these properties by name
        // over GET /api/sessions (web defaults → camelCase). A rename here
        // breaks the client silently, so pin the wire contract.
        var paths = CreatePaths();
        var service = CreateService(paths);
        service.OnSessionActivated(new SessionId("signalr/wire"), ChannelType.SignalR);
        Assert.True(service.SetPinned("signalr/wire", pinned: true));
        Assert.True(service.SetArchived("signalr/wire", archived: true));

        var entry = Assert.Single(service.ListRecent(includeArchived: true));
        var json = System.Text.Json.JsonSerializer.Serialize(
            entry, System.Text.Json.JsonSerializerOptions.Web);

        Assert.Contains("\"pinned\":true", json);
        Assert.Contains("\"archived\":true", json);
    }

    [Fact]
    public void DeleteCatalogRow_RemovesTheRow_AndReportsMissing()
    {
        var paths = CreatePaths();
        var service = CreateService(paths);
        service.OnSessionActivated(new SessionId("signalr/doomed"), ChannelType.SignalR);

        Assert.True(service.DeleteCatalogRow("signalr/doomed"));
        Assert.Empty(service.ListRecent(includeArchived: true));
        Assert.False(service.DeleteCatalogRow("signalr/doomed"));
    }

    private static SqliteConnection OpenConn(NetclawPaths paths)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        conn.Open();
        return conn;
    }

    private static void RunSql(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(SqliteConnection conn, string tableName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name";
        cmd.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    private static bool ColumnExists(SqliteConnection conn, string tableName, string columnName)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({tableName})";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static List<(string persistenceId, string channel)> ReadAllSessions(SqliteConnection conn)
    {
        var rows = new List<(string, string)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT persistence_id, channel FROM sessions";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add((reader.GetString(0), reader.GetString(1)));
        return rows;
    }
}
