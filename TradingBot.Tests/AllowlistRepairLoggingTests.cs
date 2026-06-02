using System.Text.Json;
using System.Text.Json.Nodes;
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
        var baseFingerprint = ScanLogSummaryService.CandidateScanFingerprint(22, 0, 0, "Top", new Dictionary<string, int> { ["A"] = 22 }, 10);
        var smallChangeFingerprint = ScanLogSummaryService.CandidateScanFingerprint(27, 0, 0, "Top", new Dictionary<string, int> { ["A"] = 27 }, 10);
        var materialChangeFingerprint = ScanLogSummaryService.CandidateScanFingerprint(32, 0, 0, "Top", new Dictionary<string, int> { ["A"] = 32 }, 10);

        Assert.Equal(baseFingerprint, smallChangeFingerprint);
        Assert.NotEqual(baseFingerprint, materialChangeFingerprint);
        Assert.True(throttle.ShouldLog("MULTI_CANDIDATE_SCAN", baseFingerprint, true, 25));
        Assert.False(throttle.ShouldLog("MULTI_CANDIDATE_SCAN", smallChangeFingerprint, true, 25));
        Assert.True(throttle.ShouldLog("MULTI_CANDIDATE_SCAN", materialChangeFingerprint, true, 25));
    }


    [Fact]
    public void Profile_comparison_unchanged_values_are_throttled()
    {
        var throttle = new LogThrottle();
        var firstFingerprint = ScanLogSummaryService.ProfileComparisonFingerprint([Row(active: -0.0030m, experimental: -0.0010m)], 0.002m);
        var tinyChangeFingerprint = ScanLogSummaryService.ProfileComparisonFingerprint([Row(active: -0.0029m, experimental: -0.0009m)], 0.002m);
        var materialChangeFingerprint = ScanLogSummaryService.ProfileComparisonFingerprint([Row(active: 0.0015m, experimental: -0.0010m)], 0.002m);

        Assert.Equal(firstFingerprint, tinyChangeFingerprint);
        Assert.NotEqual(firstFingerprint, materialChangeFingerprint);
        Assert.True(throttle.ShouldLog("PROFILE_COMPARISON", firstFingerprint, true, 25));
        Assert.False(throttle.ShouldLog("PROFILE_COMPARISON", tinyChangeFingerprint, true, 25));
        Assert.True(throttle.ShouldLog("PROFILE_COMPARISON", materialChangeFingerprint, true, 25));
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


    [Fact]
    public void Unresolved_summary_reports_more_when_sample_limited()
    {
        var summary = ScanLogSummaryService.UnresolvedSummaryLog(total: 3, samplesShown: 2);

        Assert.Equal("[VERIFIED_UNRESOLVED_SUMMARY] Total=3 SamplesShown=2 More=1", summary);
    }

    [Fact]
    public void Allowlist_startup_validation_logs_unique_duplicate_counts()
    {
        var json = new JsonArray(Enumerable.Range(1, 11)
            .Select(i => (JsonNode)new JsonObject { ["enabled"] = true, ["groupKey"] = $"g{i}" })
            .ToArray());
        var doc = JsonDocument.Parse(json.ToJsonString());

        var summary = MutuallyExclusiveGroupValidator.ValidateAllowlistConfig(doc.RootElement);

        Assert.Equal("[ALLOWLIST_CONFIG_VALIDATION] Total=11 UniqueGroupKeys=11 DuplicateGroupKeys=0 Enabled=11 Disabled=0", summary.ToLogLine());
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
