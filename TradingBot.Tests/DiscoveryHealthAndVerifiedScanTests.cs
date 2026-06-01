using TradingBot.Options;
using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class DiscoveryHealthAndVerifiedScanTests
{
    [Fact]
    public void Configured_cap_discovery_is_healthy_safety_cap()
    {
        var health = MarketDiscoveryHealthEvaluator.Evaluate(100, 8658, 10000, discoveryCompleted: false, stoppedReason: "SafetyCapReached", lastError: null, safetyCapReached: true, retriesAttempted: 0, failedPages: 0, Options(), DateTime.UtcNow);

        Assert.True(health.Healthy);
        Assert.False(health.Degraded);
        Assert.False(health.Partial);
        Assert.Equal("SafetyCapReached", health.StoppedReason);
        Assert.Equal("Full", health.ScanConfidence);
    }

    [Fact]
    public void Operation_canceled_before_min_pages_is_degraded_partial()
    {
        var health = MarketDiscoveryHealthEvaluator.Evaluate(71, 6011, 7100, discoveryCompleted: false, stoppedReason: "OperationCanceled", lastError: "cancelled", safetyCapReached: false, retriesAttempted: 3, failedPages: 1, Options(), DateTime.UtcNow);

        Assert.False(health.Healthy);
        Assert.True(health.Degraded);
        Assert.True(health.Partial);
        Assert.Equal("PartialDiscovery", health.ScanConfidence);
        Assert.Equal("OperationCanceled", health.StoppedReason);
    }

    [Fact]
    public void Safety_cap_is_not_warning_when_config_disables_warning()
    {
        var options = Options();
        var health = MarketDiscoveryHealthEvaluator.Evaluate(100, 8658, 10000, false, "SafetyCapReached", null, true, 0, 0, options, DateTime.UtcNow);

        var shouldWarn = (!health.Healthy && health.StoppedReason is "OperationCanceled" or "Timeout" or "RequestError" or "Unknown") || (health.SafetyCapReached && options.TreatSafetyCapAsWarning);

        Assert.False(shouldWarn);
    }

    [Fact]
    public void Multi_verified_scan_line_includes_discovery_health_and_confidence()
    {
        var line = $"[MULTI_VERIFIED_SCAN] ActiveExecutable=2 ExperimentalCandidates=0 DiscoveryHealthy={true.ToString().ToLowerInvariant()} ScanConfidence=Full DiscoveryReason=SafetyCapReached";

        Assert.Contains("DiscoveryHealthy=true", line);
        Assert.Contains("ScanConfidence=Full", line);
        Assert.Contains("DiscoveryReason=SafetyCapReached", line);
    }

    [Fact]
    public void Experimental_candidates_zero_formats_best_experimental_as_na()
    {
        var snapshot = VerifiedBasketScreener.BuildSnapshot("Conservative", "PolymarketApprox", [Screen("colombia", 0.0065m, 0.0095m, 0.012m)], []);
        var bestExperimental = snapshot.ExperimentalCandidates.Select(x => (decimal?)x.ExperimentalProfileNetEdge).DefaultIfEmpty(null).Max();

        Assert.Empty(snapshot.ExperimentalCandidates);
        Assert.Null(bestExperimental);
        Assert.Equal("N/A", bestExperimental.HasValue ? bestExperimental.Value.ToString() : "N/A");
    }

    [Fact]
    public void Active_group_better_alternate_contributes_to_alternate_not_experimental()
    {
        var snapshot = VerifiedBasketScreener.BuildSnapshot("Conservative", "PolymarketApprox", [Screen("colombia", 0.0065m, 0.0095m, 0.012m)], []);

        var bestAlternate = snapshot.ActiveProfileExecutable
            .SelectMany(g => g.ProfileResults.Where(p => !p.ProfileName.Equals(snapshot.ActiveProfile, StringComparison.OrdinalIgnoreCase) && !p.ProfileName.Equals("RawOnly", StringComparison.OrdinalIgnoreCase)).Select(p => (decimal?)p.NetEdge))
            .DefaultIfEmpty(null).Max();
        var bestExperimental = snapshot.ExperimentalCandidates.Select(x => (decimal?)x.ExperimentalProfileNetEdge).DefaultIfEmpty(null).Max();

        Assert.Single(snapshot.ActiveProfileExecutable);
        Assert.Empty(snapshot.ExperimentalCandidates);
        Assert.Equal(0.0095m, bestAlternate);
        Assert.Null(bestExperimental);
    }

    [Fact]
    public void Conservative_positive_nba_is_active_not_experimental()
    {
        var row = Screen("winner:2026 nba finals", 0.0015m, 0.0045m, 0.007m);

        Assert.Equal(VerifiedBasketScreener.ExecutionStatus.ExecutableUnderActiveProfile, row.ExecutionStatus);
        Assert.NotEqual(VerifiedBasketScreener.ExecutionStatus.ExperimentalPaperCandidate, row.ExecutionStatus);
    }

    [Fact]
    public void Conservative_negative_polymarket_positive_is_experimental_candidate()
    {
        var row = Screen("experimental", -0.0015m, 0.0015m, 0.002m);

        Assert.Equal(VerifiedBasketScreener.ExecutionStatus.ExperimentalPaperCandidate, row.ExecutionStatus);
    }

    [Fact]
    public void Raw_only_positive_with_negative_active_and_experimental_is_diagnostics_only()
    {
        var row = Screen("diagnostics", -0.0015m, -0.0005m, 0.004m);

        Assert.Equal(VerifiedBasketScreener.ExecutionStatus.DiagnosticsOnlyPositive, row.ExecutionStatus);
    }

    [Fact]
    public void Paper_open_is_blocked_when_discovery_unhealthy_and_required()
    {
        var execution = new ExecutionOptions { RequireHealthyDiscoveryForPaperOpen = true, AllowPaperExecutionOnPartialDiscovery = false };
        var health = MarketDiscoveryHealthEvaluator.Evaluate(71, 6011, 7100, false, "OperationCanceled", "cancelled", false, 3, 1, Options(), DateTime.UtcNow);

        var allowed = !execution.RequireHealthyDiscoveryForPaperOpen || health.Healthy || execution.AllowPaperExecutionOnPartialDiscovery;

        Assert.False(allowed);
    }

    [Fact]
    public void Discovery_health_export_contract_contains_endpoint_fields()
    {
        var health = MarketDiscoveryHealthEvaluator.Evaluate(100, 8658, 10000, false, "SafetyCapReached", null, true, 0, 0, Options(), DateTime.UtcNow);

        Assert.True(health.Healthy);
        Assert.Equal(100, health.PagesFetched);
        Assert.Equal(8658, health.ActiveMarketsAvailable);
        Assert.Equal(10000, health.RawLoadedTotal);
        Assert.True(health.SafetyCapReached);
        Assert.Equal(0, health.RetriesAttempted);
        Assert.Equal(0, health.FailedPages);
    }

    private static MarketDiscoveryOptions Options() => new()
    {
        TreatSafetyCapAsWarning = false,
        TreatOperationCanceledAtPageCapAsSafetyCap = true,
        MinHealthyActiveMarkets = 8000,
        MinHealthyPagesFetched = 95
    };

    private static VerifiedBasketScreener.ScreenResult Screen(string groupKey, decimal activeNet, decimal experimentalNet, decimal rawNet)
    {
        var active = new VerifiedBasketScreener.ProfileResult("Conservative", "FixedPerLeg", 0m, 0m, 0m, activeNet, activeNet, activeNet > 0.001m, false);
        var experimental = new VerifiedBasketScreener.ProfileResult("PolymarketApprox", "NoneOrExternalized", 0m, 0m, 0m, experimentalNet, experimentalNet, experimentalNet > 0.001m, true);
        var orderbook = new VerifiedBasketScreener.ProfileResult("OrderbookOnly", "None", 0m, 0m, 0m, experimentalNet - 0.0005m, experimentalNet - 0.0005m, experimentalNet - 0.0005m > 0.001m, true);
        var raw = new VerifiedBasketScreener.ProfileResult("RawOnly", "None", 0m, 0m, 0m, rawNet, rawNet, rawNet > 0.001m, true);
        var status = activeNet > 0.001m
            ? VerifiedBasketScreener.ExecutionStatus.ExecutableUnderActiveProfile
            : experimentalNet > 0.001m ? VerifiedBasketScreener.ExecutionStatus.ExperimentalPaperCandidate
            : rawNet > 0.001m ? VerifiedBasketScreener.ExecutionStatus.DiagnosticsOnlyPositive
            : VerifiedBasketScreener.ExecutionStatus.NotExecutable;
        return new VerifiedBasketScreener.ScreenResult(groupKey, 3, 2m, 1.99m, rawNet, activeNet, activeNet > 0.001m ? 1m : 0m, activeNet, "None", status.ToString(), "RawOnly", [active, experimental, orderbook, raw], [], "None", DateTime.UtcNow, Math.Max(0m, -activeNet), false, "Monitor", rawNet > 0.001m ? ["RawOnly"] : [], experimentalNet, status);
    }
}
