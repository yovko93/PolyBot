using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Services.MultiOutcome;

public sealed record DiscoveryHealthSummary(bool Healthy, bool Degraded, int Pages, int Active, string Reason, string ScanConfidence, int ExpectedMinActive)
{
    public string ToLogLine() => $"[DISCOVERY_HEALTH] Healthy={Healthy.ToString().ToLowerInvariant()} Degraded={Degraded.ToString().ToLowerInvariant()} Pages={Pages} Active={Active} Reason={Reason} ScanConfidence={ScanConfidence} ExpectedMinActive={ExpectedMinActive}";
}

public sealed record VerifiedUnresolvedSampleSummary(int Total, int SamplesShown, int Suppressed, IReadOnlyList<string>? SuppressedGroupKeys = null)
{
    public string ToLogLine() => $"[VERIFIED_UNRESOLVED_SUMMARY] Total={Total} SamplesShown={SamplesShown} Suppressed={Suppressed}";
}

public sealed record VerifiedUnresolvedBreakdown(int Total, int BrokenConfig, int NeedsRefresh, int ReviewOnly, int MonitoringOnly, int SamplesShown, int Suppressed)
{
    public string ToLogLine() => $"[VERIFIED_UNRESOLVED_BREAKDOWN] Total={Total} BrokenConfig={BrokenConfig} NeedsRefresh={NeedsRefresh} ReviewOnly={ReviewOnly} MonitoringOnly={MonitoringOnly} SamplesShown={SamplesShown} Suppressed={Suppressed}";
}

public sealed record VerifiedUnresolvedExportRow(string GroupKey, string Reason, string ValidationStatus, string HealthCategory, string RecommendedAction, string RepairConfidence, bool IsSampleLogged, bool IsSuppressedInConsole);

public sealed record VerifiedUnresolvedExport(DateTime Timestamp, int Total, IReadOnlyList<VerifiedUnresolvedExportRow> Groups);

public sealed record AllowlistConfigValidationSummary(int Total, int UniqueGroupKeys, int DuplicateGroupKeys, int Enabled, int Disabled)
{
    public string ToLogLine() => $"[ALLOWLIST_CONFIG_VALIDATION] Total={Total} UniqueGroupKeys={UniqueGroupKeys} DuplicateGroupKeys={DuplicateGroupKeys} Enabled={Enabled} Disabled={Disabled}";
}

public static class ScanLogSummaryService
{
    public static DiscoveryHealthSummary DiscoveryHealth(MarketDiscoverySummary summary, int expectedMinActive)
    {
        var healthy = summary.ActiveMarketsAvailable >= expectedMinActive && string.IsNullOrWhiteSpace(summary.LastDiscoveryError);
        var reason = summary.StoppedReason ?? (summary.SafetyCapReached ? "SafetyCapReached" : summary.DiscoveryCompleted ? "Completed" : "Incomplete");
        var confidence = healthy ? "Full" : summary.ActiveMarketsAvailable > 0 ? "Partial" : "None";
        return new DiscoveryHealthSummary(healthy, !healthy, summary.PagesFetched, summary.ActiveMarketsAvailable, reason, confidence, expectedMinActive);
    }

    public static decimal? BestExperimentalNet(IReadOnlyList<VerifiedBasketScreener.ScreenResult> experimentalCandidates)
        => experimentalCandidates.Count == 0 ? null : experimentalCandidates.Max(x => x.ExperimentalProfileNetEdge);

