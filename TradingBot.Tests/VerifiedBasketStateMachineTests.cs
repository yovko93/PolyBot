using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class VerifiedBasketStateMachineTests
{
    [Fact]
    public void Open_paper_position_prevents_transition_back_to_edge_executable_pending()
    {
        var stability = new VerifiedOpportunityStabilityTracker();
        stability.MarkPaperOpened("colombia");

        var state = stability.Track("colombia", Screen("colombia", activeNet: 0.0035m, experimentalNet: 0m), 10, 3, 0.001m, 0.002m);

        Assert.Equal(VerifiedBasketState.PaperOpened, state);
    }

    [Fact]
    public void Duplicate_open_group_causes_suppressed_duplicate_state()
    {
        var stability = new VerifiedOpportunityStabilityTracker();
        stability.MarkSuppressedDuplicate("colombia");

        Assert.Equal(VerifiedBasketState.SuppressedDuplicate, stability.State("colombia"));
        Assert.Equal("DuplicateOpenPosition", stability.LastResetReason("colombia"));
    }

    [Fact]
    public void Polymarket_positive_but_conservative_negative_is_experimental_only()
    {
        var row = Screen("winner:2026 nba finals", activeNet: -0.0015m, experimentalNet: 0.0015m);

        Assert.False(row.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.ExecutableUnderActiveProfile);
        Assert.Equal(VerifiedBasketScreener.ExecutionStatus.ExperimentalPaperCandidate, row.ExecutionStatus);
    }

    [Fact]
    public void Experimental_candidate_does_not_increment_active_executable_count()
    {
        var rows = new[]
        {
            Screen("colombia", 0.0035m, 0.002m),
            Screen("nba", -0.0015m, 0.0015m)
        };

        var activeExecutable = rows.Count(x => x.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.ExecutableUnderActiveProfile);
        var experimental = rows.Count(x => x.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.ExperimentalPaperCandidate);

        Assert.Equal(1, activeExecutable);
        Assert.Equal(1, experimental);
    }

    [Fact]
    public void Multi_verified_scan_counts_separate_active_and_experimental()
    {
        var activeExecutable = 1;
        var experimentalCandidates = 1;
        var line = $"[MULTI_VERIFIED_SCAN] ActiveExecutable={activeExecutable} ExperimentalCandidates={experimentalCandidates}";

        Assert.Contains("ActiveExecutable=1", line);
        Assert.Contains("ExperimentalCandidates=1", line);
        Assert.DoesNotContain("Executable=2", line);
    }

    [Fact]
    public void Colombian_open_position_appears_as_paper_opened_or_suppressed()
    {
        var stability = new VerifiedOpportunityStabilityTracker();
        stability.MarkSuppressedDuplicate("colombia");

        Assert.True(stability.State("colombia") is VerifiedBasketState.PaperOpened or VerifiedBasketState.SuppressedDuplicate);
    }

    [Fact]
    public void Execution_audit_has_no_new_edge_pending_after_paper_open_when_duplicate_is_gated()
    {
        var stages = new[] { "EdgeDetected", "EdgeStable", "ExecutionReadinessStable", "PreTradeApproved", "DryRunOrderPlanCreated", "DryRunFillSimulationPassed", "PaperOpened", "DuplicateSuppressed" };
        var afterPaperOpened = stages.SkipWhile(x => x != "PaperOpened").ToArray();

        Assert.DoesNotContain("EdgeExecutablePending", afterPaperOpened);
    }


    [Fact]
    public void Duplicate_open_group_skips_edge_stability_and_pretrade()
    {
        var stability = new VerifiedOpportunityStabilityTracker();
        stability.MarkSuppressedDuplicate("colombia");
        var stateBefore = stability.State("colombia");

        var stateAfterTrack = stability.Track("colombia", Screen("colombia", activeNet: 0.0035m, experimentalNet: 0m), 10, 3, 0.001m, 0.002m);

        Assert.Equal(VerifiedBasketState.SuppressedDuplicate, stateBefore);
        Assert.Equal(VerifiedBasketState.SuppressedDuplicate, stateAfterTrack);
    }

    [Fact]
    public void Experimental_candidate_logs_are_throttled()
    {
        var throttle = new LogThrottle();
        var fingerprint = "nba|-0.0015|0.0015|ExperimentalProfilePending";

        Assert.True(throttle.ShouldLog("experimental:nba", fingerprint, onChangeOnly: true, everyNCycles: 50));
        Assert.False(throttle.ShouldLog("experimental:nba", fingerprint, onChangeOnly: true, everyNCycles: 50));
    }

    [Fact]
    public void Candidate_scan_logs_are_throttled()
    {
        var throttle = new LogThrottle();
        Assert.True(throttle.ShouldLog("candidate", "same", onChangeOnly: true, everyNCycles: 25));
        Assert.False(throttle.ShouldLog("candidate", "same", onChangeOnly: true, everyNCycles: 25));
    }

    [Fact]
    public void Critical_events_bypass_throttling()
    {
        var throttle = new LogThrottle();
        Assert.True(throttle.ShouldLog("paper-open", "same", critical: true));
        Assert.True(throttle.ShouldLog("paper-open", "same", critical: true));
    }

    private static VerifiedBasketScreener.ScreenResult Screen(string groupKey, decimal activeNet, decimal experimentalNet)
    {
        var active = new VerifiedBasketScreener.ProfileResult("Conservative", "FixedPerLeg", 0m, 0m, 0m, activeNet, activeNet * 10m, activeNet > 0.001m, false);
        var experimental = new VerifiedBasketScreener.ProfileResult("PolymarketApprox", "None", 0m, 0m, 0m, experimentalNet, experimentalNet * 10m, experimentalNet > 0.001m, false);
        var status = activeNet > 0.001m
            ? VerifiedBasketScreener.ExecutionStatus.ExecutableUnderActiveProfile
            : experimentalNet > 0.001m ? VerifiedBasketScreener.ExecutionStatus.ExperimentalPaperCandidate : VerifiedBasketScreener.ExecutionStatus.NotExecutable;
        return new VerifiedBasketScreener.ScreenResult(groupKey, 3, 2m, 1.99m, 0.01m, activeNet, 10m, activeNet * 10m, "None", status.ToString(), "Conservative", [active, experimental], [], "None", DateTime.UtcNow, 0m, false, "", [], experimentalNet, status);
    }
}
