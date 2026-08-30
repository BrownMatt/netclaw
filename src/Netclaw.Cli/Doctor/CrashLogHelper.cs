// -----------------------------------------------------------------------
// <copyright file="CrashLogHelper.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Globalization;

namespace Netclaw.Cli.Doctor;

/// <summary>
/// Shared crash-log helpers used by <see cref="DaemonCrashDoctorCheck"/>
/// and <see cref="SqliteProvisioningDoctorCheck"/>.
/// </summary>
internal static class CrashLogHelper
{
    /// <summary>
    /// Opens a log file for read without excluding the writer. The live daemon
    /// keeps its current log open with an active write handle, and a plain
    /// <c>new StreamReader(path)</c> shares only read access — on Windows that
    /// combination fails with "the file is being used by another process".
    /// <c>FileShare.Delete</c> also lets log rolling or cleanup proceed while
    /// doctor reads.
    /// </summary>
    public static StreamReader OpenSharedLogReader(string path)
        => new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));

    /// <summary>
    /// Reads a whole log file with the same writer-tolerant sharing as
    /// <see cref="OpenSharedLogReader"/>.
    /// </summary>
    public static string ReadAllTextShared(string path)
    {
        using var reader = OpenSharedLogReader(path);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Returns <c>true</c> if the daemon's PID file was written after the crash log,
    /// indicating the daemon has restarted since the crash occurred.
    /// </summary>
    public static bool IsCrashLogStale(FileInfo crashLog, string pidFilePath)
    {
        var pidFile = new FileInfo(pidFilePath);
        return pidFile.Exists && pidFile.LastWriteTimeUtc > crashLog.LastWriteTimeUtc;
    }

    /// <summary>
    /// Returns crash log files written at or after the given cutoff, ordered by most recent first.
    /// </summary>
    public static IEnumerable<FileInfo> FindCrashLogsSince(string logsDirectory, DateTime cutoffUtc)
    {
        if (!Directory.Exists(logsDirectory))
            return [];

        return new DirectoryInfo(logsDirectory)
            .GetFiles("crash-*.log", SearchOption.TopDirectoryOnly)
            .Where(f => f.LastWriteTimeUtc >= cutoffUtc)
            .OrderByDescending(f => f.LastWriteTimeUtc);
    }

    /// <summary>
    /// Attempts to extract a UTC timestamp from a crash log filename with the format
    /// <c>crash-YYYYMMDD-HHMMSS.log</c> (with optional suffixes after the timestamp).
    /// Returns <c>null</c> if the filename does not match.
    /// </summary>
    public static DateTimeOffset? TryParseCrashTimestamp(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        const string prefix = "crash-";
        if (!stem.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var payload = stem[prefix.Length..];
        if (payload.Length < 15)
            return null;

        var timestampPart = payload[..15];
        if (!DateTimeOffset.TryParseExact(
                timestampPart,
                "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var parsed))
            return null;

        return parsed.ToUniversalTime();
    }
}
