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
    public void Candidate_scan_small_count_changes_do_not_log_every_cycle()
    {
        var throttle = new LogThrottle();
        var first = throttle.ShouldLog("MULTI_CANDIDATE_SCAN", "1|1|Top|A:1|exec:0", true, 25);
        var second = throttle.ShouldLog("MULTI_CANDIDATE_SCAN", "1|1|Top|A:1|exec:0", true, 25);
        var smallCountChangeSameBucket = throttle.ShouldLog("MULTI_CANDIDATE_SCAN", "1|1|Top|A:1|exec:0", true, 25);
        var materialChange = throttle.ShouldLog("MULTI_CANDIDATE_SCAN", "2|2|Top|A:2|exec:0", true, 25);

        Assert.True(first);
        Assert.False(second);
        Assert.False(smallCountChangeSameBucket);
        Assert.True(materialChange);
    }


    [Fact]
    public void Profile_comparison_logs_are_throttled_by_stable_hash()
    {
        var throttle = new LogThrottle();
        var first = throttle.ShouldLog("PROFILE_COMPARISON", "g|gross|active|poly|raw|classification", true, 25);
        var second = throttle.ShouldLog("PROFILE_COMPARISON", "g|gross|active|poly|raw|classification", true, 25);
        var changed = throttle.ShouldLog("PROFILE_COMPARISON", "g|gross|active2|poly|raw|classification", true, 25);

        Assert.True(first);
        Assert.False(second);
        Assert.True(changed);
    }



    [Fact]
    public void Unresolved_summary_reports_more_when_sample_limit_is_lower_than_total()
    {
        var summary = ScanLogSummaryService.UnresolvedSampleSummary(3, 2);

        Assert.Equal(3, summary.Total);
        Assert.Equal(2, summary.SamplesShown);
        Assert.Equal(1, summary.More);
        Assert.Contains("More=1", summary.ToLogLine());
    }

    [Fact]
    public void Candidate_scan_fingerprint_buckets_small_count_changes()
    {
        var throttle = new LogThrottle();
        var firstHash = ScanLogSummaryService.CandidateScanFingerprint(8391, "Safety", new Dictionary<string, int> { ["A"] = 14 }, 10);
        var smallHash = ScanLogSummaryService.CandidateScanFingerprint(8394, "Safety", new Dictionary<string, int> { ["A"] = 19 }, 10);

        Assert.True(throttle.ShouldLog("MULTI_CANDIDATE_SCAN", firstHash, true, 25));
        Assert.False(throttle.ShouldLog("MULTI_CANDIDATE_SCAN", smallHash, true, 25));
    }

    [Fact]
    public void Profile_comparison_fingerprint_buckets_unchanged_values()
    {
        var throttle = new LogThrottle();
        var firstHash = ScanLogSummaryService.ProfileComparisonFingerprint([Row(active: -0.0030m, experimental: -0.0010m)], 0.002m);
        var smallHash = ScanLogSummaryService.ProfileComparisonFingerprint([Row(active: -0.0031m, experimental: -0.0011m)], 0.002m);

        Assert.True(throttle.ShouldLog("PROFILE_COMPARISON", firstHash, true, 25));
        Assert.False(throttle.ShouldLog("PROFILE_COMPARISON", smallHash, true, 25));
    }

    [Fact]
    public void Allowlist_startup_validation_counts_unique_keys()
    {
        var summary = ScanLogSummaryService.AllowlistConfigValidation([
            new TradingBot.Services.VerifiedMultiOutcomeGroupConfig(true, "g1", "G1", ["m1"], [], 1, "Verified"),
            new TradingBot.Services.VerifiedMultiOutcomeGroupConfig(true, "g2", "G2", ["m2"], [], 1, "Verified")
        ]);

        Assert.Equal(2, summary.Total);
        Assert.Equal(2, summary.UniqueGroupKeys);
        Assert.Equal(0, summary.DuplicateGroupKeys);
        Assert.Contains("DuplicateGroupKeys=0", summary.ToLogLine());
    }

    [Fact]
    public void Ranking_logs_are_throttled_by_stable_hash()
    {
        var throttle = new LogThrottle();
        var first = throttle.ShouldLog("VERIFIED_BASKET_RANKING", "5|1|best|Monitor|0", true, 25);
        var second = throttle.ShouldLog("VERIFIED_BASKET_RANKING", "5|1|best|Monitor|0", true, 25);
        var changed = throttle.ShouldLog("VERIFIED_BASKET_RANKING", "5|2|best|Monitor|0", true, 25);

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
