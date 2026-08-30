// -----------------------------------------------------------------------
// <copyright file="DaemonApiAttachmentTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Netclaw.Client;
using Netclaw.Configuration;
using Netclaw.Tests.Utilities;
using Xunit;

namespace Netclaw.Cli.Tests.Cli;

public sealed class DaemonApiAttachmentTests : IDisposable
{
    private readonly DisposableTempDir _dir = new();
    private readonly NetclawPaths _paths;

    public DaemonApiAttachmentTests()
    {
        _paths = new NetclawPaths(_dir.Path);
        _paths.EnsureDirectoriesExist();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("NETCLAW_DAEMON_ENDPOINT", null);
        _dir.Dispose();
    }

    private DaemonApi CreateDaemonApi(Func<HttpRequestMessage, HttpResponseMessage> handler)
    {
        ClientConfigFile.WriteEndpoint(_paths, "http://127.0.0.1:5199");
        return new DaemonApi(new FakeHttpClientFactory(handler), new ConfigurationBuilder().Build(), _paths);
    }

    [Fact]
    public async Task UploadSessionAttachment_posts_multipart_and_returns_result()
    {
        HttpRequestMessage? captured = null;
        string? capturedBody = null;
        var api = CreateDaemonApi(request =>
        {
            captured = request;
            capturedBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return FakeHttpMessageHandler.JsonResponse(new
            {
                attachmentId = "att-1",
                fileName = "notes.txt",
                relativePath = "inbox/notes.txt",
                mimeType = "text/plain",
                sizeBytes = 5L
            });
        });

        using var content = new MemoryStream(Encoding.UTF8.GetBytes("hello"));
        var result = await api.UploadSessionAttachmentAsync(
            "signalr/abc", "notes.txt", content, "text/plain", TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(HttpMethod.Post, captured!.Method);
        Assert.Contains("/api/sessions/attachments", captured.RequestUri!.AbsolutePath);
        Assert.Contains("sessionId=signalr%2Fabc", captured.RequestUri.Query);
        Assert.StartsWith("multipart/form-data", captured.Content!.Headers.ContentType!.MediaType);
        Assert.Contains("hello", capturedBody);

        Assert.Equal("att-1", result.AttachmentId);
        Assert.Equal("inbox/notes.txt", result.RelativePath);
    }

    [Fact]
    public async Task UploadSessionAttachment_surfaces_daemon_rejection_reason()
    {
        var api = CreateDaemonApi(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("\"'big.bin' exceeds the 8 byte per-file limit.\"")
        });

        using var content = new MemoryStream(Encoding.UTF8.GetBytes("way too big"));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(
            () => api.UploadSessionAttachmentAsync(
                "signalr/abc", "big.bin", content, null, TestContext.Current.CancellationToken));

        Assert.Contains("per-file limit", ex.Message);
        Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);
    }
}
