// -----------------------------------------------------------------------
// <copyright file="PlanWithoutActionTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Actors.Sessions;
using Netclaw.Actors.Sessions.Handlers;
using Xunit;

namespace Netclaw.Actors.Tests.Sessions;

/// <summary>
/// Unit coverage for the plan-without-action heuristic
/// (<see cref="LlmResponseClassifier.IsPlanWithoutAction"/>) and the tracker's
/// re-prompt budget and restatement guard (turn-loop-governance).
/// </summary>
public sealed class PlanWithoutActionTests
{
    [Theory]
    [InlineData("I'll search the web for that now.")]
    [InlineData("Let me check the Morningstar page for a download button.")]
    [InlineData("I am going to open the portfolio page and look for the export link.")]
    [InlineData("First, I need to load the browser tools. Then I will navigate to the site.")]
    public void Plan_only_replies_classify_as_plan_without_action(string text)
        => Assert.True(LlmResponseClassifier.IsPlanWithoutAction(text));

    [Theory]
    // A question to the user is a real reply, not a bare plan.
    [InlineData("I'll need your credentials — which account should I use?")]
    // Delivered work (code) is a real reply.
    [InlineData("I'll run this:\n```bash\nls -la\n```")]
    // A long reply is treated as a real answer even if it names next steps.
    [InlineData("I'll summarize: the portfolio holds 14 positions. The largest is VTI at 22 percent, followed by SCHD at 15 percent. Cash sits at 4 percent, which is above your 2 percent target, and the bond sleeve drifted a full point under its band. Rebalancing would move about 3,100 dollars across four trades. The dashboard now reflects the September prices, the drift table is refreshed, and the two price glitches from last week are gone. Everything else matches the Morningstar export from this morning within a dollar.")]
    // A final answer with no intent construction.
    [InlineData("The download completed. The file is at C:\\exports\\portfolio.csv.")]
    // Empty and whitespace never match.
    [InlineData("")]
    [InlineData("   ")]
    public void Real_replies_stay_text(string text)
        => Assert.False(LlmResponseClassifier.IsPlanWithoutAction(text));

    [Fact]
    public void Reprompt_budget_is_three_per_turn()
    {
        var tracker = new TurnStateTracker();

        Assert.NotNull(tracker.EvaluatePlanWithoutAction("I'll check the first source."));
        Assert.NotNull(tracker.EvaluatePlanWithoutAction("Let me try the second source."));
        Assert.NotNull(tracker.EvaluatePlanWithoutAction("I will look at the third source."));

        // Budget spent: the fourth distinct plan-only reply is delivered.
        Assert.Null(tracker.EvaluatePlanWithoutAction("I'll try a fourth source."));
    }

    [Fact]
    public void Restatement_stops_the_loop_early()
    {
        var tracker = new TurnStateTracker();

        Assert.NotNull(tracker.EvaluatePlanWithoutAction("I'll search the web for that now."));

        // Same plan, different punctuation/casing: restatement, deliver it.
        Assert.Null(tracker.EvaluatePlanWithoutAction("I'LL search the web... for that now!"));
    }

    [Fact]
    public void Plan_state_resets_between_turns()
    {
        var tracker = new TurnStateTracker();
        Assert.NotNull(tracker.EvaluatePlanWithoutAction("I'll search the web for that now."));

        tracker.ResetForNewTurn();

        Assert.NotNull(tracker.EvaluatePlanWithoutAction("I'll search the web for that now."));
    }
}
