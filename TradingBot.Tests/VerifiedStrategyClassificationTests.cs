using TradingBot.Api;
using TradingBot.Options;
using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class VerifiedStrategyClassificationTests
{
    [Fact]
    public void Raw_positive_only_increments_raw_positive_not_diagnostics_blocked()
    {
        var result = ScanResult(Screen(raw: 0.02m, active: -0.01m));
        var state = new BotRuntimeState();
        state.RecordStrategyResult(result);

        var counters = state.StrategyCountersSnapshot()["VerifiedMultiOutcome"];
        Assert.Equal(1, counters.VerifiedRawPositiveOnly);
        Assert.Equal(0, counters.VerifiedDiagnosticsOnlyBlocked);
    }

    [Fact]
    public void Alternate_profile_positive_increments_alternate_not_diagnostics_blocked()
    {
        var result = ScanResult(Screen(poly: 0.02m, raw: -0.01m, active: -0.01m));
        var state = new BotRuntimeState();
        state.RecordStrategyResult(result);

        var counters = state.StrategyCountersSnapshot()["VerifiedMultiOutcome"];
        Assert.Equal(1, counters.VerifiedAlternateProfilePositive);
        Assert.Equal(0, counters.VerifiedDiagnosticsOnlyBlocked);
    }

    [Fact]
    public void Experimental_profile_candidate_increments_experimental_not_diagnostics_blocked()
    {
        var screen = Screen(poly: 0.02m, raw: -0.01m, active: -0.01m, status: VerifiedBasketScreener.ExecutionStatus.ExperimentalPaperCandidate);
        var result = ScanResult(screen);
        var state = new BotRuntimeState();
        state.RecordStrategyResult(result);

        var counters = state.StrategyCountersSnapshot()["VerifiedMultiOutcome"];
        Assert.Equal(1, counters.VerifiedExperimentalProfileCandidate);
        Assert.Equal(0, counters.VerifiedDiagnosticsOnlyBlocked);
    }

    [Fact]
    public void Conservative_active_executable_diagnostics_only_increments_blocked_when_would_open()
    {
        var screen = Screen(active: 0.02m, status: VerifiedBasketScreener.ExecutionStatus.ExecutableUnderActiveProfile);
        var classified = VerifiedStrategyClassifier.Classify(screen, Options(), stable: true, wouldPassPaperRisk: true, wouldPassFill: true);
        Assert.True(classified.WouldOpenIfPaperEligible);

        var state = new BotRuntimeState();
        state.RecordStrategyResult(ScanResult(screen, wouldOpen: 1));
        var counters = state.StrategyCountersSnapshot()["VerifiedMultiOutcome"];
        Assert.Equal(1, counters.VerifiedDiagnosticsOnlyBlocked);
    }

    [Fact]
    public void Would_open_requires_active_stable_risk_and_fill()
    {
        var screen = Screen(active: 0.02m, status: VerifiedBasketScreener.ExecutionStatus.ExecutableUnderActiveProfile);
        Assert.False(VerifiedStrategyClassifier.Classify(screen, Options(), stable: false, wouldPassPaperRisk: true, wouldPassFill: true).WouldOpenIfPaperEligible);
        Assert.False(VerifiedStrategyClassifier.Classify(screen, Options(), stable: true, wouldPassPaperRisk: false, wouldPassFill: true).WouldOpenIfPaperEligible);
        Assert.False(VerifiedStrategyClassifier.Classify(screen, Options(), stable: true, wouldPassPaperRisk: true, wouldPassFill: false).WouldOpenIfPaperEligible);
        Assert.True(VerifiedStrategyClassifier.Classify(screen, Options(), stable: true, wouldPassPaperRisk: true, wouldPassFill: true).WouldOpenIfPaperEligible);
    }

    [Fact]
    public void Verified_diagnostics_only_never_records_paper_opened()
    {
        var result = ScanResult(Screen(active: 0.02m), wouldOpen: 1) with { PaperOpened = 0 };
        Assert.Equal(StrategyMode.DiagnosticsOnly, result.Mode);
        Assert.Equal(0, result.PaperOpened);
    }

    [Fact]
    public void Runtime_health_includes_verified_classification_counters()
    {
        var state = new BotRuntimeState();
        state.RecordStrategyResult(ScanResult(Screen(raw: 0.02m, active: -0.01m)));
        var health = RuntimeHealthSnapshot.From(state);
        var line = health.ToLogLine();
        Assert.Contains("rawPositiveOnly=1", line);
        Assert.Contains("wouldOpenIfPaperEligible=0", line);
    }

    [Fact]
    public void Soak_status_includes_compact_verified_classification()
    {
        var state = new BotRuntimeState();
        state.RecordStrategyResult(ScanResult(Screen(active: 0.02m), wouldOpen: 1));
        var line = RuntimeHealthTrendTracker.ToSoakStatusLogLine(RuntimeHealthSnapshot.From(state), new RuntimeHealthTrend(1, 1, 0, 0, true, 1), new TradingBotOptions(), state);
        Assert.Contains("VerifiedMultiOutcome:DiagnosticsOnly:activePositive=1:wouldOpen=1:", line);
        Assert.Contains("diagBlocked=1", line);
    }

    [Fact]
    public void Live_trading_false_and_signing_attempts_zero_remain_enforced()
    {
        var options = new TradingBotOptions();
        Assert.False(options.TradingMode.LiveTradingEnabled);
        Assert.Equal(0, LiveTradingGuard.SigningAttempts);
    }

    [Fact]
    public void Strategy_diagnostics_only_is_throttled_for_repeated_verified_results()
    {
        var options = new TradingBotOptions();
        options.Strategies["VerifiedMultiOutcome"] = new OpportunityStrategyConfig(true, StrategyMode.DiagnosticsOnly, 50);
        var orchestrator = new StrategyOrchestrator(Array.Empty<IOpportunityStrategy>(), options);
        using var writer = new StringWriter();
        var original = Console.Out;
        Console.SetOut(writer);
        try
        {
            var result = ScanResult(Screen(active: 0.02m), wouldOpen: 1);
            orchestrator.RecordExternalResult(result, 1);
            orchestrator.RecordExternalResult(result, 1);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Equal(1, Count(writer.ToString(), "[STRATEGY_DIAGNOSTICS_ONLY] Strategy=VerifiedMultiOutcome CandidatesSuppressed=1"));
    }

    [Fact]
    public void Diagnostics_only_summary_emits_suppressed_counts()
    {
        var options = new TradingBotOptions();
        options.Strategies["VerifiedMultiOutcome"] = new OpportunityStrategyConfig(true, StrategyMode.DiagnosticsOnly, 50);
        var orchestrator = new StrategyOrchestrator(Array.Empty<IOpportunityStrategy>(), options);
        using var writer = new StringWriter();
        var original = Console.Out;
        Console.SetOut(writer);
        try
        {
            orchestrator.RecordExternalResult(ScanResult(Screen(active: 0.02m), wouldOpen: 1), 1);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains("[STRATEGY_DIAGNOSTICS_ONLY_SUMMARY] Strategy=VerifiedMultiOutcome CandidatesSuppressed=1", writer.ToString());
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }

    private static MultiOutcomeArbitrageOptions Options() => new() { MinMultiOutcomeEdge = 0.01m };

    private static OpportunityStrategyScanResult ScanResult(VerifiedBasketScreener.ScreenResult screen, int wouldOpen = 0)
    {
        var classified = VerifiedStrategyClassifier.Classify(screen, Options(), stable: wouldOpen > 0, wouldPassPaperRisk: wouldOpen > 0, wouldPassFill: wouldOpen > 0);
        return new OpportunityStrategyScanResult(
            "VerifiedMultiOutcome",
            StrategyMode.DiagnosticsOnly,
            Scanned: 1,
            Candidates: 1,
            DiagnosticsOnlyBlocked: wouldOpen,
            PositiveEdges: classified.DiagnosticsPositive ? 1 : 0,
            ExecutionReady: wouldOpen,
            VerifiedActiveConservativePositive: classified.ActiveConservativePositive ? 1 : 0,
            VerifiedActiveConservativeExecutable: classified.ActiveConservativeExecutable ? 1 : 0,
            VerifiedRawPositiveOnly: classified.RawPositiveOnly ? 1 : 0,
            VerifiedAlternateProfilePositive: classified.AlternateProfilePositive ? 1 : 0,
            VerifiedExperimentalProfileCandidate: classified.ExperimentalProfileCandidate ? 1 : 0,
            VerifiedDiagnosticsOnlyBlocked: wouldOpen,
            VerifiedWouldOpenIfPaperEligible: wouldOpen);
    }

    private static VerifiedBasketScreener.ScreenResult Screen(decimal active = 0m, decimal raw = -0.01m, decimal poly = -0.01m, VerifiedBasketScreener.ExecutionStatus? status = null)
    {
        var profiles = new[]
        {
            new VerifiedBasketScreener.ProfileResult("Conservative", "test", 0, 0, 0, active, active, active > 0.01m, false),
            new VerifiedBasketScreener.ProfileResult("RawOnly", "test", 0, 0, 0, raw, raw, raw > 0.01m, true),
            new VerifiedBasketScreener.ProfileResult("PolymarketApprox", "test", 0, 0, 0, poly, poly, poly > 0.01m, true)
        };
        return new VerifiedBasketScreener.ScreenResult("g", 2, 1, 0.99m, 0.01m, active, active > 0.01m ? 1 : 0, active, "None", "Test", "Conservative", profiles, Array.Empty<VerifiedBasketScreener.QuantityScenarioResult>(), "None", DateTime.UtcNow, 0, false, "Monitor", Array.Empty<string>(), poly, status ?? (active > 0.01m ? VerifiedBasketScreener.ExecutionStatus.ExecutableUnderActiveProfile : VerifiedBasketScreener.ExecutionStatus.DiagnosticsOnlyPositive));
    }
}
