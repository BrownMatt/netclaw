// -----------------------------------------------------------------------
// <copyright file="DiagnosticsPaneViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Netclaw.Client;
using Netclaw.Configuration;

namespace Netclaw.Gui.ViewModels;

/// <summary>The five right-pane views, one visible at a time.</summary>
public enum DiagnosticsView
{
    Logs,
    RunningModels,
    Status,
    Stats,
    Mcp
}

/// <summary>
/// The daemon calls the diagnostics pane makes, as delegates so viewmodel
/// tests substitute fakes without a live daemon (the
/// <see cref="SessionManagementActions"/> pattern).
/// </summary>
public sealed record DiagnosticsActions(
    Func<int?, Task<LogTailResultDto?>> DaemonLogTail,
    Func<string, int?, Task<LogTailResultDto?>> SessionLogTail,
    Func<Task<RunningModelsResponseDto?>> RunningModels,
    Func<Task<DaemonRuntimeStatus.Response?>> DaemonStatus,
    Func<Task<DaemonStats.Response?>> Stats,
    Func<Task<JsonElement>> McpStatuses)
{
    public static DiagnosticsActions None { get; } = new(
        _ => Task.FromResult<LogTailResultDto?>(null),
        (_, _) => Task.FromResult<LogTailResultDto?>(null),
        () => Task.FromResult<RunningModelsResponseDto?>(null),
        () => Task.FromResult<DaemonRuntimeStatus.Response?>(null),
        () => Task.FromResult<DaemonStats.Response?>(null),
        () => Task.FromResult(default(JsonElement)));
}

/// <summary>One row of the Running models view.</summary>
public sealed record RunningModelRow(
    string ProviderKey,
    string Id,
    string SizeDisplay,
    string ExpiresDisplay,
    bool IsActive);

/// <summary>One row of the MCP view.</summary>
public sealed record McpServerRow(
    string Name,
    string State,
    int ToolCount,
    string? Error);

/// <summary>
/// The collapsed-by-default right pane: Logs, Running models, Status, Stats,
/// and MCP, one view at a time. The view's timer calls
/// <see cref="OnRefreshTimerTick"/>; a collapsed pane ignores ticks, so it
/// costs no daemon requests. A failed refresh keeps the last loaded data and
/// surfaces the reason; the next tick retries.
/// </summary>
public sealed partial class DiagnosticsPaneViewModel : ObservableObject
{
    public const int RefreshIntervalSeconds = 5;
    private const int TailWindow = 2000;

