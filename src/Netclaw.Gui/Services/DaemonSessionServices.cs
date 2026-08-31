// -----------------------------------------------------------------------
// <copyright file="DaemonSessionServices.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Client;
using R3;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Gui.Services;

/// <summary>
/// The daemon operations the GUI viewmodels invoke. An interface so
/// viewmodel tests substitute a fake without a live daemon; the production
/// implementation wraps <see cref="DaemonClient"/> and <see cref="DaemonApi"/>.
/// </summary>
public interface IDaemonSessionService
{
    bool IsConnected { get; }

    Observable<SessionOutput> SessionOutput { get; }

    Observable<DaemonConnectionEvent> ConnectionEvents { get; }

    Task ConnectAsync(CancellationToken cancellationToken = default);

    Task<string> EnsureSessionAsync(CancellationToken cancellationToken = default);

    Task<string> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task SendAsync(string text, CancellationToken cancellationToken = default);

    Task RespondToInteractionAsync(string callId, string selectedKey, CancellationToken cancellationToken = default);

    Task AddFolderGrantAsync(string path, CancellationToken cancellationToken = default);

    Task RemoveFolderGrantAsync(string path, CancellationToken cancellationToken = default);

    Task<List<SessionCatalogEntryDto>> ListSessionsAsync(CancellationToken cancellationToken = default);

    Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken = default);

    Task SetSessionFlagsAsync(string sessionId, bool? pinned = null, bool? archived = null, CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<ModelCatalogResponseDto?> GetModelCatalogAsync(CancellationToken cancellationToken = default);

    Task SetSessionModelAsync(string provider, string modelId, CancellationToken cancellationToken = default);

    Task ClearSessionModelAsync(CancellationToken cancellationToken = default);

    Task<DaemonApi.SessionAttachmentUploadResultDto> UploadAttachmentAsync(
        string sessionId,
        string fileName,
        Stream content,
        string? contentType,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Production implementation over the shared client library.
/// </summary>
public sealed class DaemonSessionService : IDaemonSessionService
{
    private readonly DaemonClient _client;
    private readonly DaemonApi _api;

    public DaemonSessionService(DaemonClient client, DaemonApi api)
    {
        _client = client;
        _api = api;
    }

    public bool IsConnected => _client.IsConnected;

    public Observable<SessionOutput> SessionOutput => _client.SessionOutput;

    public Observable<DaemonConnectionEvent> ConnectionEvents => _client.ConnectionEvents;

    public Task ConnectAsync(CancellationToken cancellationToken = default)
        => _client.ConnectAsync(cancellationToken);

    public Task<string> EnsureSessionAsync(CancellationToken cancellationToken = default)
        => _client.EnsureSessionAsync(DaemonClient.TuiChannelType, cancellationToken);

    public Task<string> ResumeSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _client.ResumeSessionAsync(sessionId, DaemonClient.TuiChannelType, cancellationToken);

    public Task SendAsync(string text, CancellationToken cancellationToken = default)
        => _client.SendAsync(text, cancellationToken);

    public Task RespondToInteractionAsync(string callId, string selectedKey, CancellationToken cancellationToken = default)
        => _client.RespondToInteractionAsync(callId, selectedKey, cancellationToken);

    public Task AddFolderGrantAsync(string path, CancellationToken cancellationToken = default)
        => _client.AddFolderGrantAsync(path, cancellationToken);

    public Task RemoveFolderGrantAsync(string path, CancellationToken cancellationToken = default)
        => _client.RemoveFolderGrantAsync(path, cancellationToken);

    public Task<List<SessionCatalogEntryDto>> ListSessionsAsync(CancellationToken cancellationToken = default)
        => _api.ListSessionsAsync(limit: 100, ct: cancellationToken);

    public Task RenameSessionAsync(string sessionId, string title, CancellationToken cancellationToken = default)
        => _api.RenameSessionAsync(sessionId, title, cancellationToken);

    public Task SetSessionFlagsAsync(string sessionId, bool? pinned = null, bool? archived = null, CancellationToken cancellationToken = default)
        => _api.SetSessionFlagsAsync(sessionId, pinned, archived, cancellationToken);

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _api.DeleteSessionAsync(sessionId, cancellationToken);

    public Task<ModelCatalogResponseDto?> GetModelCatalogAsync(CancellationToken cancellationToken = default)
        => _api.GetModelsAsync(cancellationToken);

    public Task SetSessionModelAsync(string provider, string modelId, CancellationToken cancellationToken = default)
        => _client.SetSessionModelAsync(provider, modelId, cancellationToken);

    public Task ClearSessionModelAsync(CancellationToken cancellationToken = default)
        => _client.ClearSessionModelAsync(cancellationToken);

    public Task<DaemonApi.SessionAttachmentUploadResultDto> UploadAttachmentAsync(
        string sessionId,
        string fileName,
        Stream content,
        string? contentType,
        CancellationToken cancellationToken = default)
        => _api.UploadSessionAttachmentAsync(sessionId, fileName, content, contentType, cancellationToken);
}
