using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class AllowlistRepairLoggingTests
{
    [Fact]
    public void Experimental_candidates_zero_makes_best_experimental_null_and_alternate_separate()
    {
        var row = Row(active: -0.051m, experimental: -0.015m);

        var bestExperimental = ScanLogSummaryService.BestExperimentalNet(Array.Empty<VerifiedBasketScreener.ScreenResult>());
        var bestAlternate = ScanLogSummaryService.BestAlternateProfileNet([row], "PolymarketApprox");

        Assert.Null(bestExperimental);
        Assert.Equal(-0.015m, bestAlternate);
    }

    [Fact]
    public void Discovery_health_log_is_emitted_for_each_discovery_summary()
    {
        var summary = new MarketDiscoverySummary(PagesFetched: 100, ActiveMarketsAvailable: 8233, StoppedReason: "SafetyCapReached", DiscoveryCompleted: true, SafetyCapReached: true);
        var health = ScanLogSummaryService.DiscoveryHealth(summary, 8000);

        Assert.True(health.Healthy);
        Assert.False(health.Degraded);
        Assert.Equal("Full", health.ScanConfidence);
        Assert.Contains("[DISCOVERY_HEALTH]", health.ToLogLine());
        Assert.Contains("Active=8233", health.ToLogLine());
    }

    [Fact]
    public void Repair_logs_are_throttled_by_stable_hash()
    {
        var throttle = new LogThrottle();
        var first = throttle.ShouldLog("ALLOWLIST_REPAIR_REPORT", "counts|actions", true, 25);
        var second = throttle.ShouldLog("ALLOWLIST_REPAIR_REPORT", "counts|actions", true, 25);
        var changed = throttle.ShouldLog("ALLOWLIST_REPAIR_REPORT", "counts|actions2", true, 25);

        Assert.True(first);
        Assert.False(second);
        Assert.True(changed);
    }

    [Fact]
    public void Unresolved_samples_are_throttled_by_stable_hash()
    {
        var throttle = new LogThrottle();
        var first = throttle.ShouldLog("VERIFIED_UNRESOLVED_SAMPLE:g", "g|reason|rejected|ids", true, 50);
        var second = throttle.ShouldLog("VERIFIED_UNRESOLVED_SAMPLE:g", "g|reason|rejected|ids", true, 50);
        var changed = throttle.ShouldLog("VERIFIED_UNRESOLVED_SAMPLE:g", "g|reason2|rejected|ids", true, 50);

        Assert.True(first);
        Assert.False(second);
        Assert.True(changed);
    }

    [Fact]
    public void Candidate_scan_logs_are_throttled_by_stable_hash()
    {
        var throttle = new LogThrottle();
        var first = throttle.ShouldLog("MULTI_CANDIDATE_SCAN", "10|3|Top|A:1", true, 25);
        var second = throttle.ShouldLog("MULTI_CANDIDATE_SCAN", "10|3|Top|A:1", true, 25);
        var changed = throttle.ShouldLog("MULTI_CANDIDATE_SCAN", "10|4|Top|A:2", true, 25);

        Assert.True(first);
        Assert.False(second);
        Assert.True(changed);
    }

    private static VerifiedBasketScreener.ScreenResult Row(decimal active, decimal experimental) => new(
        "g",
        2,
        1m,
        0.9m,
        0.1m,
        active,
        0m,
        0m,
        "Fees",
        "Monitor",
        "PolymarketApprox",
        [new VerifiedBasketScreener.ProfileResult("Conservative", "Fixed", 0m, 0m, 0m, active, active, false, false), new VerifiedBasketScreener.ProfileResult("PolymarketApprox", "Fixed", 0m, 0m, 0m, experimental, experimental, false, false)],
        [],
        "None",
        DateTime.UtcNow,
        0m,
        false,
        "Monitor",
        [],
        experimental,
        VerifiedBasketScreener.ExecutionStatus.NotExecutable);
}
