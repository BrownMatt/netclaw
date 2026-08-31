// -----------------------------------------------------------------------
// <copyright file="ChatSessionViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Netclaw.Actors.Protocol;
using static Netclaw.Actors.Sessions.SessionProtocol;

namespace Netclaw.Gui.ViewModels;

/// <summary>
/// Session activity for the indicator, driven only by output events.
/// </summary>
public enum ActivityState
{
    Idle,
    Waiting,
    Responding,
    RunningTool,
    ApprovalRequired
}

/// <summary>
/// The chat middle pane for one attached session: maps the
/// <see cref="SessionOutput"/> stream onto history blocks, the activity
/// indicator, the usage line, and the grant chip list. Free of any UI
/// dispatcher — the shell marshals events onto the UI thread before calling
/// in, so every transition is unit-testable.
/// </summary>
public sealed partial class ChatSessionViewModel : ObservableObject
{
    private readonly Func<string, string, Task> _respondToInteraction;
    private AssistantMessageBlockViewModel? _streamingAssistant;
    private ThinkingBlockViewModel? _streamingThinking;
    private readonly Dictionary<string, ToolCallBlockViewModel> _openToolCalls = [];
    private readonly Dictionary<string, ApprovalCardViewModel> _openApprovals = [];

    public ChatSessionViewModel(string sessionId, Func<string, string, Task> respondToInteraction)
    {
        SessionId = sessionId;
        _respondToInteraction = respondToInteraction;
    }

    public string SessionId { get; }

    public ObservableCollection<ChatBlockViewModel> Blocks { get; } = [];

