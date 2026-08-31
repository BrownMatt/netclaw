// -----------------------------------------------------------------------
// <copyright file="LogTailReader.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text;

namespace Netclaw.Daemon.Gateway;

/// <summary>The tail window of one log file: the file's name and its last lines.</summary>
public sealed record LogTailResult(string FileName, IReadOnlyList<string> Lines);

/// <summary>
/// Reads the last N lines of a log file the daemon holds open for writes.
/// The open mode grants <c>FileShare.ReadWrite | Delete</c> — the write and
/// roll rights the daemon's own writers already hold — so a tail read never
/// blocks or breaks logging. The scan walks backward in fixed blocks from the
/// end, so cost is bounded by the requested window, not the file size.
/// </summary>
public static class LogTailReader
{
    public const int MaxTailLines = 2000;
    public const int DefaultTailLines = 500;

    private const int BlockSize = 64 * 1024;

    /// <summary>Clamps a client-requested line count to [1, MaxTailLines]; null or non-positive falls to the default.</summary>
    public static int ClampTail(int? requested) => requested is { } n and > 0
        ? Math.Min(n, MaxTailLines)
        : DefaultTailLines;

    /// <summary>Returns the tail window, or null when the file does not exist.</summary>
    public static async Task<LogTailResult?> ReadTailAsync(string path, int lineCount, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;

        FileStream stream;
        try
        {
            stream = new FileStream(
                path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BlockSize, FileOptions.Asynchronous);
        }
        catch (FileNotFoundException)
        {
            // Deleted between the existence check and the open (a roll or a
            // session teardown); same contract as never existing.
            return null;
        }

        await using (stream)
        {
            var length = stream.Length;
            var start = await FindWindowStartAsync(stream, length, lineCount, ct);

            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[length - start];
            var read = 0;
            while (read < buffer.Length)
            {
                var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
                if (n == 0)
                    break; // The file shrank mid-read (a roll); serve what we have.
                read += n;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var lines = SplitLines(text, lineCount);
            return new LogTailResult(Path.GetFileName(path), lines);
        }
    }

    /// <summary>
    /// Scans backward for the byte offset that starts the requested window.
    /// '\n' is a single byte in UTF-8, so an offset just after one is always a
    /// safe decode boundary.
    /// </summary>
    private static async Task<long> FindWindowStartAsync(FileStream stream, long length, int lineCount, CancellationToken ct)
    {
        var newlines = 0;
        var pos = length;
        var block = new byte[BlockSize];

        while (pos > 0)
        {
            var readFrom = Math.Max(0, pos - BlockSize);
            var size = (int)(pos - readFrom);
            stream.Seek(readFrom, SeekOrigin.Begin);

            var filled = 0;
            while (filled < size)
            {
                var n = await stream.ReadAsync(block.AsMemory(filled, size - filled), ct);
                if (n == 0)
                    break;
                filled += n;
            }

            for (var i = filled - 1; i >= 0; i--)
            {
                if (block[i] != (byte)'\n')
                    continue;

                // The newline that ends the last line of the file does not
                // bound the window; every one after that does.
                newlines++;
                if (newlines > lineCount)
                    return readFrom + i + 1;
            }

            pos = readFrom;
        }

        return 0;
    }

    private static List<string> SplitLines(string text, int lineCount)
    {
        var all = text.Split('\n');
        var lines = new List<string>(Math.Min(all.Length, lineCount));
        // A trailing '\n' yields one empty final element that is not a line.
        var end = all.Length > 0 && all[^1].Length == 0 ? all.Length - 1 : all.Length;
        var start = Math.Max(0, end - lineCount);
        for (var i = start; i < end; i++)
            lines.Add(all[i].TrimEnd('\r'));
        return lines;
    }
}
