// -----------------------------------------------------------------------
// <copyright file="LlmResponseClassifier.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace Netclaw.Actors.Sessions;

internal static partial class LlmResponseClassifier
{
    // Plan-without-action heuristic bounds (turn-loop-governance): a reply
    // longer than this is treated as a real answer, never as a bare plan.
    private const int MaxPlanOnlyChars = 400;

    // First-person intent constructions ("I'll check...", "Let me search...",
    // "I am going to open..."). Original implementation — the AGPL Unsloth
    // heuristic was not consulted for this pattern.
    [GeneratedRegex(
        @"\b(i\s*(?:'ll|will|am\s+going\s+to|need\s+to|plan\s+to|should\s+now|can\s+now)|let\s+me|next\s+i(?:'ll|\s+will))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IntentPattern();

    public static LlmResponseAnalysis Analyze(ChatMessage message)
    {
        var toolCalls = new List<FunctionCallContent>();
        bool hasText = false, hasThinking = false;
        int textChars = 0, thinkingChars = 0;

        foreach (var content in message.Contents)
        {
            switch (content)
            {
                case TextContent text:
                    textChars += text.Text?.Length ?? 0;
                    if (!string.IsNullOrWhiteSpace(text.Text))
                        hasText = true;
                    break;
                case TextReasoningContent reasoning:
                    thinkingChars += reasoning.Text?.Length ?? 0;
                    if (!string.IsNullOrWhiteSpace(reasoning.Text))
                        hasThinking = true;
                    break;
                case FunctionCallContent toolCall:
                    toolCalls.Add(toolCall);
                    break;
            }
        }

        var kind =
            toolCalls.Count > 0 ? LlmResponseKind.ToolCalls
            : hasText ? LlmResponseKind.Text
            : hasThinking ? LlmResponseKind.ThinkingOnly
            : LlmResponseKind.Empty;

        return new LlmResponseAnalysis(toolCalls, kind, textChars, thinkingChars);
    }

    /// <summary>
    /// Plan-without-action refinement of a <see cref="LlmResponseKind.Text"/>
    /// response: short, states a first-person intent to act, asks the user
    /// nothing, and carries no code. Callers opt in per config
    /// (<c>PlanRepromptEnabled</c>) — <see cref="Analyze"/> never returns
    /// <see cref="LlmResponseKind.PlanWithoutAction"/> on its own, so every
    /// other consumer of the classifier is unaffected.
    /// </summary>
    public static bool IsPlanWithoutAction(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var trimmed = text.Trim().Replace('’', '\'');
        if (trimmed.Length > MaxPlanOnlyChars)
            return false;

        // A question is a real reply (the model needs input), and a code fence
        // is delivered work — neither is a bare plan.
        if (trimmed.Contains('?', StringComparison.Ordinal)
            || trimmed.Contains("```", StringComparison.Ordinal))
            return false;

        return IntentPattern().IsMatch(trimmed);
    }
}

/// <summary>
/// What the model produced on a turn. Tool calls take precedence over reply
/// text, and reply text over reasoning — so a response carrying both text and
/// tool calls classifies as <see cref="ToolCalls"/>.
/// </summary>
public enum LlmResponseKind
{
    /// <summary>Contains reply text — a normal answer to the user.</summary>
    Text,

    /// <summary>Requested one or more tool calls.</summary>
    ToolCalls,

    /// <summary>Emitted reasoning but no reply text and no tool calls.</summary>
    ThinkingOnly,

    /// <summary>No reply text, no reasoning, and no tool calls.</summary>
    Empty,

    /// <summary>
    /// Reply text that only states an intent to act: short, first-person plan,
    /// no tool call, no final answer. Produced only by the opt-in
    /// plan-without-action refinement, never by <c>Analyze</c> directly.
    /// </summary>
    PlanWithoutAction,
}

internal sealed record LlmResponseAnalysis(
    List<FunctionCallContent> ToolCalls,
    LlmResponseKind Kind,
    int TextChars,
    int ThinkingChars);
