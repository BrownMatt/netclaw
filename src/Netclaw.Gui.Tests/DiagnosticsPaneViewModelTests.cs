// -----------------------------------------------------------------------
// <copyright file="DiagnosticsPaneViewModelTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Client;
using Netclaw.Configuration;
using Netclaw.Gui.ViewModels;
using Xunit;

namespace Netclaw.Gui.Tests;

/// <summary>
/// Right-pane contract: a collapsed pane costs nothing, only the visible
/// view refreshes, a failed refresh keeps the last data, the session log
/// follows the attached session, and the running-models view marks the
/// active model.
/// </summary>
public sealed class DiagnosticsPaneViewModelTests
{
    private readonly FakeDiagnostics _fake = new();

    private DiagnosticsPaneViewModel CreateViewModel() => new(_fake.Actions);

    [Fact]
    public void Collapsed_pane_ignores_ticks_and_sends_nothing()
    {
        var vm = CreateViewModel();

        Assert.False(vm.IsExpanded);
        vm.OnRefreshTimerTick();
        vm.OnRefreshTimerTick();

        Assert.Equal(0, _fake.TotalCalls);
    }

    [Fact]
    public async Task Expanding_refreshes_the_selected_view_immediately()
    {
        var vm = CreateViewModel();

        await vm.ToggleExpandedCommand.ExecuteAsync(null);

        Assert.True(vm.IsExpanded);
        Assert.Equal(1, _fake.DaemonLogCalls);
        Assert.Equal("daemon-2026-08-31.log", vm.LogFileName);
        Assert.Equal("line one\nline two", vm.LogText);
        Assert.Null(vm.LogError);
    }

    [Fact]
    public async Task Tick_refreshes_only_the_visible_view()
    {
        var vm = CreateViewModel();
        await vm.ToggleExpandedCommand.ExecuteAsync(null);
        await vm.SelectViewCommand.ExecuteAsync(DiagnosticsView.Status);
        _fake.ResetCounters();

        vm.OnRefreshTimerTick();
        await _fake.WaitForIdleAsync();

        Assert.Equal(1, _fake.StatusCalls);
        Assert.Equal(0, _fake.DaemonLogCalls);
        Assert.Equal(0, _fake.RunningModelsCalls);
        Assert.Equal(0, _fake.StatsCalls);
        Assert.Equal(0, _fake.McpCalls);
    }

    [Fact]
    public async Task Collapsing_stops_refreshes()
    {
        var vm = CreateViewModel();
        await vm.ToggleExpandedCommand.ExecuteAsync(null);
        await vm.ToggleExpandedCommand.ExecuteAsync(null);
        _fake.ResetCounters();

        vm.OnRefreshTimerTick();

        Assert.False(vm.IsExpanded);
        Assert.Equal(0, _fake.TotalCalls);
    }

    [Fact]
    public async Task Failed_refresh_keeps_the_last_data_and_the_next_tick_retries()
    {
        var vm = CreateViewModel();
        await vm.ToggleExpandedCommand.ExecuteAsync(null);
        Assert.Equal("line one\nline two", vm.LogText);

        _fake.DaemonLogFailure = new InvalidOperationException("daemon unreachable");
        vm.OnRefreshTimerTick();
        await _fake.WaitForIdleAsync();

        // Data survives the failure; the reason is visible.
        Assert.Equal("line one\nline two", vm.LogText);
        Assert.Contains("daemon unreachable", vm.LogError);

        // The next tick retries and recovers.
        _fake.DaemonLogFailure = null;
        _fake.DaemonLogLines = ["fresh line"];
        vm.OnRefreshTimerTick();
        await _fake.WaitForIdleAsync();

        Assert.Equal("fresh line", vm.LogText);
        Assert.Null(vm.LogError);
    }

    [Fact]
    public async Task Session_log_source_needs_an_attached_session_and_follows_it()
    {
        var vm = CreateViewModel();
        await vm.ToggleExpandedCommand.ExecuteAsync(null);

        // No session attached: the session source is refused.
        await vm.ShowSessionLogCommand.ExecuteAsync(null);
        Assert.False(vm.LogSourceIsSession);

        vm.SetAttachedSession("signalr/first");
        await vm.ShowSessionLogCommand.ExecuteAsync(null);
        Assert.True(vm.LogSourceIsSession);
        Assert.Equal("signalr/first", _fake.LastSessionLogId);

        // Attaching a different session re-targets the view.
        vm.SetAttachedSession("signalr/second");
        await _fake.WaitForIdleAsync();
        Assert.Equal("signalr/second", _fake.LastSessionLogId);

        // Losing the session falls back to the daemon log.
        vm.SetAttachedSession(null);
        Assert.False(vm.LogSourceIsSession);
        Assert.False(vm.SessionLogAvailable);
    }

