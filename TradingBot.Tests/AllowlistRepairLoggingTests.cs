using TradingBot.Models;
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
        Assert.Equal(1, summary.Suppressed);
        Assert.Contains("Suppressed=1", summary.ToLogLine());
    }


    [Fact]
    public void Unresolved_samples_shown_matches_actual_logged_sample_count()
    {
        var throttle = new LogThrottle();
        var logged = new[]
        {
            throttle.ShouldLog("VERIFIED_UNRESOLVED_SAMPLE:g1", "g1|reason", true, 50),
            throttle.ShouldLog("VERIFIED_UNRESOLVED_SAMPLE:g2", "g2|reason", true, 50),
            throttle.ShouldLog("VERIFIED_UNRESOLVED_SAMPLE:g3", "g3|reason", true, 50)
        }.Count(x => x);

        var summary = ScanLogSummaryService.UnresolvedSampleSummary(3, logged);

        Assert.Equal(3, summary.SamplesShown);
        Assert.Equal(0, summary.Suppressed);
    }

    [Fact]
    public void Unresolved_summary_reports_suppressed_count_when_sample_is_throttled()
    {
        var throttle = new LogThrottle();
        Assert.True(throttle.ShouldLog("VERIFIED_UNRESOLVED_SAMPLE:g1", "g1|reason", true, 50));
        var logged = new[]
        {
            throttle.ShouldLog("VERIFIED_UNRESOLVED_SAMPLE:g1", "g1|reason", true, 50),
            throttle.ShouldLog("VERIFIED_UNRESOLVED_SAMPLE:g2", "g2|reason", true, 50),
            throttle.ShouldLog("VERIFIED_UNRESOLVED_SAMPLE:g3", "g3|reason", true, 50)
        }.Count(x => x);

        var summary = ScanLogSummaryService.UnresolvedSampleSummary(3, logged);

        Assert.Equal(2, summary.SamplesShown);
        Assert.Equal(1, summary.Suppressed);
        Assert.Contains("Suppressed=1", summary.ToLogLine());
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

    [Fact]
    public void Unresolved_breakdown_explains_broken_total_two_with_three_unresolved()
    {
        var rows = new[]
        {
            new VerifiedUnresolvedExportRow("broken", "VerifiedGroupIncomplete", "ConfiguredButIncomplete", "BrokenConfig", "DisableMissingMarkets", "High", true, false),
            new VerifiedUnresolvedExportRow("refresh", "VerifiedGroupMarketMismatch", "Rejected", "NeedsRefresh", "RefreshFromCandidateExport", "Medium", true, false),
            new VerifiedUnresolvedExportRow("review", "VerifiedGroupNotFoundInDiscoveredPool", "Rejected", "ReviewOnly", "NeedsManualReview", "Low", false, true)
        };

        var breakdown = ScanLogSummaryService.UnresolvedBreakdown(rows, 2);

        Assert.Equal(3, breakdown.Total);
        Assert.Equal(1, breakdown.BrokenConfig);
        Assert.Equal(1, breakdown.NeedsRefresh);
        Assert.Equal(1, breakdown.ReviewOnly);
        Assert.Equal(0, breakdown.MonitoringOnly);
        Assert.Contains("BrokenConfig=1", breakdown.ToLogLine());
        Assert.Contains("NeedsRefresh=1", breakdown.ToLogLine());
        Assert.Contains("ReviewOnly=1", breakdown.ToLogLine());
    }

    [Fact]
    public void Unresolved_export_contains_all_groups_even_when_console_samples_are_capped()
    {
        var unresolved = new[]
        {
            Resolved("g1", "Rejected", "VerifiedGroupMarketMismatch"),
            Resolved("g2", "Rejected", "VerifiedGroupConditionMismatch"),
            Resolved("g3", "ConfiguredButIncomplete", "VerifiedGroupIncomplete")
        };
        var repair = new Dictionary<string, AllowlistRepairGroup>(StringComparer.OrdinalIgnoreCase)
        {
            ["g1"] = Repair("g1", "NeedsRefresh", "RefreshFromCandidateExport", "Medium"),
            ["g2"] = Repair("g2", "ReviewOnly", "NeedsManualReview", "Low"),
            ["g3"] = Repair("g3", "BrokenConfig", "DisableMissingMarkets", "High")
        };

        var export = ScanLogSummaryService.BuildUnresolvedExport(unresolved, repair, new HashSet<string>(["g1", "g2"], StringComparer.OrdinalIgnoreCase), DateTime.UnixEpoch);

        Assert.Equal(3, export.Total);
        Assert.Equal(new[] { "g1", "g2", "g3" }, export.Groups.Select(x => x.GroupKey).ToArray());
        Assert.True(export.Groups.Single(x => x.GroupKey == "g3").IsSuppressedInConsole);
    }

    [Fact]
    public void Console_can_show_two_samples_while_export_contains_all_three()
    {
        var export = new VerifiedUnresolvedExport(DateTime.UnixEpoch, 3,
        [
            new VerifiedUnresolvedExportRow("g1", "r", "Rejected", "NeedsRefresh", "RefreshFromCandidateExport", "Medium", true, false),
            new VerifiedUnresolvedExportRow("g2", "r", "Rejected", "BrokenConfig", "DisableMissingMarkets", "High", true, false),
            new VerifiedUnresolvedExportRow("g3", "r", "Rejected", "ReviewOnly", "NeedsManualReview", "Low", false, true)
        ]);

        var summary = ScanLogSummaryService.UnresolvedSampleSummary(export.Total, export.Groups.Count(x => x.IsSampleLogged), export.Groups.Where(x => x.IsSuppressedInConsole).Select(x => x.GroupKey).ToArray());

        Assert.Equal(2, summary.SamplesShown);
        Assert.Equal(1, summary.Suppressed);
        Assert.Equal("g3", Assert.Single(summary.SuppressedGroupKeys!));
    }

    [Fact]
    public void Rejected_only_candidate_scan_is_suppressed_in_operational_quiet_mode_after_first_log()
    {
        var throttle = new LogThrottle();
        var hash = ScanLogSummaryService.CandidateScanFingerprint(19, "AutoCandidateUnverified", new Dictionary<string, int> { ["AutoCandidateUnverified"] = 19 }, 10, 0);

        Assert.True(throttle.ShouldLog("MULTI_CANDIDATE_SCAN", hash, true, 50));
        var throttleWouldLog = throttle.ShouldLog("MULTI_CANDIDATE_SCAN", hash, true, 50);
        var suppressedByQuietMode = ScanLogSummaryService.ShouldSuppressRejectedOnlyCandidateScan(true, false, true, hash, hash, periodic: false);

        Assert.False(throttleWouldLog);
        Assert.True(suppressedByQuietMode);
    }

    [Fact]
    public void Top_reject_material_change_logs_once()
    {
        var throttle = new LogThrottle();
        var first = ScanLogSummaryService.CandidateScanFingerprint(19, "AutoCandidateUnverified", new Dictionary<string, int> { ["AutoCandidateUnverified"] = 19 }, 10, 0);
        var changed = ScanLogSummaryService.CandidateScanFingerprint(19, "AutoCandidatePartialOverlap", new Dictionary<string, int> { ["AutoCandidatePartialOverlap"] = 19 }, 10, 0);

        Assert.True(throttle.ShouldLog("MULTI_CANDIDATE_SCAN", first, true, 50));
        Assert.True(throttle.ShouldLog("MULTI_CANDIDATE_SCAN", changed, true, 50));
        Assert.False(throttle.ShouldLog("MULTI_CANDIDATE_SCAN", changed, true, 50));
    }

    [Fact]
    public void Profile_comparison_classification_change_logs()
    {
        var throttle = new LogThrottle();
        var first = ScanLogSummaryService.ProfileComparisonFingerprint([Row(active: -0.003m, experimental: -0.001m, classification: "Monitor")], 0.005m);
        var changed = ScanLogSummaryService.ProfileComparisonFingerprint([Row(active: -0.003m, experimental: -0.001m, classification: "NearExecutable")], 0.005m);

        Assert.True(throttle.ShouldLog("PROFILE_COMPARISON", first, true, 50));
        Assert.True(throttle.ShouldLog("PROFILE_COMPARISON", changed, true, 50));
    }

    [Fact]
    public void Patchable_zero_noop_repair_suggestion_logs_only_once()
    {
        var throttle = new LogThrottle();
        var fingerprint = "winner:2026 peruvian presidential election|kind:person|winner:2026 peruvian presidential election|kind:person|1|NoDiff";

        Assert.True(throttle.ShouldLog("ALLOWLIST_REPAIR_NOOP:winner:2026 peruvian presidential election|kind:person", fingerprint, true, 0));
        Assert.False(throttle.ShouldLog("ALLOWLIST_REPAIR_NOOP:winner:2026 peruvian presidential election|kind:person", fingerprint, true, 0));
    }

    [Fact]
    public void Peru_exact_no_diff_repair_match_is_repair_noop()
    {
        var match = new AllowlistRepairMatch(
            "winner:2026 peruvian presidential election|kind:person",
            1m,
            1m,
            1m,
            1m,
            1m,
            0,
            0,
            Array.Empty<string>(),
            Array.Empty<string>(),
            0,
            "High");

        var repairNoOp = match.Score >= 1m && match.AddedMarketIds.Count == 0 && match.RemovedMarketIds.Count == 0;

        Assert.True(repairNoOp);
    }

    private static VerifiedBasketScreener.ScreenResult Row(decimal active, decimal experimental, string classification = "Monitor") => new(
        "g",
        2,
        1m,
        0.9m,
        0.1m,
        active,
        0m,
        0m,
        "Fees",
        classification,
        "PolymarketApprox",
        [new VerifiedBasketScreener.ProfileResult("Conservative", "Fixed", 0m, 0m, 0m, active, active, false, false), new VerifiedBasketScreener.ProfileResult("PolymarketApprox", "Fixed", 0m, 0m, 0m, experimental, experimental, false, false)],
        [],
        "None",
        DateTime.UtcNow,
        0m,
        false,
        classification,
        [],
        experimental,
        VerifiedBasketScreener.ExecutionStatus.NotExecutable);

    private static ResolvedVerifiedGroup Resolved(string groupKey, string status, string reason) => new(groupKey, groupKey, [groupKey + "-m"], [], [], [groupKey + "-m"], [], status, reason);

    private static AllowlistRepairGroup Repair(string groupKey, string healthCategory, string action, string confidence) => new(
        "snapshot",
        1,
        null,
        action,
        DateTime.UnixEpoch,
        "initial",
        groupKey,
        groupKey,
        true,
        "Rejected",
        healthCategory,
        false,
        false,
        1,
        0,
        1,
        [groupKey + "-m"],
        0,
        0,
        [],
        "mismatch",
        null,
        action,
        confidence,
        "reason",
        null,
        null,
        null,
        0,
        [],
        "review",
        "copy");
}
