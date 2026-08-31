// -----------------------------------------------------------------------
// <copyright file="SessionManagementEndpointTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using Akka.Streams;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Actors.Channels;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Security;
using Xunit;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Daemon.Tests.Gateway;

/// <summary>
/// Management REST surface: rename routes through the command pipeline only
/// for cataloged sessions; pin/archive hit the catalog; every route is
/// operator-authenticated.
/// </summary>
public sealed class SessionManagementEndpointTests : IDisposable
{
    private readonly string _tempBase = Path.Combine(Path.GetTempPath(), $"netclaw-mgmt-endpoint-{Guid.NewGuid():N}");
    private readonly FakeSessionPipeline _pipeline = new();
    private readonly SessionCatalogService _catalog;

    public SessionManagementEndpointTests()
    {
        var paths = new NetclawPaths(_tempBase);
        paths.EnsureDirectoriesExist();
        _catalog = new SessionCatalogService(
            paths, TimeProvider.System, NullLogger<SessionCatalogService>.Instance);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempBase))
            Directory.Delete(_tempBase, recursive: true);
    }

    [Fact]
    public async Task Rename_of_a_cataloged_session_sends_the_actor_command()
    {
        _catalog.OnSessionActivated(new SessionId("signalr/known"), Netclaw.Actors.Channels.ChannelType.SignalR);
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/api/sessions/rename?sessionId=signalr/known",
            new SessionManagementEndpointRouteBuilderExtensions.RenameSessionRequest("New title"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var command = Assert.IsType<RenameSession>(Assert.Single(_pipeline.Sent));
        Assert.Equal("signalr/known", command.SessionId.Value);
        Assert.Equal("New title", command.Title);
    }

    [Fact]
    public async Task Rename_of_an_unknown_session_is_rejected_without_an_actor_command()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/api/sessions/rename?sessionId=signalr/ghost",
            new SessionManagementEndpointRouteBuilderExtensions.RenameSessionRequest("New title"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty(_pipeline.Sent);
    }

    [Fact]
    public async Task Empty_title_is_rejected_before_the_pipeline()
    {
        _catalog.OnSessionActivated(new SessionId("signalr/known"), Netclaw.Actors.Channels.ChannelType.SignalR);
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/api/sessions/rename?sessionId=signalr/known",
            new SessionManagementEndpointRouteBuilderExtensions.RenameSessionRequest("   "),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_pipeline.Sent);
    }

    [Fact]
    public async Task Actor_nack_surfaces_as_bad_request_with_the_reason()
    {
        _catalog.OnSessionActivated(new SessionId("signalr/known"), Netclaw.Actors.Channels.ChannelType.SignalR);
        _pipeline.Response = CommandNack.For(new SessionId("signalr/known"), "journal unrecoverable");
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/api/sessions/rename?sessionId=signalr/known",
            new SessionManagementEndpointRouteBuilderExtensions.RenameSessionRequest("New title"),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("journal unrecoverable", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Flags_patch_sets_pin_and_archive_for_a_cataloged_session()
    {
        _catalog.OnSessionActivated(new SessionId("signalr/known"), Netclaw.Actors.Channels.ChannelType.SignalR);
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.PatchAsJsonAsync(
            "/api/sessions/flags?sessionId=signalr/known",
            new SessionManagementEndpointRouteBuilderExtensions.SessionFlagsRequest(Pinned: true, Archived: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var entry = Assert.Single(_catalog.ListRecent(includeArchived: true));
        Assert.True(entry.Pinned);
        Assert.True(entry.Archived);
    }

    [Fact]
    public async Task Flags_patch_rejects_unknown_session_and_empty_body()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var unknown = await client.PatchAsJsonAsync(
            "/api/sessions/flags?sessionId=signalr/ghost",
            new SessionManagementEndpointRouteBuilderExtensions.SessionFlagsRequest(Pinned: true, Archived: null),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);

        var empty = await client.PatchAsJsonAsync(
            "/api/sessions/flags?sessionId=signalr/ghost",
            new SessionManagementEndpointRouteBuilderExtensions.SessionFlagsRequest(Pinned: null, Archived: null),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_requests_are_refused()
    {
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var rename = await client.PostAsJsonAsync(
            "/api/sessions/rename?sessionId=signalr/known",
            new SessionManagementEndpointRouteBuilderExtensions.RenameSessionRequest("New title"),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, rename.StatusCode);

        var flags = await client.PatchAsJsonAsync(
            "/api/sessions/flags?sessionId=signalr/known",
            new SessionManagementEndpointRouteBuilderExtensions.SessionFlagsRequest(Pinned: true, Archived: null),
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, flags.StatusCode);
        Assert.Empty(_pipeline.Sent);
    }

    private async Task<WebApplication> CreateAppAsync(bool spoofLoopback)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(_catalog);
        builder.Services.AddSingleton<ISessionPipeline>(_pipeline);
        builder.Services.AddNetclawAuthSchemes(new DaemonConfig());
        builder.Services.AddAuthorization();
        builder.Services.AddLogging();

        var app = builder.Build();

        if (spoofLoopback)
        {
            app.Use(async (ctx, next) =>
            {
                ctx.Connection.RemoteIpAddress = IPAddress.Loopback;
                await next(ctx);
            });
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapSessionManagementEndpoints();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }

    private sealed class FakeSessionPipeline : ISessionPipeline
    {
        public List<IWithSessionId> Sent { get; } = [];

        public ISessionResponse? Response { get; set; }

        public Task<MaterializedSession> CreateAsync(
            SessionId sessionId,
            SessionPipelineOptions options,
            IMaterializer? materializer = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task SendFeedbackAsync(IWithSessionId feedback, CancellationToken ct = default)
        {
            Sent.Add(feedback);
            return Task.CompletedTask;
        }

        public Task<ISessionResponse> SendFeedbackAndWaitAsync(IWithSessionId feedback, CancellationToken ct = default)
        {
            Sent.Add(feedback);
            return Task.FromResult(Response ?? CommandAck.For(((ISessionCommand)feedback).SessionId));
        }
    }
}
