// -----------------------------------------------------------------------
// <copyright file="RunningModelsServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Netclaw.Configuration;
using Netclaw.Daemon.Providers;
using Netclaw.Daemon.Security;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

/// <summary>
/// Running-models composition: only descriptors that implement
/// <see cref="IRunningModelsProbe"/> participate; absence of the concept is
/// silent, probe failure is loud.
/// </summary>
public sealed class RunningModelsServiceTests
{
    private static RunningModelsService CreateService(
        Dictionary<string, ProviderEntry> providers,
        params IProviderDescriptor[] descriptors)
        => new(
            providers,
            new ProviderDescriptorRegistry(descriptors),
            NullLogger<RunningModelsService>.Instance);

    [Fact]
    public async Task Provider_with_the_probe_reports_its_loaded_models()
    {
        var service = CreateService(
            new Dictionary<string, ProviderEntry>
            {
                ["my-ollama"] = new() { Type = "fake-running" },
                ["my-cloud"] = new() { Type = "fake-plain" },
            },
            new FakeRunningDescriptor(new RunningModelsProbeResult(
                true, null, [new RunningModel("qwen3:8b", 123L, null)])),
            new FakePlainDescriptor());

        var response = await service.GetRunningModelsAsync(TestContext.Current.CancellationToken);

        // The plain provider contributes nothing and is NOT listed as failed.
        var provider = Assert.Single(response.Providers);
        Assert.Equal("my-ollama", provider.ProviderKey);
        Assert.True(provider.Ok);
        var model = Assert.Single(provider.Models);
        Assert.Equal("qwen3:8b", model.Id);
        Assert.Equal(123L, model.SizeBytes);
    }

    [Fact]
    public async Task Failed_probe_is_reported_with_a_reason()
    {
        var service = CreateService(
            new Dictionary<string, ProviderEntry> { ["my-ollama"] = new() { Type = "fake-running" } },
            new FakeRunningDescriptor(new RunningModelsProbeResult(
                false, "Connection failed: refused", [])));

        var response = await service.GetRunningModelsAsync(TestContext.Current.CancellationToken);

        var provider = Assert.Single(response.Providers);
        Assert.False(provider.Ok);
        Assert.Contains("refused", provider.Error);
        Assert.Empty(provider.Models);
    }

    [Fact]
    public async Task Throwing_probe_is_reported_failed_not_thrown()
    {
        var service = CreateService(
            new Dictionary<string, ProviderEntry> { ["my-ollama"] = new() { Type = "fake-running" } },
            new FakeRunningDescriptor(exception: new InvalidOperationException("boom")));

        var response = await service.GetRunningModelsAsync(TestContext.Current.CancellationToken);

        var provider = Assert.Single(response.Providers);
        Assert.False(provider.Ok);
        Assert.Contains("boom", provider.Error);
    }

    [Fact]
    public async Task Endpoint_serves_the_listing_and_requires_authentication()
    {
        var service = CreateService(
            new Dictionary<string, ProviderEntry> { ["my-ollama"] = new() { Type = "fake-running" } },
            new FakeRunningDescriptor(new RunningModelsProbeResult(
                true, null, [new RunningModel("qwen3:8b", null, null)])));

        // Unauthenticated (no loopback spoof) is refused.
        await using (var app = await CreateAppAsync(service, spoofLoopback: false))
        {
            var refused = await app.GetTestClient().GetAsync(
                "/api/models/running", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        }

        // Authenticated succeeds with the composed listing.
        await using (var app = await CreateAppAsync(service, spoofLoopback: true))
        {
            var response = await app.GetTestClient().GetFromJsonAsync<RunningModelsResponse>(
                "/api/models/running", TestContext.Current.CancellationToken);
            var provider = Assert.Single(response!.Providers);
            Assert.Equal("qwen3:8b", Assert.Single(provider.Models).Id);
        }
    }

    private static async Task<WebApplication> CreateAppAsync(
        RunningModelsService service, bool spoofLoopback)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Services.AddSingleton(service);
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

    private class FakePlainDescriptor : IProviderDescriptor
    {
        public virtual string TypeKey => "fake-plain";

        public string DisplayName => "Fake";

        public string DefaultEndpoint => "http://fake.test";

        public string ModelListingPath => "/models";

        public IProviderAuth Auth { get; } = new EndpointOnlyAuth();

        public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
            => Task.FromResult(new ProviderProbeResult(true, null, []));
    }

    private sealed class FakeRunningDescriptor : FakePlainDescriptor, IRunningModelsProbe
    {
        private readonly RunningModelsProbeResult? _result;
        private readonly Exception? _exception;

        public FakeRunningDescriptor(
            RunningModelsProbeResult? result = null, Exception? exception = null)
        {
            _result = result;
            _exception = exception;
        }

        public override string TypeKey => "fake-running";

        public Task<RunningModelsProbeResult> ProbeRunningAsync(
            ProviderEntry entry, CancellationToken ct = default)
            => _exception is not null
                ? Task.FromException<RunningModelsProbeResult>(_exception)
                : Task.FromResult(_result!);
    }
}
