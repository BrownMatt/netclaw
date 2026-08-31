// -----------------------------------------------------------------------
// <copyright file="LogEndpointTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;
using Netclaw.Configuration;
using Netclaw.Daemon.Gateway;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

/// <summary>
/// Log tail routes: newest daemon log resolution, session log by id, the
/// 2000-line cap, unknown-session rejection, and operator authentication.
/// </summary>
public sealed class LogEndpointTests : IDisposable
{
    private readonly string _tempBase = Path.Combine(Path.GetTempPath(), $"netclaw-log-endpoint-{Guid.NewGuid():N}");
    private readonly NetclawPaths _paths;

    public LogEndpointTests()
    {
        _paths = new NetclawPaths(_tempBase);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempBase))
            Directory.Delete(_tempBase, recursive: true);
    }

    [Fact]
    public async Task Daemon_tail_serves_the_newest_rolled_file()
    {
        File.WriteAllText(Path.Combine(_paths.LogsDirectory, "daemon-2026-08-30.log"), "old line\n");
        File.WriteAllText(Path.Combine(_paths.LogsDirectory, "daemon-2026-08-31.log"), "new line\n");
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var result = await client.GetFromJsonAsync<LogTailResult>(
            "/api/logs/daemon?tail=10", TestContext.Current.CancellationToken);

        Assert.Equal("daemon-2026-08-31.log", result!.FileName);
        Assert.Equal(["new line"], result.Lines);
    }

    [Fact]
    public async Task Daemon_tail_caps_an_oversized_request()
    {
        File.WriteAllLines(
            Path.Combine(_paths.LogsDirectory, "daemon-2026-08-31.log"),
            Enumerable.Range(1, 2100).Select(i => $"line {i}"));
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var result = await client.GetFromJsonAsync<LogTailResult>(
            "/api/logs/daemon?tail=99999", TestContext.Current.CancellationToken);

        Assert.Equal(2000, result!.Lines.Count);
        Assert.Equal("line 101", result.Lines[0]);
        Assert.Equal("line 2100", result.Lines[^1]);
    }

    [Fact]
    public async Task Daemon_tail_without_a_log_file_is_not_found()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/logs/daemon", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Session_tail_resolves_the_log_by_sanitized_id()
    {
        var sessionId = new SessionId("signalr/log-target");
        var logDir = SessionLogFile.GetLogsDirectory(sessionId, _paths.SessionLogsDirectory);
        Directory.CreateDirectory(logDir);
        File.WriteAllText(SessionLogFile.GetLogPath(sessionId, _paths.SessionLogsDirectory), "session line\n");
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var result = await client.GetFromJsonAsync<LogTailResult>(
            "/api/logs/session?sessionId=signalr/log-target&tail=10",
            TestContext.Current.CancellationToken);

        Assert.Equal(["session line"], result!.Lines);
    }

    [Fact]
    public async Task Unknown_session_is_not_found()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.GetAsync(
            "/api/logs/session?sessionId=signalr/ghost", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Unauthenticated_log_requests_are_refused()
    {
        File.WriteAllText(Path.Combine(_paths.LogsDirectory, "daemon-2026-08-31.log"), "secret\n");
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var daemon = await client.GetAsync("/api/logs/daemon", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, daemon.StatusCode);

        var session = await client.GetAsync(
            "/api/logs/session?sessionId=signalr/x", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, session.StatusCode);
    }

    private async Task<WebApplication> CreateAppAsync(bool spoofLoopback)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(_paths);
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
        app.MapLogEndpoints();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
