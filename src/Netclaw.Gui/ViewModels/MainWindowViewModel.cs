// -----------------------------------------------------------------------
// <copyright file="MainWindowViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netclaw.Client;
using Netclaw.Configuration;
using R3;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Gui.ViewModels;

/// <summary>
/// Walking-skeleton view model: connect to the daemon, ensure one TUI-channel
/// session, send text, and render the streamed reply. Product UI lands in
/// Phase 1 of the GUI plan; this class only proves the transport path.
/// </summary>
public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan ConnectRetryDelay = TimeSpan.FromSeconds(3);

    private readonly DaemonClient _client;
    private readonly string _endpoint;
    private readonly Queue<string> _pending = new();
    private readonly StringBuilder _transcript = new();
    private readonly IDisposable _outputSubscription;
    private readonly IDisposable _connectionSubscription;

    // Start index of the streaming assistant turn inside _transcript. The final
    // TextOutput snapshot replaces the accumulated deltas from this index.
    private int _turnStart = -1;
    private bool _sessionEnsured;

    [ObservableProperty]
    private string _status = "Connecting...";

    [ObservableProperty]
    private string _outputText = string.Empty;

    [ObservableProperty]
    private string _inputText = string.Empty;

    public MainWindowViewModel()
    {
        var paths = new NetclawPaths();
        _endpoint = DaemonApi.ResolveEndpoint(paths);
        _client = DaemonClientFactory.Create(_endpoint, paths);

        _outputSubscription = _client.SessionOutput
            .Subscribe(this, static (output, self) =>
                Dispatcher.UIThread.Post(() => self.HandleOutput(output)));
        _connectionSubscription = _client.ConnectionEvents
            .Subscribe(this, static (evt, self) =>
                Dispatcher.UIThread.Post(() => self.HandleConnectionEvent(evt)));

        _ = ConnectLoopAsync();
    }

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0)
            return;

        InputText = string.Empty;
        AppendLine($"you> {text}");

        if (_client.IsConnected && _sessionEnsured)
        {
            await DispatchAsync(text);
            return;
        }

        _pending.Enqueue(text);
        Status = $"Queued ({_pending.Count}) - waiting for daemon at {_endpoint}";
    }

    public void Dispose()
    {
        _outputSubscription.Dispose();
        _connectionSubscription.Dispose();
        _ = _client.DisposeAsync();
    }

    private async Task ConnectLoopAsync()
    {
        while (true)
        {
            try
            {
                await _client.ConnectAsync();
                await EnsureSessionAndFlushAsync();
                return;
            }
            catch (Exception ex)
            {
                // Terminal connect failures (daemon down past the retry budget,
                // 401 with its `netclaw pair` instruction) all surface here.
                // Keep retrying: the daemon can start, and pairing can be fixed,
                // while the window stays open.
                SetStatusThreadSafe($"Disconnected - retrying {_endpoint} ({ex.Message})");
                await Task.Delay(ConnectRetryDelay);
            }
        }
    }

    private async Task EnsureSessionAndFlushAsync()
    {
        await _client.EnsureSessionAsync(DaemonClient.TuiChannelType);
        _sessionEnsured = true;
        SetStatusThreadSafe($"Connected - {_endpoint}");

        while (true)
        {
            string? next = null;
            Dispatcher.UIThread.Invoke(() =>
            {
                if (_pending.Count > 0)
                    next = _pending.Dequeue();
            });
            if (next is null)
                return;

            await DispatchAsync(next);
        }
    }

    private async Task DispatchAsync(string text)
    {
        try
        {
            await _client.SendAsync(text);
        }
        catch (Exception ex)
        {
            SetStatusThreadSafe($"Send failed: {ex.Message}");
            Dispatcher.UIThread.Invoke(() => _pending.Enqueue(text));
        }
    }

    private void HandleOutput(SessionOutput output)
    {
        switch (output)
        {
            case TextDeltaOutput delta:
                if (_turnStart < 0)
                    _turnStart = _transcript.Length;
                _transcript.Append(delta.Delta);
                RefreshOutput();
                break;

            case TextOutput text:
                // Authoritative snapshot: replace the accumulated deltas.
                if (_turnStart >= 0)
                    _transcript.Length = _turnStart;
                _turnStart = -1;
                AppendLine(text.Text);
                break;

            case TurnCompleted:
                _turnStart = -1;
                AppendLine(string.Empty);
                break;

            case ErrorOutput error:
                _turnStart = -1;
                AppendLine($"[error] {error.Message}");
                Status = $"Error - {error.Message}";
                break;

            default:
                break;
        }
    }

    private void HandleConnectionEvent(DaemonConnectionEvent evt)
    {
        Status = evt.State switch
        {
            DaemonConnectionState.Connected => $"Connected - {_endpoint}",
            DaemonConnectionState.Connecting => $"Connecting to {_endpoint}...",
            DaemonConnectionState.Reconnecting or DaemonConnectionState.TransportClosed
                => $"Reconnecting to {_endpoint}...",
            DaemonConnectionState.Disconnected => $"Disconnected - {evt.Message}",
            _ => evt.Message,
        };

        if (evt.State == DaemonConnectionState.Connected && _sessionEnsured && _pending.Count > 0)
            _ = EnsureSessionAndFlushAsync();
    }

    private void AppendLine(string line)
    {
        _transcript.AppendLine(line);
        RefreshOutput();
    }

    private void RefreshOutput()
        => OutputText = _transcript.ToString();

    private void SetStatusThreadSafe(string status)
        => Dispatcher.UIThread.Post(() => Status = status);
}
