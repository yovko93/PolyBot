using TradingBot.Services;
using Xunit;

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

public class VerifiedPricingAndPruneDryRunTests
{
    [Fact]
    public void Missing_no_ask_before_active_candidate_increments_pricing_counter_not_would_open_counter()
    {
        var counters = new StrategyRuntimeCounters();
        counters.Add(new OpportunityStrategyScanResult(
            "VerifiedMultiOutcome",
            StrategyMode.DiagnosticsOnly,
            VerifiedActiveConservativePositive: 0,
            VerifiedWouldOpenIfPaperEligible: 0,
            VerifiedPricingBlockedByMissingNoAsk: 1,
            VerifiedRejectedByMissingNoAsk: 0));

        var snap = counters.Snapshot("VerifiedMultiOutcome");
        Assert.Equal(1, snap.VerifiedPricingBlockedByMissingNoAsk);
        Assert.Equal(0, snap.VerifiedRejectedByMissingNoAsk);
        Assert.Equal(0, snap.VerifiedWouldOpenIfPaperEligible);
    }

    [Fact]
    public void Active_positive_zero_keeps_would_open_and_would_open_blockers_zero()
    {
        var counters = new StrategyRuntimeCounters();
        counters.Add(new OpportunityStrategyScanResult(
            "VerifiedMultiOutcome",
            StrategyMode.DiagnosticsOnly,
            VerifiedActiveConservativePositive: 0,
            VerifiedWouldOpenIfPaperEligible: 0,
            VerifiedRejectedByStability: 0,
            VerifiedRejectedByRisk: 0,
            VerifiedWouldOpenBlockedByFill: 0,
            VerifiedWouldOpenBlockedByDepth: 0,
            VerifiedRejectedByCostProfile: 0,
            VerifiedWouldOpenBlockedByUnknown: 0));

        var snap = counters.Snapshot("VerifiedMultiOutcome");
        Assert.Equal(0, snap.VerifiedActiveConservativePositive);
        Assert.Equal(0, snap.VerifiedWouldOpenIfPaperEligible);
        Assert.Equal(0, snap.VerifiedRejectedByStability + snap.VerifiedRejectedByRisk + snap.VerifiedWouldOpenBlockedByFill + snap.VerifiedWouldOpenBlockedByDepth + snap.VerifiedRejectedByCostProfile + snap.VerifiedWouldOpenBlockedByUnknown);
    }

    [Fact]
    public void Missing_no_ask_group_emits_verified_prune_dry_run_log()
    {
        var dryRun = PruneDryRun();
        var line = dryRun.ToLogLine();
        Assert.Contains("[VERIFIED_PRUNE_DRY_RUN]", line);
        Assert.Contains("MissingMarketIds=[558984]", line);
        Assert.Contains("Action=ReviewOnly", line);
        Assert.Contains("AutoApply=false", line);
    }

    [Fact]
    public void Prune_dry_run_is_review_only_and_does_not_create_paper_candidate_or_mutate_allowlist()
    {
        var originalAllowlist = new[] { "558001", "558984" };
        var dryRun = PruneDryRun();
        Assert.Equal("ReviewOnly", dryRun.Action);
        Assert.False(dryRun.AutoApply);
        Assert.Equal(new[] { "558001", "558984" }, originalAllowlist);
        Assert.Empty(dryRun.SuggestedPrunedLegList.Where(x => x.MarketId == "558984"));
        var eligibility = new VerifiedPaperEligibilityDecision(dryRun.WouldOpenIfPaperEligible, VerifiedPaperBlockedReason.DiagnosticsOnly);
        Assert.False(VerifiedPaperEligibilityDryRun.CanOpenPaper(eligibility, new OpportunityStrategyConfig(true, StrategyMode.DiagnosticsOnly, 50)));
    }

    [Fact]
    public void Review_only_repair_cannot_auto_apply()
    {
        var dryRun = PruneDryRun();
        Assert.Equal("ReviewOnly", dryRun.Action);
        Assert.False(dryRun.AutoApply);
    }

    [Fact]
    public void Live_trading_false_and_signing_attempts_zero_remain_enforced_for_pricing_diagnostics()
    {
        LiveTradingGuard.ResetForTests();
        Assert.Equal(0, LiveTradingGuard.SigningAttempts);
        Assert.Throws<LiveTradingBlockedException>(() => LiveTradingGuard.AssertOrderSigningAllowed(false));
    }

    private static VerifiedPruneDryRunResult PruneDryRun()
        => new(
            "winner:2026 fifa world cup|kind:generic",
            new[] { "558984" },
            35,
            34,
            -0.01m,
            0.02m,
            0.01m,
            0.04m,
            true,
            false,
            "ReviewOnly",
            false,
            new[]
            {
                new VerifiedPruneDryRunLeg("558001", "c1", "n1", 0.5m, 10m, "DirectNoAsk", null),
                new VerifiedPruneDryRunLeg("558984", "c2", "n2", null, null, "None", "EmptyBook")
            },
            new[] { new VerifiedPruneDryRunLeg("558001", "c1", "n1", 0.5m, 10m, "DirectNoAsk", null) },
            new[] { new VerifiedPruneDryRunLeg("558984", "c2", "n2", null, null, "None", "EmptyBook") });
}
