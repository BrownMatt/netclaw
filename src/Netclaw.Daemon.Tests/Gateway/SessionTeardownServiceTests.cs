// -----------------------------------------------------------------------
// <copyright file="SessionTeardownServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Akka.Hosting.TestKit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Hosting;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Providers;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;
using ChannelType = Netclaw.Actors.Channels.ChannelType;

namespace Netclaw.Daemon.Tests.Gateway;

/// <summary>
/// Multi-store delete teardown: every store keyed by the session's
/// persistence identity is removed, the actors stop, a partial failure is
/// reported per step, and an unknown session changes nothing.
/// </summary>
public sealed class SessionTeardownServiceTests : TestKit
{
    private const string SessionIdValue = "signalr/teardown-target";

    private readonly string _tempBase = Path.Combine(Path.GetTempPath(), $"netclaw-teardown-{Guid.NewGuid():N}");
    private NetclawPaths? _paths;

    public SessionTeardownServiceTests(ITestOutputHelper output) : base(output: output) { }

    protected override void ConfigureAkka(AkkaConfigurationBuilder builder, IServiceProvider provider)
    {
        // No hosted actors needed; parents are created per test.
    }

    private NetclawPaths Paths
    {
        get
        {
            if (_paths is null)
            {
                _paths = new NetclawPaths(_tempBase);
                _paths.EnsureDirectoriesExist();
            }

            return _paths;
        }
    }

    /// <summary>Parent actor that spawns one idle, named child in its ctor.</summary>
    private sealed class ParentWithChild : ReceiveActor
    {
        public ParentWithChild(string childName)
            => Context.ActorOf(Props.Create(() => new IdleChild()), childName);
    }

    private sealed class IdleChild : ReceiveActor;

    private sealed class FakeRequiredActor<TKey>(IActorRef actorRef) : IRequiredActor<TKey>
    {
        public IActorRef ActorRef => actorRef;

        public Task<IActorRef> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(actorRef);
    }

    private sealed class FakeHubClient : ISessionHubClient
    {
        public List<SessionOutputDto> Received { get; } = [];

