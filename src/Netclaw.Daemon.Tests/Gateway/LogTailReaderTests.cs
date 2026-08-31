// -----------------------------------------------------------------------
// <copyright file="LogTailReaderTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Gateway;
using Xunit;

namespace Netclaw.Daemon.Tests.Gateway;

/// <summary>
/// Tail reader contract: newest-lines window, cap, shared read against an
/// open writer, and the backward block scan across block boundaries.
/// </summary>
public sealed class LogTailReaderTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"netclaw-logtail-{Guid.NewGuid():N}");

    public LogTailReaderTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private string WriteLog(string name, IEnumerable<string> lines, bool trailingNewline = true)
    {
        var path = Path.Combine(_tempDir, name);
        var text = string.Join('\n', lines);
        File.WriteAllText(path, trailingNewline ? text + "\n" : text);
        return path;
    }

    [Fact]
    public async Task Tail_returns_the_last_lines_in_file_order()
    {
        var path = WriteLog("a.log", Enumerable.Range(1, 100).Select(i => $"line {i}"));

        var result = await LogTailReader.ReadTailAsync(path, 10, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("a.log", result.FileName);
        Assert.Equal(Enumerable.Range(91, 10).Select(i => $"line {i}"), result.Lines);
    }

    [Fact]
    public async Task Short_file_returns_every_line()
    {
        var path = WriteLog("short.log", ["one", "two", "three"]);

        var result = await LogTailReader.ReadTailAsync(path, 10, TestContext.Current.CancellationToken);

        Assert.Equal(["one", "two", "three"], result!.Lines);
    }

    [Fact]
    public async Task Torn_final_line_without_a_newline_still_counts()
    {
        var path = WriteLog("torn.log", ["complete", "partial"], trailingNewline: false);

        var result = await LogTailReader.ReadTailAsync(path, 2, TestContext.Current.CancellationToken);

        Assert.Equal(["complete", "partial"], result!.Lines);
    }

    [Fact]
    public async Task Window_spanning_multiple_scan_blocks_is_correct()
    {
        // ~150 KB of lines forces the backward scan across several 64 KB
        // blocks, exercising the block-boundary newline accounting.
        var padding = new string('x', 40);
        var path = WriteLog("big.log", Enumerable.Range(1, 3000).Select(i => $"line {i} {padding}"));

        var result = await LogTailReader.ReadTailAsync(path, 2000, TestContext.Current.CancellationToken);

        Assert.Equal(2000, result!.Lines.Count);
        Assert.Equal($"line 1001 {padding}", result.Lines[0]);
        Assert.Equal($"line 3000 {padding}", result.Lines[^1]);
    }

    [Fact]
    public async Task Read_succeeds_while_a_writer_holds_the_file_open()
    {
        var path = Path.Combine(_tempDir, "live.log");
        // Same open mode as RollingFileLoggerProvider and SessionLogActor:
        // StreamWriter append grants FileShare.Read only.
        await using var writer = new StreamWriter(path, append: true);
        await writer.WriteLineAsync("first");
        await writer.FlushAsync(TestContext.Current.CancellationToken);

        var result = await LogTailReader.ReadTailAsync(path, 10, TestContext.Current.CancellationToken);
        Assert.Equal(["first"], result!.Lines);

        // The writer keeps working after the shared read.
        await writer.WriteLineAsync("second");
        await writer.FlushAsync(TestContext.Current.CancellationToken);

        var again = await LogTailReader.ReadTailAsync(path, 10, TestContext.Current.CancellationToken);
        Assert.Equal(["first", "second"], again!.Lines);
    }

    [Fact]
    public async Task Empty_file_returns_no_lines()
    {
        var path = WriteLog("empty.log", []);
        File.WriteAllText(path, string.Empty);

        var result = await LogTailReader.ReadTailAsync(path, 10, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result.Lines);
    }

    [Fact]
    public async Task Missing_file_returns_null()
    {
        var result = await LogTailReader.ReadTailAsync(
            Path.Combine(_tempDir, "nope.log"), 10, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Theory]
    [InlineData(null, LogTailReader.DefaultTailLines)]
    [InlineData(0, LogTailReader.DefaultTailLines)]
    [InlineData(-5, LogTailReader.DefaultTailLines)]
    [InlineData(50, 50)]
    [InlineData(2000, 2000)]
    [InlineData(5000, LogTailReader.MaxTailLines)]
    public void Clamp_bounds_the_requested_window(int? requested, int expected)
        => Assert.Equal(expected, LogTailReader.ClampTail(requested));
}
