// -----------------------------------------------------------------------
// <copyright file="OllamaDescriptorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OllamaDescriptorTests
{
    [Fact]
    public async Task Probe_FiltersEmbeddingOnlyModels()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            models = new object[]
            {
                new { name = "all-minilm:latest", capabilities = new[] { "embedding" } },
                new { name = "qwen2:0.5b", capabilities = new[] { "completion" } },
                new { name = "future-chat:latest", capabilities = new[] { "chat" } },
                new { name = "legacy-metadata:latest" },
            },
        }));
        var descriptor = new OllamaDescriptor(new HttpClient(handler));

        var result = await descriptor.ProbeAsync(new ProviderEntry
        {
            Type = "ollama",
            Endpoint = "http://ollama.test",
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        var ids = result.Models.Select(model => model.ModelId.Value).ToList();
        Assert.Equal(["qwen2:0.5b", "future-chat:latest", "legacy-metadata:latest"], ids);
    }

    [Fact]
    public async Task ProbeRunning_MapsNameSizeAndExpiry_AbsentFieldsStayAbsent()
    {
        var handler = new FakeHttpMessageHandler(_ => FakeHttpMessageHandler.JsonResponse(new
        {
            models = new object[]
            {
                new { name = "qwen3:8b", size = 6654289920L, expires_at = "2026-08-31T12:34:56Z" },
                new { name = "bare-model:latest" },
            },
        }));
        var descriptor = new OllamaDescriptor(new HttpClient(handler));

        var result = await descriptor.ProbeRunningAsync(new ProviderEntry
        {
            Type = "ollama",
            Endpoint = "http://ollama.test",
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Equal(2, result.Models.Count);
        Assert.Equal("qwen3:8b", result.Models[0].ModelId);
        Assert.Equal(6654289920L, result.Models[0].SizeBytes);
        Assert.Equal(DateTimeOffset.Parse("2026-08-31T12:34:56Z"), result.Models[0].ExpiresAt);
        Assert.Null(result.Models[1].SizeBytes);
        Assert.Null(result.Models[1].ExpiresAt);
    }

    [Fact]
    public async Task ProbeRunning_UnreachableBackend_FailsWithReason()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            throw new HttpRequestException("connection refused"));
        var descriptor = new OllamaDescriptor(new HttpClient(handler));

        var result = await descriptor.ProbeRunningAsync(new ProviderEntry
        {
            Type = "ollama",
            Endpoint = "http://ollama.test",
        }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("Connection failed", result.ErrorMessage);
        Assert.Empty(result.Models);
    }

    [Fact]
    public async Task ProbeRunning_ErrorStatus_FailsWithStatus()
    {
        var handler = new FakeHttpMessageHandler(_ =>
            new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));
        var descriptor = new OllamaDescriptor(new HttpClient(handler));

        var result = await descriptor.ProbeRunningAsync(new ProviderEntry
        {
            Type = "ollama",
            Endpoint = "http://ollama.test",
        }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("500", result.ErrorMessage);
    }
}