        public Task ReceiveOutput(SessionOutputDto output)
        {
            Received.Add(output);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHubClients(ISessionHubClient client) : IHubClients<ISessionHubClient>
    {
        public ISessionHubClient All => client;
        public ISessionHubClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => client;
        public ISessionHubClient Client(string connectionId) => client;
        public ISessionHubClient Clients(IReadOnlyList<string> connectionIds) => client;
        public ISessionHubClient Group(string groupName) => client;
        public ISessionHubClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => client;
        public ISessionHubClient Groups(IReadOnlyList<string> groupNames) => client;
        public ISessionHubClient User(string userId) => client;
        public ISessionHubClient Users(IReadOnlyList<string> userIds) => client;
    }

    private sealed class FakeHubContext(ISessionHubClient client) : IHubContext<SessionHub, ISessionHubClient>
    {
        public IHubClients<ISessionHubClient> Clients { get; } = new FakeHubClients(client);

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private async Task<(SessionTeardownService Service, SessionCatalogService Catalog, SessionRegistry Registry, IActorRef ManagerChildWatchTarget, IActorRef LogChildWatchTarget, FakeHubClient HubClient)>
        BuildHarnessAsync(string managerName, string logDispatcherName)
    {
        var childName = Uri.EscapeDataString(SessionIdValue);
        var manager = Sys.ActorOf(Props.Create(() => new ParentWithChild(childName)), managerName);
        var logDispatcher = Sys.ActorOf(Props.Create(() => new ParentWithChild(childName)), logDispatcherName);

        // Child creation is asynchronous after ActorOf — poll until resolvable.
        IActorRef managerChild = ActorRefs.Nobody;
        IActorRef logChild = ActorRefs.Nobody;
        await AwaitAssertAsync(async () =>
        {
            managerChild = await Sys.ActorSelection(manager.Path / childName)
                .ResolveOne(TimeSpan.FromMilliseconds(250));
            logChild = await Sys.ActorSelection(logDispatcher.Path / childName)
                .ResolveOne(TimeSpan.FromMilliseconds(250));
        });

        var catalog = new SessionCatalogService(
            Paths, TimeProvider.System, NullLogger<SessionCatalogService>.Instance);
        var registry = new SessionRegistry(
            new FakeRequiredActor<SignalRGatewayActorKey>(ActorRefs.Nobody),
            new FakeSessionPipeline(),
            new SessionIngressGate(),
            new ClaimsPrincipalMapper(),
            BuildModelCatalog(),
            TimeProvider.System,
            NullLogger<SessionRegistry>.Instance);
        var hubClient = new FakeHubClient();

        var service = new SessionTeardownService(
            registry,
            catalog,
            new FakeRequiredActor<SessionManagerActorKey>(manager),
            new FakeRequiredActor<SessionLogDispatcherActorKey>(logDispatcher),
            Sys,
            new FakeHubContext(hubClient),
            Paths,
            TimeProvider.System,
            NullLogger<SessionTeardownService>.Instance);

        return (service, catalog, registry, managerChild, logChild, hubClient);
    }

    private static ModelCatalogService BuildModelCatalog()
        => new(
            new Dictionary<string, ProviderEntry>(),
            new ThrowingProbe(),
            _ => null,
            new Microsoft.Extensions.Time.Testing.FakeTimeProvider(),
            NullLogger<ModelCatalogService>.Instance);

    private sealed class ThrowingProbe : IProviderProbe
    {
        public Task<ProviderProbeResult> ProbeAsync(
            string providerType, string? endpoint, string? apiKey, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProviderProbeResult> ProbeAsync(
            ProviderEntry entry, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProviderProbeResult> ProbeAsync(
            string providerType, string? endpoint, string? credential,
            AuthMethod authMethod, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeSessionPipeline : ISessionPipeline
    {
        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            Akka.Streams.IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(
            IWithSessionId feedback, CancellationToken ct = default)
            => Task.FromResult<ISessionResponse>(CommandAck.For(feedback.SessionId));
    }

    private void SeedStores()
    {
        var sessionId = new SessionId(SessionIdValue);
        var persistenceId = $"session-{SessionIdValue}";

        // Persistence rows in the same tables the daemon migration creates.
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        using (var conn = new SqliteConnection(connectionString))
        {
            conn.Open();
            Run(conn, "CREATE TABLE IF NOT EXISTS journal (ordering INTEGER PRIMARY KEY AUTOINCREMENT, persistence_id TEXT NOT NULL, sequence_number INTEGER NOT NULL, message BLOB)");
            Run(conn, "CREATE TABLE IF NOT EXISTS journal_metadata (persistence_id TEXT NOT NULL, sequence_number INTEGER NOT NULL)");
            Run(conn, "CREATE TABLE IF NOT EXISTS tags (ordering_id INTEGER NOT NULL, tag TEXT NOT NULL, sequence_nr INTEGER NOT NULL, persistence_id TEXT NOT NULL)");
            Run(conn, "CREATE TABLE IF NOT EXISTS snapshot (persistence_id TEXT NOT NULL, sequence_number INTEGER NOT NULL, snapshot BLOB)");
            Run(conn, $"INSERT INTO journal (persistence_id, sequence_number) VALUES ('{persistenceId}', 1)");
            Run(conn, $"INSERT INTO journal_metadata (persistence_id, sequence_number) VALUES ('{persistenceId}', 1)");
            Run(conn, $"INSERT INTO tags (ordering_id, tag, sequence_nr, persistence_id) VALUES (1, 't', 1, '{persistenceId}')");
            Run(conn, $"INSERT INTO snapshot (persistence_id, sequence_number) VALUES ('{persistenceId}', 1)");
            // A second session's rows must survive the teardown untouched.
            Run(conn, "INSERT INTO journal (persistence_id, sequence_number) VALUES ('session-signalr/other', 1)");
        }

        // Session directory, staging directory, and session log.
        var sessionDir = SessionDirectoryHelper.GetSessionDirectory(sessionId, Paths.SessionsDirectory);
        Directory.CreateDirectory(Path.Combine(sessionDir, "inbox"));
        File.WriteAllText(Path.Combine(sessionDir, "inbox", "upload.txt"), "staged upload");
        var stagingDir = SessionDirectoryHelper.GetOrCreateAttachmentStagingDirectory(sessionId, Paths.SessionsDirectory);
        File.WriteAllText(Path.Combine(stagingDir, "pending.bin"), "bytes");
        var logDir = SessionLogFile.GetLogsDirectory(sessionId, Paths.SessionLogsDirectory);
        Directory.CreateDirectory(logDir);
        File.WriteAllText(Path.Combine(logDir, SessionLogFile.FileName), "log line");
    }

    private static void Run(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private long CountRows(string table, string persistenceId)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Paths.SqliteDbPath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE persistence_id = $pid";
        cmd.Parameters.AddWithValue("$pid", persistenceId);
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task Delete_removes_every_store_and_stops_both_actors()
    {
        var (service, catalog, _, managerChild, logChild, _) =
            await BuildHarnessAsync("session-manager-full", "session-log-dispatcher-full");
        catalog.OnSessionActivated(new SessionId(SessionIdValue), Netclaw.Actors.Channels.ChannelType.SignalR);
        SeedStores();
        var deathWatch = CreateTestProbe();
        deathWatch.Watch(managerChild);
        deathWatch.Watch(logChild);

        var report = await service.DeleteSessionAsync(SessionIdValue, TestContext.Current.CancellationToken);

        Assert.NotNull(report);
        Assert.True(report.Deleted, string.Join("; ", report.Steps.Select(s => $"{s.Name}:{s.Ok}:{s.Error}")));

        await deathWatch.ExpectTerminatedAsync(managerChild, cancellationToken: TestContext.Current.CancellationToken);
        await deathWatch.ExpectTerminatedAsync(logChild, cancellationToken: TestContext.Current.CancellationToken);

        var persistenceId = $"session-{SessionIdValue}";
        Assert.Equal(0, CountRows("journal", persistenceId));
        Assert.Equal(0, CountRows("journal_metadata", persistenceId));
        Assert.Equal(0, CountRows("tags", persistenceId));
        Assert.Equal(0, CountRows("snapshot", persistenceId));
        // Restart recovery reads the catalog — the row is gone, so nothing recovers.
        Assert.Empty(catalog.ListRecent(includeArchived: true));
        // Another session's journal rows survive.
        Assert.Equal(1, CountRows("journal", "session-signalr/other"));

        var sessionId = new SessionId(SessionIdValue);
        Assert.False(Directory.Exists(SessionDirectoryHelper.GetSessionDirectory(sessionId, Paths.SessionsDirectory)));
        Assert.False(Directory.Exists(Path.Combine(
            Paths.SessionsDirectory,
            SessionDirectoryHelper.AttachmentStagingRootSubdirectory,
            SessionDirectoryHelper.SanitizeSessionId(sessionId))));
        Assert.False(Directory.Exists(SessionLogFile.GetLogsDirectory(sessionId, Paths.SessionLogsDirectory)));
    }

    [Fact]
    public async Task Unknown_session_is_rejected_and_nothing_changes()
    {
        var (service, _, _, _, _, _) =
            await BuildHarnessAsync("session-manager-unknown", "session-log-dispatcher-unknown");

        var report = await service.DeleteSessionAsync("signalr/ghost", TestContext.Current.CancellationToken);

        Assert.Null(report);
    }

    [Fact]
    public async Task Locked_session_log_reports_the_failed_step_and_never_claims_success()
    {
        var (service, catalog, _, _, _, _) =
            await BuildHarnessAsync("session-manager-locked", "session-log-dispatcher-locked");
        catalog.OnSessionActivated(new SessionId(SessionIdValue), Netclaw.Actors.Channels.ChannelType.SignalR);
        SeedStores();

        var logPath = SessionLogFile.GetLogPath(new SessionId(SessionIdValue), Paths.SessionLogsDirectory);
        await using (File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var report = await service.DeleteSessionAsync(SessionIdValue, TestContext.Current.CancellationToken);

            Assert.NotNull(report);
            Assert.False(report.Deleted);
            var failed = Assert.Single(report.Steps, s => !s.Ok);
            Assert.Equal("delete-session-log", failed.Name);
            Assert.NotNull(failed.Error);
            // The independent steps still completed.
            Assert.True(report.Steps.Single(s => s.Name == "delete-persistence-rows").Ok);
            Assert.True(report.Steps.Single(s => s.Name == "delete-catalog-row").Ok);
        }
    }

    [Fact]
    public async Task Ensure_during_teardown_is_rejected()
    {
        var (_, _, registry, _, _, _) =
            await BuildHarnessAsync("session-manager-block", "session-log-dispatcher-block");
        var sessionId = (await registry.EnsureSessionAsync("conn-1", null, "tui")).SessionId;

        await registry.BeginTeardownAsync(sessionId, _ => Task.CompletedTask);

        await Assert.ThrowsAsync<HubException>(
            () => registry.EnsureSessionAsync("conn-2", sessionId, "tui"));
    }

    [Fact]
    public async Task Delete_endpoint_deletes_rejects_unknown_and_requires_auth()
    {
        var (service, catalog, _, _, _, _) =
            await BuildHarnessAsync("session-manager-endpoint", "session-log-dispatcher-endpoint");
        catalog.OnSessionActivated(new SessionId(SessionIdValue), ChannelType.SignalR);
        SeedStores();

        await using var app = await CreateDeleteAppAsync(service, catalog, spoofLoopback: true);
        var client = app.GetTestClient();

        var unknown = await client.DeleteAsync(
            "/api/sessions?sessionId=signalr/ghost", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, unknown.StatusCode);

        var ok = await client.DeleteAsync(
            $"/api/sessions?sessionId={Uri.EscapeDataString(SessionIdValue)}", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("\"deleted\":true", body);
        Assert.Empty(catalog.ListRecent(includeArchived: true));

        await using var unauthApp = await CreateDeleteAppAsync(service, catalog, spoofLoopback: false);
        var unauthClient = unauthApp.GetTestClient();
        var unauth = await unauthClient.DeleteAsync(
            "/api/sessions?sessionId=whatever", TestContext.Current.CancellationToken);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauth.StatusCode);
    }

    private static async Task<WebApplication> CreateDeleteAppAsync(
        SessionTeardownService service, SessionCatalogService catalog, bool spoofLoopback)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(service);
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton<ISessionPipeline>(new FakeSessionPipeline());
        Netclaw.Daemon.Security.NetclawAuthExtensions.AddNetclawAuthSchemes(
            builder.Services, new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();

        var app = builder.Build();
        if (spoofLoopback)
        {
            app.Use(async (ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Loopback;
                await next(ctx);
            });
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSessionManagementEndpoints();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
