// -----------------------------------------------------------------------
// <copyright file="TurnStateTracker.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
namespace Netclaw.Actors.Sessions.Handlers;

/// <summary>
/// Owns tool-loop control flow decisions: budget tracking, duplicate detection,
/// empty-response retry logic, and force-no-tools state. The actor asks
/// "what should I do?" and the tracker answers based on accumulated state.
/// </summary>
internal sealed class TurnStateTracker
{
    private const int MaxPreToolEmptyRetries = 5;
    private const int MaxPostToolEmptyRetries = 8;
    private const int DuplicateToolThreshold = 3;
    private const double BudgetNudgeRatio = 0.75;

    // Nudge for a thinking-only response: the model emitted reasoning but no
    // final answer. Generic across providers — no provider-specific payload.
    private const string ThinkingOnlyNudge =
        "Your last response contained only reasoning and no reply to the user. "
        + "Stop thinking and write your answer now as a normal assistant message.";

    // A length-truncated response was cut off mid-output by the provider's token
    // ceiling — it did not refuse to answer, so the "stop thinking" scold is
    // counterproductive. Ask for brevity so the next attempt fits the budget.
    private const string TruncatedResponseNudge =
        "Your previous response was cut off before you finished — it reached the output length limit. "
        + "Give your final answer directly now and keep any reasoning brief.";

    private const string PreToolEmptyNudge =
        "Your previous response was empty. If you need MCP capabilities, call search_tools(\"servers\") to pick a server "
        + "(for example browser, memory, or email), then call search_tools(\"<intent>\", server: \"<server_name>\") to load tools. "
        + "MCP tools are not directly callable until loaded via search_tools.";

    private const string PostToolEmptyNudge =
        "You received tool results but did not respond. "
        + "Continue working or produce your final response.";

    private const string EmptyResponseFailureMessage =
        "I didn't manage to produce a reply. Please try rephrasing or sending your request again.";

    // Nudge for a tool iteration that returned error results: without it,
    // local models tend to retry the same call verbatim or spiral into
    // thinking loops (observed 2026-08-27). Names no tool and echoes no
    // arguments — the attributed error text is already in the transcript.
    private const string ToolErrorNudgeText =
        "One or more of your tool calls returned an error. "
        + "Read the error message in the tool result, then either try a different approach or report the failure to the user. "
        + "Do not retry the same tool with identical arguments.";

    // Act-or-report nudge after a thinking-cap breach: the stream was cut, the
    // accumulated thinking stayed in the transcript, and the model gets one
    // chance to convert that reasoning into action.
    private const string ThinkingCapNudge =
        "Your reasoning reached the configured thinking limit before you produced a reply or a tool call. "
        + "Act now: call the tool you need, or report your status to the user in one short message.";

    // Operator-visible failure when the re-prompted response breaches again.
    // Names the cap so the operator knows which knob to turn.
    private const string ThinkingCapFailureMessage =
        "I stopped this turn: my reasoning reached the configured thinking cap twice without producing a reply or a tool call. "
        + "Ask again, or raise Session.ThinkingCapChars if this task needs deeper reasoning.";

    private const int MaxPlanRepromptsPerTurn = 3;

    // Nudge for a plan-only reply: the model narrated its next step instead of
    // taking it. Sent only when PlanRepromptEnabled is on.
    private const string PlanRepromptNudge =
        "You stated a plan but did not act on it. "
        + "Execute the plan now: call the tool you named, or give the user your final answer.";

    private readonly Dictionary<ToolCallFingerprint, int> _toolCallCounts = [];
    private readonly List<string> _planOnlyResponses = [];

    public int ToolCallCount { get; private set; }
    public int ToolIterationCount { get; private set; }
    public bool ForceNoToolsActive { get; set; }

    private bool _budgetNudgeSent;
    private int _postToolEmptyResponseCount;
    private int _preToolEmptyResponseCount;
    private bool _duplicateNudgeSent;
    private string? _duplicateNudgeToolName;
    private bool _thinkingCapRepromptUsed;