    [Fact]
    public async Task Running_models_marks_the_override_model_active()
    {
        _fake.Running = Running(
            ("my-ollama", Ok: true, null, [("qwen3:8b", 1073741824L), ("other:1b", null)]));
        var vm = CreateViewModel();
        vm.SetModelOverride("my-ollama", "qwen3:8b");

        await vm.ToggleExpandedCommand.ExecuteAsync(null);
        await vm.SelectViewCommand.ExecuteAsync(DiagnosticsView.RunningModels);

        Assert.Equal(2, vm.RunningModels.Count);
        Assert.True(vm.RunningModels[0].IsActive);
        Assert.False(vm.RunningModels[1].IsActive);
        Assert.Equal("1.0 GB", vm.RunningModels[0].SizeDisplay);
    }

    [Fact]
    public async Task Running_models_falls_back_to_the_configured_model_for_the_mark()
    {
        _fake.Running = Running(("my-ollama", Ok: true, null, [("qwen3:8b", null)]));
        _fake.Status = StatusWithModel("ollama", "qwen3:8b");
        var vm = CreateViewModel();

        await vm.ToggleExpandedCommand.ExecuteAsync(null);
        await vm.SelectViewCommand.ExecuteAsync(DiagnosticsView.RunningModels);

        Assert.True(Assert.Single(vm.RunningModels).IsActive);
    }

    [Fact]
    public async Task Untagged_configured_id_matches_the_latest_tagged_loaded_model()
    {
        // Ollama reports "name:latest" in /api/ps while the configured model
        // omits the default tag; the mark must still land.
        _fake.Running = Running(("my-ollama", Ok: true, null, [("qwen3.6-35b-256k:latest", null)]));
        _fake.Status = StatusWithModel("ollama", "qwen3.6-35b-256k");
        var vm = CreateViewModel();

        await vm.ToggleExpandedCommand.ExecuteAsync(null);
        await vm.SelectViewCommand.ExecuteAsync(DiagnosticsView.RunningModels);

        Assert.True(Assert.Single(vm.RunningModels).IsActive);
    }

    [Fact]
    public async Task Failed_provider_surfaces_while_good_rows_render()
    {
        _fake.Running = Running(
            ("my-ollama", Ok: true, null, [("qwen3:8b", null)]),
            ("dead-ollama", Ok: false, "Connection failed: refused", []));
        var vm = CreateViewModel();

        await vm.ToggleExpandedCommand.ExecuteAsync(null);
        await vm.SelectViewCommand.ExecuteAsync(DiagnosticsView.RunningModels);

        Assert.Single(vm.RunningModels);
        Assert.Contains("dead-ollama", vm.RunningModelsError);
        Assert.Contains("refused", vm.RunningModelsError);
    }

    [Fact]
    public async Task Mcp_view_lists_server_statuses()
    {
        _fake.Mcp = JsonDocument.Parse(
            """{"unifi":{"state":"Connected","toolCount":12,"errorMessage":null},"dead":{"state":"Failed","toolCount":0,"errorMessage":"spawn failed"}}""").RootElement;
        var vm = CreateViewModel();

        await vm.ToggleExpandedCommand.ExecuteAsync(null);
        await vm.SelectViewCommand.ExecuteAsync(DiagnosticsView.Mcp);

        Assert.Equal(2, vm.McpServers.Count);
        Assert.Equal(new McpServerRow("unifi", "Connected", 12, null), vm.McpServers[0]);
        Assert.Equal(new McpServerRow("dead", "Failed", 0, "spawn failed"), vm.McpServers[1]);
    }

    [Fact]
    public async Task Stats_view_renders_the_daemon_numbers()
    {
        var vm = CreateViewModel();

        await vm.ToggleExpandedCommand.ExecuteAsync(null);
        await vm.SelectViewCommand.ExecuteAsync(DiagnosticsView.Stats);

        Assert.Contains("Tokens: in", vm.StatsText);
        Assert.Contains("2 total, 1 active", vm.StatsText);
        Assert.Null(vm.StatsError);
    }