    public ObservableCollection<string> GrantedFolders { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityText))]
    private ActivityState _activity = ActivityState.Idle;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActivityText))]
    private string? _runningToolName;

    /// <summary>
    /// Blank until the first usage event — never zeros (spec: Usage line).
    /// </summary>
    [ObservableProperty]
    private string _usageText = string.Empty;

    public string ActivityText => Activity switch
    {
        ActivityState.Idle => string.Empty,
        ActivityState.Waiting => "Waiting...",
        ActivityState.Responding => "Responding...",
        ActivityState.RunningTool => $"Running tool: {RunningToolName}",
        ActivityState.ApprovalRequired => "Approval required",
        _ => string.Empty
    };

    /// <summary>Called by the shell when the operator's message is sent.</summary>
    public void OnMessageSent(string text)
    {
        Blocks.Add(new UserMessageBlockViewModel { Text = text });
        Activity = ActivityState.Waiting;
    }

    /// <summary>
    /// Hydrates the pane from <see cref="SessionJoined"/>: grant chips always,
    /// replayed messages only when the history is still empty. A re-join for a
    /// session with live local history (e.g. after a send-failure recovery)
    /// must not replace it — the local blocks are richer than the truncated
    /// replay window and can include a message the replay does not carry yet.
    /// </summary>
    public void LoadReplay(SessionJoined joined)
    {
        GrantedFolders.Clear();
        foreach (var folder in joined.GrantedFolders)
            GrantedFolders.Add(folder);

        if (Blocks.Count > 0)
            return;

        foreach (var message in joined.RecentMessages ?? [])
        {
            if (string.IsNullOrWhiteSpace(message.Content))
                continue;

            if (string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                Blocks.Add(new UserMessageBlockViewModel { Text = message.Content });
            else
                Blocks.Add(new AssistantMessageBlockViewModel().Complete(message.Content));
        }
    }

    /// <summary>
    /// Flushes buffered streaming deltas into the visible blocks. The shell
    /// calls this on a ~80 ms timer while a turn streams.
    /// </summary>
    public void FlushStreamingDeltas()
    {
        _streamingAssistant?.FlushDeltas();
        _streamingThinking?.FlushDeltas();
    }

    public void HandleOutput(SessionOutput output)
    {
        switch (output)
        {
            case TextDeltaOutput delta:
                CurrentAssistantBlock().AppendDelta(delta.Delta);
                Activity = ActivityState.Responding;
                break;

            case TextOutput text:
                // The final snapshot is authoritative — it replaces every
                // accumulated delta, so no fragment can remain duplicated.
                CurrentAssistantBlock().SetFinalText(text.Text);
                _streamingAssistant = null;
                _streamingThinking = null;
                break;

            case ThinkingDeltaOutput delta:
                CurrentThinkingBlock().AppendDelta(delta.Delta);
                Activity = ActivityState.Responding;
                break;

            case ThinkingOutput thinking:
                CurrentThinkingBlock().SetFinalText(thinking.Text);
                _streamingThinking = null;
                break;

            case ToolCallOutput call:
            {
                var block = new ToolCallBlockViewModel
                {
                    CallId = call.CallId.Value,
                    ToolName = call.ToolName.Value,
                    ArgumentsJson = call.ArgumentsJson
                };
                _openToolCalls[call.CallId.Value] = block;
                Blocks.Add(block);
                // A new tool call closes the current streaming text block —
                // later deltas belong to the model's next reply segment.
                _streamingAssistant?.FlushDeltas();
                _streamingAssistant = null;
                _streamingThinking = null;
                RunningToolName = call.ToolName.Value;
                Activity = ActivityState.RunningTool;
                break;
            }

            case ToolResultOutput result:
            {
                if (_openToolCalls.Remove(result.CallId.Value, out var block))
                    block.SetResult(result.Result);
                RunningToolName = null;
                Activity = ActivityState.Waiting;
                break;
            }

            case ToolInteractionRequest interaction:
            {
                var card = new ApprovalCardViewModel(
                    interaction.CallId.Value,
                    interaction.ToolName.Value,
                    interaction.DisplayText,
                    [.. interaction.Options.Select(o => new ApprovalOptionViewModel(o.Key.Value, o.Label))],
                    RespondAndResumeAsync);
                _openApprovals[interaction.CallId.Value] = card;
                Blocks.Add(card);
                Activity = ActivityState.ApprovalRequired;
                break;
            }

            case UsageOutput usage:
                UsageText = FormatUsage(usage);
                break;

            case FolderGrantOutput grant:
                GrantedFolders.Clear();
                foreach (var folder in grant.GrantedFolders)
                    GrantedFolders.Add(folder);
                break;

            case SessionTitleOutput:
                // The session list owns title display.
                break;

            case ErrorOutput error:
                FlushStreamingDeltas();
                _streamingAssistant = null;
                _streamingThinking = null;
                Blocks.Add(new ErrorBlockViewModel { Message = error.Message });
                Activity = ActivityState.Idle;
                break;

            case TurnCompleted:
                FlushStreamingDeltas();
                _streamingAssistant = null;
                _streamingThinking = null;
                RunningToolName = null;
                Activity = ActivityState.Idle;
                break;
        }
    }

    /// <summary>
    /// The spec's usage form: <c>in=&lt;n&gt; out=&lt;n&gt; (&lt;p&gt;% ctx)</c>.
    /// Values the event does not carry stay absent rather than showing zeros.
    /// </summary>
    internal static string FormatUsage(UsageOutput usage)
    {
        if (usage.InputTokens is null && usage.OutputTokens is null)
            return string.Empty;

        var parts = new List<string>(3);
        if (usage.InputTokens is { } input)
            parts.Add($"in={input}");
        if (usage.OutputTokens is { } outputTokens)
            parts.Add($"out={outputTokens}");

        var percent = usage.UsagePercent
                      ?? (usage.InputTokens is { } inTokens && usage.ContextWindowTokens > 0
                          ? (double)inTokens / usage.ContextWindowTokens
                          : null);
        if (percent is { } p)
            parts.Add($"({p * 100:0.#}% ctx)");

        return string.Join(" ", parts);
    }

    private async Task RespondAndResumeAsync(string callId, string selectedKey)
    {
        await _respondToInteraction(callId, selectedKey);
        _openApprovals.Remove(callId);
        if (Activity == ActivityState.ApprovalRequired)
            Activity = ActivityState.Waiting;
    }

    private AssistantMessageBlockViewModel CurrentAssistantBlock()
    {
        if (_streamingAssistant is null)
        {
            _streamingAssistant = new AssistantMessageBlockViewModel();
            Blocks.Add(_streamingAssistant);
        }

        return _streamingAssistant;
    }

    private ThinkingBlockViewModel CurrentThinkingBlock()
    {
        if (_streamingThinking is null)
        {
            _streamingThinking = new ThinkingBlockViewModel();
            Blocks.Add(_streamingThinking);
        }

        return _streamingThinking;
    }
}

internal static class AssistantBlockExtensions
{
    public static AssistantMessageBlockViewModel Complete(this AssistantMessageBlockViewModel block, string content)
    {
        block.SetFinalText(content);
        return block;
    }
}