    /// <summary>
    /// Reset all per-turn state. Called at the start of each user turn.
    /// </summary>
    public void ResetForNewTurn()
    {
        ToolCallCount = 0;
        ToolIterationCount = 0;
        _budgetNudgeSent = false;
        _postToolEmptyResponseCount = 0;
        _preToolEmptyResponseCount = 0;
        ForceNoToolsActive = false;
        _toolCallCounts.Clear();
        _duplicateNudgeSent = false;
        _duplicateNudgeToolName = null;
        _thinkingCapRepromptUsed = false;
        _planOnlyResponses.Clear();
    }

    /// <summary>
    /// Partial reset for mid-turn buffer drain: clears tool counters and hashes
    /// but preserves empty-response and force-no-tools state.
    /// </summary>
    public void ResetToolCounters()
    {
        ToolCallCount = 0;
        ToolIterationCount = 0;
        _toolCallCounts.Clear();
        _duplicateNudgeSent = false;
        _duplicateNudgeToolName = null;
    }

    /// <summary>
    /// Reset empty-response guards when the model starts doing tool work.
    /// Called when a new tool call batch is initiated — the model is clearly
    /// not stuck, so retry counters reset.
    /// </summary>
    public void ResetEmptyResponseGuards()
    {
        _postToolEmptyResponseCount = 0;
        _preToolEmptyResponseCount = 0;
        ForceNoToolsActive = false;
    }

    // ── Tool call tracking ──

    /// <summary>
    /// Record a tool call for duplicate detection.
    /// </summary>
    public void TrackToolCall(string toolName, string? argumentsJson)
    {
        var fingerprint = new ToolCallFingerprint(toolName, argumentsJson ?? "{}");
        _toolCallCounts.TryGetValue(fingerprint, out var count);
        _toolCallCounts[fingerprint] = count + 1;
    }

    // ── Tool budget decisions ──

    /// <summary>
    /// Record completed tool results and determine what the actor should do next.
    /// Call after tool execution completes with the number of results in the batch.
    /// Enforcement is iteration-based: one completed LLM-to-tools round increments
    /// <see cref="ToolIterationCount"/> by 1 regardless of how many tool calls
    /// were issued in parallel. <see cref="ToolCallCount"/> is retained for
    /// telemetry only.
    /// </summary>
    public ToolBudgetStatus RecordToolCompletion(int resultCount, int maxToolIterationsPerTurn)
    {
        ToolCallCount += resultCount;
        ToolIterationCount++;

        if (ToolIterationCount >= maxToolIterationsPerTurn)
        {
            return new ToolBudgetStatus.Exhausted(
                $"You have reached the tool iteration limit for this turn. "
                + "Do NOT request any more tools. "
                + "Produce a concise final executive summary based only on the information gathered so far. "
                + "Use this format: Summary, Completed, Partial or Unknown, Caveats, Useful Evidence. "
                + "Clearly state that the result is partial when work remains or evidence is incomplete.");
        }

        var budgetThreshold = (int)(maxToolIterationsPerTurn * BudgetNudgeRatio);
        if (ToolIterationCount >= budgetThreshold && !_budgetNudgeSent)
        {
            _budgetNudgeSent = true;
            var remaining = maxToolIterationsPerTurn - ToolIterationCount;
            return new ToolBudgetStatus.NudgeNeeded(
                remaining,
                $"You have used {ToolIterationCount} of {maxToolIterationsPerTurn} tool iterations for this turn. "
                + $"You have approximately {remaining} iterations remaining. "
                + "Start wrapping up your tool usage and prepare to produce your final response.");
        }

        return ToolBudgetStatus.Ok.Instance;
    }

    // ── Duplicate detection decisions ──

