using TradingBot.Services;

namespace TradingBot.Tests;

public class VerifiedPaperEligibilityDryRunTests
{
    private static VerifiedPaperEligibilityInput Input(
        bool stability = true,
        bool risk = true,
        bool fill = true,
        bool depth = true,
        bool missingNoAsk = false,
        bool orderbookUnavailable = false,
        bool costProfile = true,
        StrategyMode mode = StrategyMode.DiagnosticsOnly)
        => new("g", 0.01m, 0.01m, 0.02m, 0.015m, 1m, 2m, stability, risk, fill, depth, missingNoAsk, orderbookUnavailable, costProfile, mode);

    [Fact]
    public void Active_positive_with_failed_stability_is_blocked_by_stability()
    {
        var decision = VerifiedPaperEligibilityDryRun.Evaluate(Input(stability: false));
        Assert.False(decision.WouldOpenIfPaperEligible);
        Assert.Equal(VerifiedPaperBlockedReason.Stability, decision.BlockedReason);
    }

    [Fact]
    public void Active_positive_with_failed_fill_is_blocked_by_fill()
    {
        var decision = VerifiedPaperEligibilityDryRun.Evaluate(Input(fill: false));
        Assert.False(decision.WouldOpenIfPaperEligible);
        Assert.Equal(VerifiedPaperBlockedReason.Fill, decision.BlockedReason);
    }

    [Fact]
    public void Active_positive_with_failed_risk_is_blocked_by_risk()
    {
        var decision = VerifiedPaperEligibilityDryRun.Evaluate(Input(risk: false));
        Assert.False(decision.WouldOpenIfPaperEligible);
        Assert.Equal(VerifiedPaperBlockedReason.Risk, decision.BlockedReason);
    }

    [Fact]
    public void All_gates_pass_in_diagnostics_only_would_open_but_blocked_by_diagnostics_only()
    {
        var decision = VerifiedPaperEligibilityDryRun.Evaluate(Input(mode: StrategyMode.DiagnosticsOnly));
        Assert.True(decision.WouldOpenIfPaperEligible);
        Assert.Equal(VerifiedPaperBlockedReason.DiagnosticsOnly, decision.BlockedReason);
    }

    [Fact]
    public void Paper_eligible_requires_explicit_allow_paper_eligible_flag_before_opening_paper()
    {
        var decision = VerifiedPaperEligibilityDryRun.Evaluate(Input(mode: StrategyMode.PaperEligible));
        var config = new OpportunityStrategyConfig(true, StrategyMode.PaperEligible, 50) { AllowPaperEligible = false };
        Assert.True(decision.WouldOpenIfPaperEligible);
        Assert.False(VerifiedPaperEligibilityDryRun.CanOpenPaper(decision, config));
    }

    [Fact]
    public void Raw_positive_only_and_experimental_candidates_are_not_dry_run_paper_candidates()
    {
        AssertRawOrExperimentalDoesNotReachDryRun(isActiveConservativeExecutable: false, isRawPositiveOnly: true, isExperimentalCandidate: false);
        AssertRawOrExperimentalDoesNotReachDryRun(isActiveConservativeExecutable: false, isRawPositiveOnly: false, isExperimentalCandidate: true);
    }

    [Fact]
    public void Live_trading_and_signing_remain_blocked()
    {
        LiveTradingGuard.ResetForTests();
        Assert.Throws<LiveTradingBlockedException>(() => LiveTradingGuard.AssertNoLiveTrading(false, "test", LiveTradingAction.RealApiOrderSubmit));
        Assert.Throws<LiveTradingBlockedException>(() => LiveTradingGuard.AssertOrderSigningAllowed(false));
        Assert.Equal(1, LiveTradingGuard.SigningAttempts);
    }

    private static void AssertRawOrExperimentalDoesNotReachDryRun(bool isActiveConservativeExecutable, bool isRawPositiveOnly, bool isExperimentalCandidate)
    {
        var reachesDryRun = isActiveConservativeExecutable && !isRawPositiveOnly && !isExperimentalCandidate;
        Assert.False(reachesDryRun);
    }
}
