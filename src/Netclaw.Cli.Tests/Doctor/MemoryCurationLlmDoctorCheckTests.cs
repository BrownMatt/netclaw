// -----------------------------------------------------------------------
// <copyright file="MemoryCurationLlmDoctorCheckTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Netclaw.Cli.Doctor;
using Netclaw.Configuration;
using Xunit;

namespace Netclaw.Cli.Tests.Doctor;

public sealed class MemoryCurationLlmDoctorCheckTests
{
    [Fact]
    public async Task ReadsDaemonLog_WhileWriterHoldsFileOpen()
    {
        var paths = CreateTempPaths();
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var logPath = Path.Combine(paths.LogsDirectory, "daemon-2026-08-29.log");

        // Hold the log open the way the daemon's file sink does: write
        // access, sharing only read. A reader that shares only read fails
        // against this handle on Windows ("file is being used by another
        // process"); the check must read with writer-tolerant sharing.
        // FileShare is advisory on Unix, so this regression only bites on
        // Windows — the assertion below still holds on both.
        await using (var writer = new FileStream(
            logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            var line = Encoding.UTF8.GetBytes(
                "info curation_llm_decision outcome=keep\n" +
                "info curation_llm_decision outcome=drop\n");
            await writer.WriteAsync(line, TestContext.Current.CancellationToken);
            await writer.FlushAsync(TestContext.Current.CancellationToken);

            var check = new MemoryCurationLlmDoctorCheck(paths, new FakeTimeProvider(now));
            var result = await check.RunAsync(TestContext.Current.CancellationToken);

            Assert.Equal(DoctorSeverity.Pass, result.Severity);
            Assert.Contains("2 decisions", result.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ReturnsWarning_WhenCurationLlmOnlyFails()
    {
        var paths = CreateTempPaths();
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var logPath = Path.Combine(paths.LogsDirectory, "daemon-2026-08-28.log");
        await File.WriteAllTextAsync(
            logPath,
            "warn curation_llm_timeout\nwarn curation_llm_no_decision\n",
            TestContext.Current.CancellationToken);

        var check = new MemoryCurationLlmDoctorCheck(paths, new FakeTimeProvider(now));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Warning, result.Severity);
        Assert.Contains("0 successful decisions", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IgnoresLogsOutsideTheWindow()
    {
        var paths = CreateTempPaths();
        var now = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        var stale = Path.Combine(paths.LogsDirectory, "daemon-2026-08-01.log");
        await File.WriteAllTextAsync(
            stale,
            "warn curation_llm_error\n",
            TestContext.Current.CancellationToken);

        var check = new MemoryCurationLlmDoctorCheck(paths, new FakeTimeProvider(now));
        var result = await check.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DoctorSeverity.Pass, result.Severity);
        Assert.Contains("No curation LLM activity", result.Message, StringComparison.Ordinal);
    }

    private static NetclawPaths CreateTempPaths()
    {
        var basePath = Path.Combine(Path.GetTempPath(), "netclaw-tests", Guid.NewGuid().ToString("N"));
        var paths = new NetclawPaths(basePath);
        paths.EnsureDirectoriesExist();
        return paths;
    }
}