    private readonly DiagnosticsActions _actions;
    private string? _attachedSessionId;
    private string? _overrideProvider;
    private string? _overrideModelId;
    private bool _refreshing;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowLogs))]
    [NotifyPropertyChangedFor(nameof(ShowRunningModels))]
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
    [NotifyPropertyChangedFor(nameof(ShowStats))]
    [NotifyPropertyChangedFor(nameof(ShowMcp))]
    private DiagnosticsView _selectedView = DiagnosticsView.Logs;

    public bool ShowLogs => SelectedView == DiagnosticsView.Logs;

    public bool ShowRunningModels => SelectedView == DiagnosticsView.RunningModels;

    public bool ShowStatus => SelectedView == DiagnosticsView.Status;

    public bool ShowStats => SelectedView == DiagnosticsView.Stats;

    public bool ShowMcp => SelectedView == DiagnosticsView.Mcp;

    // ── Logs view ─────────────────────────────────────────────────────

    [ObservableProperty]
    private bool _logSourceIsSession;

    [ObservableProperty]
    private bool _sessionLogAvailable;

    [ObservableProperty]
    private string _logFileName = string.Empty;

    [ObservableProperty]
    private string _logText = string.Empty;

    [ObservableProperty]
    private string? _logError;

    // ── Running models view ───────────────────────────────────────────

    public ObservableCollection<RunningModelRow> RunningModels { get; } = [];

    [ObservableProperty]
    private string? _runningModelsError;

    // ── Status / Stats views ──────────────────────────────────────────

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _statusError;

    [ObservableProperty]
    private string _statsText = string.Empty;

    [ObservableProperty]
    private string? _statsError;

    // ── MCP view ──────────────────────────────────────────────────────

    public ObservableCollection<McpServerRow> McpServers { get; } = [];

    [ObservableProperty]
    private string? _mcpError;

    public string RefreshIntervalDisplay => $"auto refresh {RefreshIntervalSeconds}s";

    public DiagnosticsPaneViewModel()
        : this(DiagnosticsActions.None)
    {
    }

    public DiagnosticsPaneViewModel(DiagnosticsActions actions) => _actions = actions;

    [RelayCommand]
    private Task ToggleExpandedAsync()
    {
        IsExpanded = !IsExpanded;
        return IsExpanded ? RefreshVisibleAsync() : Task.CompletedTask;
    }

    [RelayCommand]
    private Task SelectViewAsync(DiagnosticsView view)
    {
        SelectedView = view;
        return IsExpanded ? RefreshVisibleAsync() : Task.CompletedTask;
    }

    [RelayCommand]
    private Task ShowDaemonLogAsync()
    {
        LogSourceIsSession = false;
        return IsExpanded ? RefreshVisibleAsync() : Task.CompletedTask;
    }

    [RelayCommand]
    private Task ShowSessionLogAsync()
    {
        if (!SessionLogAvailable)
            return Task.CompletedTask;

        LogSourceIsSession = true;
        return IsExpanded ? RefreshVisibleAsync() : Task.CompletedTask;
    }

    /// <summary>
    /// The shell reports the attached session (null when none). The session
    /// log option follows it: it disables without a session, and a selected
    /// session source re-targets to the new session on the next refresh.
    /// </summary>
    public void SetAttachedSession(string? sessionId)
    {
        var changed = !string.Equals(_attachedSessionId, sessionId, StringComparison.Ordinal);
        _attachedSessionId = sessionId;
        SessionLogAvailable = sessionId is not null;
        if (sessionId is null)
            LogSourceIsSession = false;

        if (changed && IsExpanded && SelectedView == DiagnosticsView.Logs && LogSourceIsSession)
            _ = RefreshVisibleAsync();
    }

    /// <summary>
    /// The shell reports the session model override (both null = cleared,
    /// the session runs the configured main model).
    /// </summary>
    public void SetModelOverride(string? provider, string? modelId)
    {
        _overrideProvider = provider;
        _overrideModelId = modelId;
    }

    /// <summary>The view's timer calls this every second-or-so slice; a collapsed pane ignores it.</summary>
    public void OnRefreshTimerTick()
    {
        if (!IsExpanded || _refreshing)
            return;

        _ = RefreshVisibleAsync();
    }

    private async Task RefreshVisibleAsync()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        try
        {
            switch (SelectedView)
            {
                case DiagnosticsView.Logs:
                    await RefreshLogsAsync();
                    break;
                case DiagnosticsView.RunningModels:
                    await RefreshRunningModelsAsync();
                    break;
                case DiagnosticsView.Status:
                    await RefreshStatusAsync();
                    break;
                case DiagnosticsView.Stats:
                    await RefreshStatsAsync();
                    break;
                case DiagnosticsView.Mcp:
                    await RefreshMcpAsync();
                    break;
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    private async Task RefreshLogsAsync()
    {
        try
        {
            LogTailResultDto? tail;
            if (LogSourceIsSession && _attachedSessionId is { } sessionId)
            {
                tail = await _actions.SessionLogTail(sessionId, TailWindow);
                if (tail is null)
                {
                    LogError = "The session has no log yet.";
                    return;
                }
            }
            else
            {
                tail = await _actions.DaemonLogTail(TailWindow);
                if (tail is null)
                {
                    LogError = "No daemon log file found.";
                    return;
                }
            }

            LogFileName = tail.FileName;
            LogText = string.Join('\n', tail.Lines);
            LogError = null;
        }
        catch (Exception ex)
        {
            LogError = $"Refresh failed: {ex.Message}";
        }
    }

    private async Task RefreshRunningModelsAsync()
    {
        try
        {
            var response = await _actions.RunningModels();
            if (response is null)
            {
                RunningModelsError = "The daemon returned no running-models response.";
                return;
            }

            // The active mark falls back to the configured main model when no
            // override is set; the status endpoint is the only place the GUI
            // can learn it, so this view reads status as an input.
            var (activeProvider, activeModelId) = await ResolveActiveModelAsync();

            RunningModels.Clear();
            var failures = new List<string>();
            foreach (var provider in response.Providers)
            {
                if (!provider.Ok)
                {
                    failures.Add($"{provider.ProviderKey}: {provider.Error}");
                    continue;
                }

                foreach (var model in provider.Models)
                {
                    RunningModels.Add(new RunningModelRow(
                        provider.ProviderKey,
                        model.Id,
                        model.SizeBytes is { } bytes ? $"{bytes / 1073741824.0:F1} GB" : string.Empty,
                        model.ExpiresAt is { } expires ? $"expires {expires.LocalDateTime:HH:mm}" : string.Empty,
                        IsActiveModel(provider, model.Id, activeProvider, activeModelId)));
                }
            }

            RunningModelsError = failures.Count > 0 ? string.Join("; ", failures) : null;
        }
        catch (Exception ex)
        {
            RunningModelsError = $"Refresh failed: {ex.Message}";
        }
    }

    private async Task<(string? Provider, string? ModelId)> ResolveActiveModelAsync()
    {
        if (_overrideModelId is not null)
            return (_overrideProvider, _overrideModelId);

        try
        {
            var status = await _actions.DaemonStatus();
            return status?.Model is { Degraded: false } model
                ? (model.Provider, model.ModelId)
                : (null, null);
        }
        catch (Exception)
        {
            // No mark is better than a failed running-models view.
            return (null, null);
        }
    }

    private static bool IsActiveModel(
        RunningModelsProviderDto provider, string modelId, string? activeProvider, string? activeModelId)
    {
        if (activeModelId is null || !ModelIdsMatch(modelId, activeModelId))
            return false;

        // The active provider name may be the config key or the provider
        // type, depending on the source; accept either.
        return activeProvider is null
            || string.Equals(provider.ProviderKey, activeProvider, StringComparison.OrdinalIgnoreCase)
            || string.Equals(provider.Type, activeProvider, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ollama treats an untagged model name as ":latest": the configured model
    /// says "qwen3:8b-ish" while /api/ps reports "qwen3:8b-ish:latest"-style
    /// names, so the ids compare with the default tag stripped.
    /// </summary>
    private static bool ModelIdsMatch(string left, string right)
        => string.Equals(StripLatestTag(left), StripLatestTag(right), StringComparison.Ordinal);

    private static string StripLatestTag(string id)
        => id.EndsWith(":latest", StringComparison.Ordinal) ? id[..^":latest".Length] : id;

    private async Task RefreshStatusAsync()
    {
        try
        {
            var status = await _actions.DaemonStatus();
            if (status is null)
            {
                StatusError = "The daemon returned no status response.";
                return;
            }

            var text = new StringBuilder();
            text.AppendLine($"Overall: {status.Overall}");
            text.AppendLine($"Version: {status.Build.Version}");
            text.AppendLine($"Uptime: {FormatUptime(status.Process.UptimeSeconds)} (PID {status.Process.Pid})");
            text.AppendLine(status.Model is { } model
                ? model.Degraded
                    ? $"Model: DEGRADED - {model.DegradedReason}"
                    : $"Model: {model.Provider}/{model.ModelId} (ctx {model.ContextWindow})"
                : "Model: unknown");
            text.AppendLine($"Persistence: {status.Persistence.Provider}");
            if (status.Memory is { } memory)
                text.AppendLine($"Memory: {memory.Provider} ({memory.Status})");
            foreach (var connector in status.Connectors)
                text.AppendLine($"Connector {connector.DisplayName}: {connector.Status}{(connector.Message is null ? string.Empty : $" - {connector.Message}")}");
            if (status.Update is { Available: true } update)
                text.AppendLine($"Update available: {update.LatestVersion}");

            StatusText = text.ToString().TrimEnd();
            StatusError = null;
        }
        catch (Exception ex)
        {
            StatusError = $"Refresh failed: {ex.Message}";
        }
    }

    private async Task RefreshStatsAsync()
    {
        try
        {
            var stats = await _actions.Stats();
            if (stats is null)
            {
                StatsError = "The daemon returned no stats response.";
                return;
            }

            var text = new StringBuilder();
            text.AppendLine($"Uptime: {FormatUptime(stats.Process.UptimeSeconds)}");
            text.AppendLine($"Tokens: in {stats.Tokens.InputTokensTotal:N0} / out {stats.Tokens.OutputTokensTotal:N0}");
            text.AppendLine($"Turns: {stats.Tokens.TurnsCompletedTotal:N0}");
            text.AppendLine($"Sessions: {stats.Sessions.TotalSessions} total, {stats.Sessions.ActiveSessions} active");
            text.AppendLine($"Memory: {stats.Memory.DocumentCount} documents, {stats.Memory.AnchorCount} anchors ({stats.Memory.Status})");
            text.AppendLine($"Skills: {stats.Skills.TotalAvailable} available, {stats.Tokens.SkillsLoadedTotal:N0} loads");
            text.AppendLine($"Webhooks: {stats.Webhooks.EnabledRoutes}/{stats.Webhooks.TotalRoutes} routes enabled");

            StatsText = text.ToString().TrimEnd();
            StatsError = null;
        }
        catch (Exception ex)
        {
            StatsError = $"Refresh failed: {ex.Message}";
        }
    }

    private async Task RefreshMcpAsync()
    {
        try
        {
            var statuses = await _actions.McpStatuses();
            McpServers.Clear();
            if (statuses.ValueKind == JsonValueKind.Object)
            {
                foreach (var server in statuses.EnumerateObject())
                {
                    var state = server.Value.TryGetProperty("state", out var stateProp)
                        ? stateProp.GetString() ?? "unknown"
                        : "unknown";
                    var toolCount = server.Value.TryGetProperty("toolCount", out var countProp)
                                    && countProp.ValueKind == JsonValueKind.Number
                        ? countProp.GetInt32()
                        : 0;
                    var error = server.Value.TryGetProperty("errorMessage", out var errorProp)
                        ? errorProp.GetString()
                        : null;
                    McpServers.Add(new McpServerRow(server.Name, state, toolCount, error));
                }
            }

            McpError = null;
        }
        catch (Exception ex)
        {
            McpError = $"Refresh failed: {ex.Message}";
        }
    }

    private static string FormatUptime(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        return span.TotalDays >= 1
            ? $"{(int)span.TotalDays}d {span.Hours}h"
            : span.TotalHours >= 1
                ? $"{(int)span.TotalHours}h {span.Minutes}m"
                : $"{span.Minutes}m";
    }
}
