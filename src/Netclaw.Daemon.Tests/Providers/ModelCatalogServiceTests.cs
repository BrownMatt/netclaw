// -----------------------------------------------------------------------
// <copyright file="ModelCatalogServiceTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Configuration;
using Netclaw.Daemon.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class ModelCatalogServiceTests
{
    private sealed class FakeProbe : IProviderProbe
    {
        private readonly Func<ProviderEntry, ProviderProbeResult> _respond;

        public int ProbeCount { get; private set; }

        public FakeProbe(Func<ProviderEntry, ProviderProbeResult> respond) => _respond = respond;

        public Task<ProviderProbeResult> ProbeAsync(
            string providerType, string? endpoint, string? apiKey, CancellationToken ct = default)
            => throw new NotSupportedException("Catalog uses the entry overload.");

        public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
        {
            ProbeCount++;
            return Task.FromResult(_respond(entry));
        }

        public Task<ProviderProbeResult> ProbeAsync(
            string providerType, string? endpoint, string? credential,
            AuthMethod authMethod, CancellationToken ct = default)
            => throw new NotSupportedException("Catalog uses the entry overload.");
    }

    private sealed class FakeEnricher : IModelCapabilityResolver
    {
        private readonly Func<string, ResolvedModelCapabilities?> _respond;

        public FakeEnricher(Func<string, ResolvedModelCapabilities?> respond) => _respond = respond;

        public Task<ResolvedModelCapabilities?> ResolveAsync(string modelId, CancellationToken ct = default)
            => Task.FromResult(_respond(modelId));
    }

    private static DiscoveredModel Model(string id) => new() { ModelId = new ModelId(id) };

    private static ModelCatalogService Create(
        Dictionary<string, ProviderEntry> providers,
        IProviderProbe probe,
        Func<ProviderEntry, IModelCapabilityResolver?>? enricherFactory = null,
        TimeProvider? timeProvider = null)
        => new(
            providers,
            probe,
            enricherFactory ?? (_ => null),
            timeProvider ?? new FakeTimeProvider(),
            NullLogger<ModelCatalogService>.Instance);

    [Fact]
    public async Task Dead_provider_reports_failed_and_does_not_empty_the_catalog()
    {
        var providers = new Dictionary<string, ProviderEntry>
        {
            ["local-ollama"] = new() { Type = "ollama" },
            ["cloud"] = new() { Type = "openrouter" }
        };
        var probe = new FakeProbe(entry => entry.Type == "ollama"
            ? new ProviderProbeResult(true, null, [Model("qwen3:30b"), Model("llama4:8b")])
            : new ProviderProbeResult(false, "connection refused", []));

        var catalog = await Create(providers, probe)
            .GetCatalogAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, catalog.Providers.Count);
        var cloud = Assert.Single(catalog.Providers, p => p.ProviderKey == "cloud");
        Assert.False(cloud.Ok);
        Assert.Equal("connection refused", cloud.Error);
        Assert.Empty(cloud.Models);
        var ollama = Assert.Single(catalog.Providers, p => p.ProviderKey == "local-ollama");
        Assert.True(ollama.Ok);
        Assert.Equal(2, ollama.Models.Count);
    }

    [Fact]
    public async Task Cache_prevents_a_second_probe_inside_the_window()
    {
        var providers = new Dictionary<string, ProviderEntry> { ["p"] = new() { Type = "ollama" } };
        var probe = new FakeProbe(_ => new ProviderProbeResult(true, null, [Model("m1")]));
        var time = new FakeTimeProvider();
        var service = Create(providers, probe, timeProvider: time);

        await service.GetCatalogAsync(TestContext.Current.CancellationToken);
        await service.GetCatalogAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, probe.ProbeCount);

        time.Advance(TimeSpan.FromSeconds(31));
        await service.GetCatalogAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, probe.ProbeCount);
    }

    [Fact]
    public async Task Enrichment_maps_tri_state_tool_support()
    {
        var providers = new Dictionary<string, ProviderEntry> { ["p"] = new() { Type = "ollama" } };
        var probe = new FakeProbe(_ => new ProviderProbeResult(
            true, null, [Model("tools-yes"), Model("tools-no"), Model("tools-unknown")]));
        var enricher = new FakeEnricher(id => id switch
        {
            "tools-yes" => new ResolvedModelCapabilities(id, null, null, 32_768, SupportsToolCalls: true),
            "tools-no" => new ResolvedModelCapabilities(id, null, null, null, SupportsToolCalls: false),
            _ => null
        });

        var catalog = await Create(providers, probe, _ => enricher)
            .GetCatalogAsync(TestContext.Current.CancellationToken);

        var models = Assert.Single(catalog.Providers).Models;
        Assert.Equal(ModelToolSupport.Supported, Assert.Single(models, m => m.Id == "tools-yes").ToolSupport);
        Assert.Equal(ModelToolSupport.Unsupported, Assert.Single(models, m => m.Id == "tools-no").ToolSupport);
        Assert.Equal(ModelToolSupport.Unknown, Assert.Single(models, m => m.Id == "tools-unknown").ToolSupport);
        Assert.Equal(32_768, Assert.Single(models, m => m.Id == "tools-yes").ContextWindowTokens);
    }

    [Fact]
    public async Task No_enricher_leaves_tool_support_unknown()
    {
        var providers = new Dictionary<string, ProviderEntry> { ["p"] = new() { Type = "openrouter" } };
        var probe = new FakeProbe(_ => new ProviderProbeResult(true, null, [Model("m1")]));

        var catalog = await Create(providers, probe)
            .GetCatalogAsync(TestContext.Current.CancellationToken);

        var model = Assert.Single(Assert.Single(catalog.Providers).Models);
        Assert.Equal(ModelToolSupport.Unknown, model.ToolSupport);
    }

    [Fact]
    public async Task Validate_rejects_unconfigured_provider()
    {
        var providers = new Dictionary<string, ProviderEntry> { ["p"] = new() { Type = "ollama" } };
        var probe = new FakeProbe(_ => new ProviderProbeResult(true, null, [Model("m1")]));

        var reason = await Create(providers, probe)
            .ValidateSelectionAsync("ghost", "m1", TestContext.Current.CancellationToken);

        Assert.NotNull(reason);
        Assert.Contains("ghost", reason);
    }

    [Fact]
    public async Task Validate_rejects_model_absent_from_the_provider()
    {
        var providers = new Dictionary<string, ProviderEntry> { ["p"] = new() { Type = "ollama" } };
        var probe = new FakeProbe(_ => new ProviderProbeResult(true, null, [Model("m1")]));

        var reason = await Create(providers, probe)
            .ValidateSelectionAsync("p", "ghost-model", TestContext.Current.CancellationToken);

        Assert.NotNull(reason);
        Assert.Contains("ghost-model", reason);
    }

    [Fact]
    public async Task Validate_fails_closed_when_the_probe_failed()
    {
        var providers = new Dictionary<string, ProviderEntry> { ["p"] = new() { Type = "ollama" } };
        var probe = new FakeProbe(_ => new ProviderProbeResult(false, "unreachable", []));

        var reason = await Create(providers, probe)
            .ValidateSelectionAsync("p", "m1", TestContext.Current.CancellationToken);

        Assert.NotNull(reason);
        Assert.Contains("unreachable", reason);
    }

    [Fact]
    public async Task Validate_accepts_a_listed_model()
    {
        var providers = new Dictionary<string, ProviderEntry> { ["p"] = new() { Type = "ollama" } };
        var probe = new FakeProbe(_ => new ProviderProbeResult(true, null, [Model("m1")]));

        var reason = await Create(providers, probe)
            .ValidateSelectionAsync("p", "m1", TestContext.Current.CancellationToken);

        Assert.Null(reason);
    }
}