    private static RunningModelsResponseDto Running(
        params (string Key, bool Ok, string? Error, (string Id, long? Size)[] Models)[] providers)
        => new([.. providers.Select(p => new RunningModelsProviderDto(
            p.Key, "ollama", p.Ok, p.Error,
            [.. p.Models.Select(m => new RunningModelEntryDto(m.Id, m.Size, null))]))]);

    private static DaemonRuntimeStatus.Response StatusWithModel(string provider, string modelId) => new()
    {
        Overall = "healthy",
        Build = new DaemonRuntimeStatus.Build { Version = "1.0", CommitHash = "abc", BuildTimestamp = "now" },
        Process = new DaemonRuntimeStatus.Process { Pid = 1, UptimeSeconds = 60 },
        Connectors = [],
        Persistence = new DaemonRuntimeStatus.Persistence { Provider = "sqlite" },
        Telemetry = new DaemonRuntimeStatus.Telemetry { Enabled = false },
        Model = new DaemonRuntimeStatus.Model
        {
            ModelId = modelId,
            Provider = provider,
            InputModalities = "text",
            OutputModalities = "text",
            ContextWindow = 8192
        }
    };

    private sealed class FakeDiagnostics
    {
        private int _inFlight;

        public int DaemonLogCalls;
        public int SessionLogCalls;
        public int RunningModelsCalls;
        public int StatusCalls;
        public int StatsCalls;
        public int McpCalls;

        public string[] DaemonLogLines = ["line one", "line two"];
        public Exception? DaemonLogFailure;
        public string? LastSessionLogId;
        public RunningModelsResponseDto? Running;
        public DaemonRuntimeStatus.Response? Status;
        public JsonElement Mcp = JsonDocument.Parse("{}").RootElement;

        public int TotalCalls =>
            DaemonLogCalls + SessionLogCalls + RunningModelsCalls + StatusCalls + StatsCalls + McpCalls;

        public DiagnosticsActions Actions => new(
            tail => Track(() =>
            {
                DaemonLogCalls++;
                if (DaemonLogFailure is { } failure)
                    throw failure;
                return (LogTailResultDto?)new LogTailResultDto("daemon-2026-08-31.log", DaemonLogLines);
            }),
            (sessionId, tail) => Track(() =>
            {
                SessionLogCalls++;
                LastSessionLogId = sessionId;
                return (LogTailResultDto?)new LogTailResultDto("session.log", ["session line"]);
            }),
            () => Track(() =>
            {
                RunningModelsCalls++;
                return Running;
            }),
            () => Track(() =>
            {
                StatusCalls++;
                return Status;
            }),
            () => Track(() =>
            {
                StatsCalls++;
                return (DaemonStats.Response?)new DaemonStats.Response
                {
                    Process = new DaemonStats.Process { UptimeSeconds = 90 },
                    Tokens = new DaemonStats.Tokens { InputTokensTotal = 1000, OutputTokensTotal = 500, TurnsCompletedTotal = 7 },
                    Sessions = new DaemonStats.Sessions { TotalSessions = 2, ActiveSessions = 1, TotalTurns = 7 },
                    Memory = new DaemonStats.Memory { Status = "ok", DocumentCount = 3 },
                    Skills = new DaemonStats.Skills { TotalAvailable = 40 },
                    Webhooks = new DaemonStats.Webhooks { TotalRoutes = 1, EnabledRoutes = 1 }
                };
            }),
            () => Track(() =>
            {
                McpCalls++;
                return Mcp;
            }));

        public void ResetCounters()
        {
            DaemonLogCalls = 0;
            SessionLogCalls = 0;
            RunningModelsCalls = 0;
            StatusCalls = 0;
            StatsCalls = 0;
            McpCalls = 0;
        }

        /// <summary>
        /// The fakes complete synchronously, so in-flight work drains after a
        /// few yields — no timers, no sleeps.
        /// </summary>
        public async Task WaitForIdleAsync()
        {
            for (var i = 0; i < 20; i++)
                await Task.Yield();
            Assert.Equal(0, _inFlight);
        }

        private Task<T> Track<T>(Func<T> body)
        {
            _inFlight++;
            try
            {
                return Task.FromResult(body());
            }
            finally
            {
                _inFlight--;
            }
        }
    }
}
