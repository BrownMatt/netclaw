// -----------------------------------------------------------------------
// <copyright file="ModelEndpointTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Providers;
using Netclaw.Daemon.Security;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class ModelEndpointTests
{
    private sealed class FakeProbe : IProviderProbe
    {
        public Task<ProviderProbeResult> ProbeAsync(
            string providerType, string? endpoint, string? apiKey, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
            => Task.FromResult(new ProviderProbeResult(
                true, null, [new DiscoveredModel { ModelId = new ModelId("qwen3:30b") }]));

        public Task<ProviderProbeResult> ProbeAsync(
            string providerType, string? endpoint, string? credential,
            AuthMethod authMethod, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task Authenticated_request_lists_provider_models()
    {
        await using var app = await CreateAppAsync(spoofLoopback: true);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/models", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(
            TestContext.Current.CancellationToken);
        var provider = Assert.Single(json.GetProperty("providers").EnumerateArray());
        Assert.Equal("local-ollama", provider.GetProperty("providerKey").GetString());
        Assert.True(provider.GetProperty("ok").GetBoolean());
        var model = Assert.Single(provider.GetProperty("models").EnumerateArray());
        Assert.Equal("qwen3:30b", model.GetProperty("id").GetString());
        Assert.Equal("unknown", model.GetProperty("toolSupport").GetString());
    }

    [Fact]
    public async Task Unauthenticated_request_is_refused()
    {
        await using var app = await CreateAppAsync(spoofLoopback: false);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/models", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<WebApplication> CreateAppAsync(bool spoofLoopback)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var providers = new Dictionary<string, ProviderEntry>
        {
            ["local-ollama"] = new() { Type = "openrouter" } // no enricher path in tests
        };
        builder.Services.AddSingleton(new ModelCatalogService(
            providers,
            new FakeProbe(),
            _ => null,
            new FakeTimeProvider(),
            NullLogger<ModelCatalogService>.Instance));

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
        app.MapModelEndpoints();

        await app.StartAsync(TestContext.Current.CancellationToken);
        return app;
    }
}
