// -----------------------------------------------------------------------
// <copyright file="ChatBlocks.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Netclaw.Gui.ViewModels;

/// <summary>
/// Base type for one rendered block in the chat history. The history is an
/// <c>ItemsControl</c> over these viewmodels; per-type data templates in
/// <c>MainWindow.axaml</c> select the visual. Grouped in one file because
/// the types form one closed union consumed by one control.
/// </summary>
public abstract class ChatBlockViewModel : ObservableObject;

/// <summary>A message the operator sent.</summary>
public sealed class UserMessageBlockViewModel : ChatBlockViewModel
{
    public required string Text { get; init; }
}

/// <summary>
/// One assistant reply. Streams via <see cref="AppendDelta"/> plus
/// <see cref="FlushDeltas"/> (the shell flushes on a ~80 ms timer so the
/// editor is not invalidated per token); the final snapshot replaces the
/// accumulated deltas through <see cref="SetFinalText"/>.
/// </summary>
public sealed partial class AssistantMessageBlockViewModel : ChatBlockViewModel
{
    private readonly System.Text.StringBuilder _pending = new();

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private bool _isStreaming = true;

    public void AppendDelta(string delta) => _pending.Append(delta);

    public bool HasPendingDeltas => _pending.Length > 0;

    public void FlushDeltas()
    {
        if (_pending.Length == 0)
            return;

        Content += _pending.ToString();
        _pending.Clear();
    }

    public void SetFinalText(string text)
    {
        _pending.Clear();
        Content = text;
        IsStreaming = false;
    }
}

/// <summary>
/// Model thinking content under a collapsed-by-default expander. Expansion
/// is local UI state only.
/// </summary>
public sealed partial class ThinkingBlockViewModel : ChatBlockViewModel
{
    private readonly System.Text.StringBuilder _pending = new();

    [ObservableProperty]
    private string _content = string.Empty;

    [ObservableProperty]
    private bool _isExpanded;

    public void AppendDelta(string delta) => _pending.Append(delta);

    public void FlushDeltas()
    {
        if (_pending.Length == 0)
            return;

        Content += _pending.ToString();
        _pending.Clear();
    }

    public void SetFinalText(string text)
    {
        _pending.Clear();
        Content = text;
    }
}

/// <summary>
/// One tool call and its result under a single collapsed-by-default header.
/// </summary>
public sealed partial class ToolCallBlockViewModel : ChatBlockViewModel
{
    public required string CallId { get; init; }

    public required string ToolName { get; init; }

    public string? ArgumentsJson { get; init; }

    [ObservableProperty]
    private string? _result;

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isRunning = true;

    public string Header => $"Tool: {ToolName}";

    public void SetResult(string result)
    {
        Result = result;
        IsRunning = false;
    }
}

/// <summary>One selectable option on an approval card.</summary>
public sealed record ApprovalOptionViewModel(string Key, string Label);

/// <summary>
/// Inline approval card for a <c>tool_interaction</c> event. The card
/// resolves once: the first selected option disables every option and the
/// resolved outcome stays visible.
/// </summary>
public sealed partial class ApprovalCardViewModel : ChatBlockViewModel
{
    private readonly Func<string, string, Task> _respond;

    public ApprovalCardViewModel(
        string callId,
        string toolName,
        string displayText,
        IReadOnlyList<ApprovalOptionViewModel> options,
        Func<string, string, Task> respond)
    {
        CallId = callId;
        ToolName = toolName;
        DisplayText = displayText;
        Options = options;
        _respond = respond;
    }

    public string CallId { get; }

    public string ToolName { get; }

    public string DisplayText { get; }

    public IReadOnlyList<ApprovalOptionViewModel> Options { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRespond))]
    private bool _isResolved;

    [ObservableProperty]
    private string? _resolvedLabel;

    public bool CanRespond => !IsResolved;

    [RelayCommand]
    private async Task RespondAsync(ApprovalOptionViewModel option)
    {
        // Resolve once: mark before the network call so a double click can
        // never send a second response. The daemon rejects duplicates too.
        if (IsResolved)
            return;

        IsResolved = true;
        ResolvedLabel = option.Label;
        await _respond(CallId, option.Key);
    }
}

/// <summary>Per-turn token usage line, e.g. <c>in=1200 out=300 (0.9% ctx)</c>.</summary>
public sealed class UsageLineBlockViewModel : ChatBlockViewModel
{
    public required string Text { get; init; }
}

/// <summary>A daemon error surfaced into the history.</summary>
public sealed class ErrorBlockViewModel : ChatBlockViewModel
{
    public required string Message { get; init; }
}