    /// <summary>
    /// Check for duplicate tool calls and return a nudge if the threshold is met.
    /// Returns null if no duplicates warrant a nudge.
    /// </summary>
    public DuplicateToolNudge? CheckForDuplicates()
    {
        if (_duplicateNudgeSent)
            return null;

        foreach (var (fingerprint, count) in _toolCallCounts)
        {
            if (count < DuplicateToolThreshold) continue;

            _duplicateNudgeSent = true;
            _duplicateNudgeToolName = fingerprint.ToolName;
            return new DuplicateToolNudge(
                fingerprint.ToolName, count,
                $"You have called the tool '{fingerprint.ToolName}' with the same arguments {count} times this turn. "
                + "This strongly indicates you are repeating work you already completed. "
                + "Review your prior tool results — the information you need is already in the conversation. "
                + "If the task is complete, produce your final response.");
        }

        return null;
    }

    // ── Tool error decisions ──

    /// <summary>
    /// Evaluate the error results of one completed tool iteration and return a
    /// follow-up nudge, or null when no nudge is warranted. Called at most once
    /// per iteration, so the nudge is naturally one-shot per iteration.
    /// Suppression is per tool: when the duplicate-call guard already fired for
    /// a tool this turn, that tool's errors do not trigger another scold — the
    /// duplicate nudge already told the model to stop repeating it.
    /// </summary>
    public ToolErrorNudge? EvaluateToolErrors(IReadOnlyCollection<string> errorToolNames)
    {
        if (errorToolNames.Count == 0)
            return null;

        var nudgeworthy = errorToolNames
            .Where(name => !string.Equals(name, _duplicateNudgeToolName, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return nudgeworthy.Length == 0
            ? null
            : new ToolErrorNudge(nudgeworthy, ToolErrorNudgeText);
    }

    // ── Thinking cap decisions ──

    /// <summary>
    /// The streaming reader cut a response at the per-response thinking cap.
    /// The first breach in a turn earns one act-or-report re-prompt; a second
    /// breach fails the turn with an operator-visible message that names the
    /// cap as the cause.
    /// </summary>
    public EmptyResponseAction EvaluateThinkingCapBreach()
    {
        if (_thinkingCapRepromptUsed)
        {
            return new EmptyResponseAction.Fail(
                ThinkingCapFailureMessage,
                new InvalidOperationException("LLM reached the per-response thinking cap twice in one turn."));
        }

        _thinkingCapRepromptUsed = true;
        return new EmptyResponseAction.Retry(ThinkingCapNudge);
    }

    // ── Plan-without-action decisions ──

    /// <summary>
    /// A completed response classified as plan-without-action. Returns the
    /// re-prompt nudge, or null when the response should be delivered instead:
    /// the per-turn re-prompt budget (3) is spent, or the response restates a
    /// prior plan-only response this turn (the model is looping, not planning).
    /// </summary>
    public string? EvaluatePlanWithoutAction(string text)
    {
        if (_planOnlyResponses.Count >= MaxPlanRepromptsPerTurn)
            return null;

        var normalized = NormalizePlanText(text);
        if (_planOnlyResponses.Any(prior => IsRestatement(prior, normalized)))
            return null;

        _planOnlyResponses.Add(normalized);
        return PlanRepromptNudge;
    }

    private static string NormalizePlanText(string text)
    {
        var builder = new System.Text.StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
                lastWasSpace = false;
            }
            else if (!lastWasSpace && builder.Length > 0)
            {
                builder.Append(' ');
                lastWasSpace = true;
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static bool IsRestatement(string prior, string current)
        => prior == current
           || (current.Length > 0 && prior.Contains(current, StringComparison.Ordinal))
           || (prior.Length > 0 && current.Contains(prior, StringComparison.Ordinal));

    // ── Empty response decisions ──

    /// <summary>
    /// The LLM produced no reply text and no tool calls. Determine what the
    /// actor should do. Expects <paramref name="kind"/> to be
    /// <see cref="LlmResponseKind.ThinkingOnly"/> or
    /// <see cref="LlmResponseKind.Empty"/>.
    /// <para>
    /// Consecutive counters track empty responses in the pre-tool and post-tool
    /// phases independently. They are cleared by
    /// <see cref="ResetEmptyResponseGuards"/> when the model initiates a tool
    /// batch — legitimate thinking-only responses interleaved with tool work do
    /// not accumulate toward the failure threshold, so reasoning models are not
    /// penalised for their normal workflow.
    /// </para>
    /// <para>
    /// <paramref name="truncated"/> is true when the provider reported a
    /// length/token-limit finish reason. Such a response was cut off mid-output,
    /// not refused, so it gets a brevity nudge rather than the "stop thinking"
    /// scold.
    /// </para>
    /// </summary>
    public EmptyResponseAction EvaluateEmptyResponse(
        LlmResponseKind kind,
        bool truncated)
    {
        // Pre-tool: LLM hasn't done any tool work yet
        if (ToolIterationCount == 0)
        {
            _preToolEmptyResponseCount++;
            if (_preToolEmptyResponseCount > MaxPreToolEmptyRetries)
                return new EmptyResponseAction.Fail(
                    EmptyResponseFailureMessage,
                    new InvalidOperationException("LLM produced repeated empty responses before any tool execution."));

            return new EmptyResponseAction.Retry(SelectNudge(kind, truncated, preTool: true));
        }

        // Post-tool: nudge the model to produce its final reply
        _postToolEmptyResponseCount++;
        if (_postToolEmptyResponseCount > MaxPostToolEmptyRetries)
            return new EmptyResponseAction.Fail(
                EmptyResponseFailureMessage,
                new InvalidOperationException("LLM produced repeated empty responses after tool execution."));

        return new EmptyResponseAction.Retry(SelectNudge(kind, truncated, preTool: false));
    }

    private static string SelectNudge(LlmResponseKind kind, bool truncated, bool preTool)
    {
        if (truncated)
            return TruncatedResponseNudge;
        if (kind == LlmResponseKind.ThinkingOnly)
            return ThinkingOnlyNudge;
        return preTool ? PreToolEmptyNudge : PostToolEmptyNudge;
    }
}

internal readonly record struct ToolCallFingerprint(string ToolName, string ArgumentsJson);

// ── Result types ──

/// <summary>Result of <see cref="TurnStateTracker.RecordToolCompletion"/>.</summary>
internal abstract record ToolBudgetStatus
{
    /// <summary>Under budget, continue normally.</summary>
    internal sealed record Ok : ToolBudgetStatus
    {
        public static readonly Ok Instance = new();
    }

    /// <summary>Approaching budget limit — inject a nudge.</summary>
    internal sealed record NudgeNeeded(int Remaining, string NudgeText) : ToolBudgetStatus;

    /// <summary>Budget exhausted — force text-only response.</summary>
    internal sealed record Exhausted(string NudgeText) : ToolBudgetStatus;
}

/// <summary>Result of <see cref="TurnStateTracker.CheckForDuplicates"/>.</summary>
internal sealed record DuplicateToolNudge(string ToolName, int Count, string NudgeText);

/// <summary>Result of <see cref="TurnStateTracker.EvaluateToolErrors"/>.</summary>
internal sealed record ToolErrorNudge(IReadOnlyList<string> ToolNames, string NudgeText);

/// <summary>Result of <see cref="TurnStateTracker.EvaluateEmptyResponse"/>.</summary>
internal abstract record EmptyResponseAction
{
    /// <summary>Retry the LLM call with the given nudge text.</summary>
    internal sealed record Retry(string NudgeText) : EmptyResponseAction;

    /// <summary>Fail the turn with the given error message and cause.</summary>
    internal sealed record Fail(string ErrorMessage, Exception Cause) : EmptyResponseAction;
}
