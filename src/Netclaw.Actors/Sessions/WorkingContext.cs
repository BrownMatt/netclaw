// -----------------------------------------------------------------------
// <copyright file="WorkingContext.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.Immutable;
using Netclaw.Actors.Serialization;

namespace Netclaw.Actors.Sessions;

/// <summary>
/// Durable session state for "what the agent is currently working on."
/// Persisted through <see cref="SessionSnapshot"/> and
/// <see cref="SessionProtocol.SessionCompacted"/> events so it survives compaction,
/// actor recovery, and daemon restart without depending on the observer LLM
/// to reconstruct it.
///
/// Carries three fields:
/// - <see cref="RecentFiles"/>: bounded ring buffer of file paths the agent
///   has recently read/written/edited. Most-recent-first, deduped, capped at
///   <see cref="MaxRecentFiles"/>.
/// - <see cref="ProjectDirectory"/>: optional absolute path to the project
///   root the session is working on. Set via <c>set_working_directory</c>
///   tool, persisted across crash/restart. Null means "no project selected."
/// - <see cref="GrantedFolders"/>: operator-granted folder roots for
///   first-party file tools. A grant lives for the session life; removal
///   revokes immediately. Grants never join the shell approval safe-space
///   set — <c>set_working_directory</c> stays the only shell-trust gesture.
/// </summary>
public sealed record WorkingContext : INetclawSerializableMessage
{
    /// <summary>
    /// Maximum number of entries retained in <see cref="RecentFiles"/>.
    /// Older entries fall off the tail when a new distinct path is pushed.
    /// </summary>
    public const int MaxRecentFiles = 10;

    public static readonly WorkingContext Empty = new();

    public ImmutableList<string> RecentFiles { get; init; } =
        [];

    public string? ProjectDirectory { get; init; }

    public ImmutableList<string> GrantedFolders { get; init; } =
        [];

    /// <summary>
    /// Returns true when there is nothing to report — no recent files, no
    /// project directory, and no granted folders. Consumers use this to
    /// suppress the <c>[working-context]</c> block entirely; SessionState
    /// also drops an empty context from snapshots, so a grants-only context
    /// must count as non-empty or grants would not persist.
    /// </summary>
    public bool IsEmpty => RecentFiles.IsEmpty && ProjectDirectory is null && GrantedFolders.IsEmpty;

    /// <summary>
    /// Return a new <see cref="WorkingContext"/> with <see cref="ProjectDirectory"/>
    /// set to the given absolute path. Returns the same instance when the value
    /// is unchanged, so <c>ReferenceEquals</c> callers can short-circuit.
    /// Rejects paths containing control characters for the same prompt-injection
    /// reasons as <see cref="AddRecentFile"/>.
    /// </summary>
    public WorkingContext WithProjectDirectory(string? path)
    {
        if (string.Equals(ProjectDirectory, path, StringComparison.Ordinal))
            return this;

        if (path is not null && path.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0)
            return this;

        return this with { ProjectDirectory = path };
    }

    /// <summary>
    /// Push a file path to the front of <see cref="RecentFiles"/>, dedupe on
    /// repeat, and cap the list at <see cref="MaxRecentFiles"/> entries.
    /// Rejects paths containing control characters (newline, carriage return,
    /// null) — such paths are either malformed or adversarial (prompt
    /// injection into the <see cref="ToContextBlock"/> output). Returns the
    /// same instance when <paramref name="path"/> is already at the head or
    /// is rejected, so <c>ReferenceEquals</c> callers can short-circuit.
    /// </summary>
    public WorkingContext AddRecentFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return this;

        // Reject control characters: a path containing `\n`, `\r`, or `\0`
        // would break out of the [working-context] block rendering and
        // inject arbitrary content into the LLM's system prompt. Real file
        // paths never contain these characters; anything that does is
        // adversarial or malformed input that shouldn't be tracked.
        if (path.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0)
            return this;

        // No-op when path is already the most-recent entry. Returning the
        // same instance lets ReferenceEquals-guarded callers skip the
        // surrounding SessionState allocation entirely.
        if (RecentFiles.Count > 0 && string.Equals(RecentFiles[0], path, StringComparison.Ordinal))
            return this;

        var builder = ImmutableList.CreateBuilder<string>();
        builder.Add(path);

        foreach (var existing in RecentFiles)
        {
            if (builder.Count >= MaxRecentFiles)
                break;

            if (string.Equals(existing, path, StringComparison.Ordinal))
                continue;

            builder.Add(existing);
        }

        return this with { RecentFiles = builder.ToImmutable() };
    }

    /// <summary>
    /// Add an operator-granted folder root. Rejects paths with control
    /// characters for the same prompt-injection reasons as
    /// <see cref="AddRecentFile"/>. Returns the same instance when the path
    /// is rejected or already granted, so <c>ReferenceEquals</c> callers can
    /// short-circuit. The caller (the session actor) validates existence and
    /// normalizes the path before this method runs.
    /// </summary>
    public WorkingContext WithGrantedFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return this;

        if (path.AsSpan().IndexOfAny('\n', '\r', '\0') >= 0)
            return this;

        if (GrantedFolders.Contains(path, StringComparer.Ordinal))
            return this;

        return this with { GrantedFolders = GrantedFolders.Add(path) };
    }

    /// <summary>
    /// Remove an operator-granted folder root. Returns the same instance
    /// when the path is not in the grant list.
    /// </summary>
    public WorkingContext WithoutGrantedFolder(string path)
    {
        var index = GrantedFolders.IndexOf(path, StringComparer.Ordinal);
        if (index < 0)
            return this;

        return this with { GrantedFolders = GrantedFolders.RemoveAt(index) };
    }

    /// <summary>
    /// Render this context as a <c>[working-context]</c> block suitable
    /// for injection into the dynamic system message. Returns an empty
    /// string when the whole context is <see cref="IsEmpty"/> — callers
    /// should check that and suppress the injection entirely rather than
    /// emit a barren header.
    /// </summary>
    public string ToContextBlock()
        => new WorkingContextSnapshot
        {
            WorkingContext = this,
            Git = new GitWorkingContextInspection.Skipped()
        }.ToContextBlock();
}