    public static decimal? BestAlternateProfileNet(IReadOnlyList<VerifiedBasketScreener.ScreenResult> baskets, string profileName)
    {
        var values = baskets.Select(x => x.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals(profileName, StringComparison.OrdinalIgnoreCase))?.NetEdge).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        return values.Length == 0 ? null : values.Max();
    }

    public static string CandidateScanFingerprint(int candidateCount, string topReject, IReadOnlyDictionary<string, int> rejectedByReason, int countBucketSize, int executableAutoCandidates = 0)
    {
        var bucket = Math.Max(1, countBucketSize);
        var materialCountBucket = (int)Math.Floor(candidateCount / (decimal)(bucket * 2));
        var materialBucket = bucket * 2;
        var reasonBuckets = string.Join(",", rejectedByReason.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{(int)Math.Floor(x.Value / (decimal)materialBucket)}"));
        var executableBucket = executableAutoCandidates > 0 ? $"exec:{executableAutoCandidates}" : "exec:0";
        return $"count2:{materialCountBucket}|top:{topReject}|reasons:{reasonBuckets}|{executableBucket}";
    }


    public static bool ShouldSuppressRejectedOnlyCandidateScan(bool operationalQuietMode, bool logCandidateScanWhenOnlyRejected, bool rejectedOnlyCandidateScan, string currentFingerprint, string lastFingerprint, bool periodic)
        => operationalQuietMode
            && !logCandidateScanWhenOnlyRejected
            && rejectedOnlyCandidateScan
            && currentFingerprint.Equals(lastFingerprint, StringComparison.OrdinalIgnoreCase)
            && !periodic;

    public static string ProfileComparisonFingerprint(IReadOnlyList<VerifiedBasketScreener.ScreenResult> rows, decimal netDelta)
    {
        var bucket = netDelta <= 0m ? 0.005m : netDelta;
        static long EdgeBucket(decimal value, decimal bucket) => (long)Math.Round(value / bucket, MidpointRounding.AwayFromZero);
        var best = rows.FirstOrDefault();
        var activeExecutableCount = rows.Count(x => x.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.ExecutableUnderActiveProfile);
        if (best is null) return $"empty|activeExec:{activeExecutableCount}";
        var conservative = best.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("Conservative", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? best.ActiveProfileNetEdge;
        var poly = best.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m;
        return $"best:{best.GroupKey}|class:{best.Classification}|activeExec:{activeExecutableCount}|gross:{EdgeBucket(best.GrossEdge, bucket)}|conservative:{EdgeBucket(conservative, bucket)}|poly:{EdgeBucket(poly, bucket)}";
    }

    public static VerifiedUnresolvedSampleSummary UnresolvedSampleSummary(int total, int samplesShown, IReadOnlyList<string>? suppressedGroupKeys = null)
        => new(total, Math.Max(0, samplesShown), Math.Max(0, total - Math.Max(0, samplesShown)), suppressedGroupKeys ?? Array.Empty<string>());

    public static VerifiedUnresolvedBreakdown UnresolvedBreakdown(IReadOnlyList<VerifiedUnresolvedExportRow> groups, int samplesShown)
    {
        var total = groups.Count;
        var brokenConfig = groups.Count(x => x.HealthCategory.Equals("BrokenConfig", StringComparison.OrdinalIgnoreCase));
        var needsRefresh = groups.Count(x => x.HealthCategory.Equals("NeedsRefresh", StringComparison.OrdinalIgnoreCase));
        var monitoringOnly = groups.Count(x => x.HealthCategory.Equals("MonitoringOnly", StringComparison.OrdinalIgnoreCase));
        var reviewOnly = groups.Count(x =>
            (x.HealthCategory.Equals("ReviewOnly", StringComparison.OrdinalIgnoreCase) || x.RecommendedAction.Equals("NeedsManualReview", StringComparison.OrdinalIgnoreCase))
            && !x.HealthCategory.Equals("BrokenConfig", StringComparison.OrdinalIgnoreCase)
            && !x.HealthCategory.Equals("NeedsRefresh", StringComparison.OrdinalIgnoreCase)
            && !x.HealthCategory.Equals("MonitoringOnly", StringComparison.OrdinalIgnoreCase));
        return new VerifiedUnresolvedBreakdown(
            total,
            brokenConfig,
            needsRefresh,
            reviewOnly,
            monitoringOnly,
            Math.Max(0, samplesShown),
            Math.Max(0, total - Math.Max(0, samplesShown)));
    }

    public static VerifiedUnresolvedExport BuildUnresolvedExport(
        IReadOnlyList<ResolvedVerifiedGroup> unresolvedGroups,
        IReadOnlyDictionary<string, AllowlistRepairGroup> repairByGroupKey,
        IReadOnlySet<string> loggedGroupKeys,
        DateTime timestampUtc)
    {
        var rows = unresolvedGroups.Select(g =>
        {
            repairByGroupKey.TryGetValue(g.GroupKey, out var repair);
            var healthCategory = repair?.HealthCategory ?? "ReviewOnly";
            var recommendedAction = repair?.RecommendedAction ?? "NeedsManualReview";
            var repairConfidence = repair?.RepairConfidence ?? "None";
            if (healthCategory.Equals("MonitoringOnly", StringComparison.OrdinalIgnoreCase) && recommendedAction.Equals("NeedsManualReview", StringComparison.OrdinalIgnoreCase))
                healthCategory = "ReviewOnly";
            var logged = loggedGroupKeys.Contains(g.GroupKey);
            return new VerifiedUnresolvedExportRow(
                g.GroupKey,
                g.RejectionReason,
                g.ValidationStatus,
                healthCategory,
                recommendedAction,
                repairConfidence,
                logged,
                !logged);
        }).ToArray();

        return new VerifiedUnresolvedExport(timestampUtc, rows.Length, rows);
    }

    public static AllowlistConfigValidationSummary AllowlistConfigValidation(IReadOnlyList<VerifiedMultiOutcomeGroupConfig> groups)
    {
        var total = groups.Count;
        var unique = groups.Select(x => x.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new AllowlistConfigValidationSummary(total, unique, Math.Max(0, total - unique), groups.Count(x => x.Enabled), groups.Count(x => !x.Enabled));
    }
}
