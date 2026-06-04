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

public sealed record VerifiedUnresolvedCategoryCounts(int Total, int BrokenConfig, int NeedsRefresh, int ReviewOnly, int MonitoringOnly, int Other, int SamplesShown, int Suppressed)
{
    public int CategoryTotal => BrokenConfig + NeedsRefresh + ReviewOnly + MonitoringOnly + Other;
    public int SampleTotal => SamplesShown + Suppressed;
    public bool InvariantOk => Total == CategoryTotal && Total == SampleTotal;
    public string ToBreakdownLogLine() => $"[VERIFIED_UNRESOLVED_BREAKDOWN] Total={Total} BrokenConfig={BrokenConfig} NeedsRefresh={NeedsRefresh} ReviewOnly={ReviewOnly} MonitoringOnly={MonitoringOnly} Other={Other} SamplesShown={SamplesShown} Suppressed={Suppressed}";
    public string ToCounterErrorLogLine() => $"[VERIFIED_UNRESOLVED_COUNTER_ERROR] UnresolvedTotal={Total} BreakdownTotal={CategoryTotal} SamplesShown={SamplesShown} Suppressed={Suppressed}";
}

public sealed record VerifiedUnresolvedBreakdown(int Total, int BrokenConfig, int NeedsRefresh, int ReviewOnly, int MonitoringOnly, int SamplesShown, int Suppressed)
{
    public string ToLogLine() => $"[VERIFIED_UNRESOLVED_BREAKDOWN] Total={Total} BrokenConfig={BrokenConfig} NeedsRefresh={NeedsRefresh} ReviewOnly={ReviewOnly} MonitoringOnly={MonitoringOnly} SamplesShown={SamplesShown} Suppressed={Suppressed}";
}

public sealed record VerifiedUnresolvedGroupDiagnostic(
    string GroupKey,
    string Reason,
    string ValidationStatus,
    string HealthCategory,
    string RecommendedAction,
    string RepairConfidence,
    bool IsReviewOnly,
    bool IsBrokenConfig,
    bool IsNeedsRefresh,
    bool IsMonitoringOnly,
    bool SampleLogged,
    bool SuppressedInConsole);

public sealed record VerifiedUnresolvedExport(DateTime Timestamp, int Total, VerifiedUnresolvedCategoryCounts CategoryCounts, IReadOnlyList<VerifiedUnresolvedGroupDiagnostic> Groups);

public sealed record VerifiedUnresolvedExportRow(string GroupKey, string Reason, string ValidationStatus, string HealthCategory, string RecommendedAction, string RepairConfidence, bool IsSampleLogged, bool IsSuppressedInConsole);

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


    public static string RejectedOnlyCandidateScanFingerprint(string topReject, IReadOnlyDictionary<string, int> rejectedByReason, int candidateCountBucketSize, int reasonBucketSize)
    {
        // Rejected-only scans are operationally useful only when the dominant reject class changes.
        // Do not include small count fluctuations here; 8 -> 9 -> 15 should stay quiet.
        var reasonKeys = string.Join(",", rejectedByReason.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return $"top:{topReject}|reasons:{reasonKeys}";
    }

    public static string RejectedOnlyCandidateScanFingerprint(string topReject, IReadOnlyDictionary<string, int> rejectedByReason, int reasonBucketSize)
        => RejectedOnlyCandidateScanFingerprint(topReject, rejectedByReason, reasonBucketSize, reasonBucketSize);

    public static string RepairSuggestionStableHash(string groupKey, string action, string confidence, IEnumerable<string> addedIds, IEnumerable<string> removedIds, int missingNoAsk, bool locked, bool quarantined)
    {
        var added = string.Join(",", addedIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var removed = string.Join(",", removedIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return $"group:{groupKey}|action:{action}|confidence:{confidence}|added:{added}|removed:{removed}|missing:{missingNoAsk}|locked:{locked.ToString().ToLowerInvariant()}|quarantined:{quarantined.ToString().ToLowerInvariant()}";
    }

    public static bool ShouldSuppressRejectedOnlyCandidateScan(bool operationalQuietMode, bool logCandidateScanWhenOnlyRejected, bool rejectedOnlyCandidateScan, string currentFingerprint, string lastFingerprint, bool periodic)
        => operationalQuietMode
            && !logCandidateScanWhenOnlyRejected
            && rejectedOnlyCandidateScan
            && currentFingerprint.Equals(lastFingerprint, StringComparison.OrdinalIgnoreCase)
            && !periodic;


    public static string VerifiedUnresolvedGroupSetFingerprint(IEnumerable<string> groupKeys)
        => string.Join(",", groupKeys.Where(x => !string.IsNullOrWhiteSpace(x)).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

    public static string VerifiedUnresolvedCategoryFingerprint(VerifiedUnresolvedCategoryCounts counts, string groupSetFingerprint)
        => $"total:{counts.Total}|broken:{counts.BrokenConfig}|refresh:{counts.NeedsRefresh}|review:{counts.ReviewOnly}|monitor:{counts.MonitoringOnly}|other:{counts.Other}|groups:{groupSetFingerprint}";

    public static string MultiVerifiedScanQuietFingerprint(VerifiedUnresolvedCategoryCounts counts, string groupSetFingerprint, int activeExecutable, int paperOpened = 0, decimal? bestActiveNet = null, decimal significantEdgeDelta = 0.005m)
    {
        var bucketSize = significantEdgeDelta <= 0m ? 0.005m : significantEdgeDelta;
        var edgeBucket = bestActiveNet.HasValue ? ((long)Math.Round(bestActiveNet.Value / bucketSize, MidpointRounding.AwayFromZero)).ToString() : "none";
        return $"{VerifiedUnresolvedCategoryFingerprint(counts, groupSetFingerprint)}|activeExecutable:{Math.Max(0, activeExecutable)}|paperOpened:{Math.Max(0, paperOpened)}|bestActiveNetBucket:{edgeBucket}";
    }

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

    public static VerifiedUnresolvedCategoryCounts UnresolvedCategoryCounts(IReadOnlyList<VerifiedUnresolvedGroupDiagnostic> groups)
    {
        var total = groups.Count;
        var samplesShown = groups.Count(x => x.SampleLogged);
        var suppressed = groups.Count(x => x.SuppressedInConsole);
        return new VerifiedUnresolvedCategoryCounts(
            total,
            groups.Count(x => x.IsBrokenConfig),
            groups.Count(x => x.IsNeedsRefresh),
            groups.Count(x => x.IsReviewOnly),
            groups.Count(x => x.IsMonitoringOnly),
            groups.Count(x => !x.IsBrokenConfig && !x.IsNeedsRefresh && !x.IsReviewOnly && !x.IsMonitoringOnly),
            samplesShown,
            suppressed);
    }

    public static VerifiedUnresolvedBreakdown UnresolvedBreakdown(IReadOnlyList<VerifiedUnresolvedExportRow> groups, int samplesShown)
    {
        var diagnostics = groups.Select(x =>
        {
            var category = NormalizeUnresolvedHealthCategory(x.HealthCategory, x.RecommendedAction);
            return new VerifiedUnresolvedGroupDiagnostic(
                x.GroupKey,
                x.Reason,
                x.ValidationStatus,
                category,
                x.RecommendedAction,
                x.RepairConfidence,
                category.Equals("ReviewOnly", StringComparison.OrdinalIgnoreCase),
                category.Equals("BrokenConfig", StringComparison.OrdinalIgnoreCase),
                category.Equals("NeedsRefresh", StringComparison.OrdinalIgnoreCase),
                category.Equals("MonitoringOnly", StringComparison.OrdinalIgnoreCase),
                x.IsSampleLogged,
                x.IsSuppressedInConsole);
        }).ToArray();
        var counts = UnresolvedCategoryCounts(diagnostics);
        return new VerifiedUnresolvedBreakdown(counts.Total, counts.BrokenConfig, counts.NeedsRefresh, counts.ReviewOnly, counts.MonitoringOnly, Math.Max(0, samplesShown), Math.Max(0, counts.Total - Math.Max(0, samplesShown)));
    }

    public static IReadOnlyList<VerifiedUnresolvedGroupDiagnostic> BuildUnresolvedDiagnostics(
        IReadOnlyList<VerifiedMultiOutcomeGroupConfig> allowlist,
        IReadOnlyList<ResolvedVerifiedGroup> resolvedGroups,
        IReadOnlyDictionary<string, AllowlistRepairGroup> repairByGroupKey,
        IReadOnlySet<string> loggedGroupKeys)
    {
        var resolvedByGroupKey = resolvedGroups
            .GroupBy(x => x.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var verifiedResolvedKeys = resolvedGroups
            .Where(x => x.ValidationStatus.Equals("VerifiedGroupResolved", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.GroupKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return allowlist
            .Where(x => x.Enabled && !verifiedResolvedKeys.Contains(x.GroupKey))
            .Select(config => BuildUnresolvedDiagnostic(config, resolvedByGroupKey, repairByGroupKey, loggedGroupKeys))
            .OrderBy(x => x.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static VerifiedUnresolvedExport BuildUnresolvedExport(IReadOnlyList<VerifiedUnresolvedGroupDiagnostic> diagnostics, DateTime timestampUtc)
        => new(timestampUtc, diagnostics.Count, UnresolvedCategoryCounts(diagnostics), diagnostics);

    public static VerifiedUnresolvedExport BuildUnresolvedExport(
        IReadOnlyList<ResolvedVerifiedGroup> unresolvedGroups,
        IReadOnlyDictionary<string, AllowlistRepairGroup> repairByGroupKey,
        IReadOnlySet<string> loggedGroupKeys,
        DateTime timestampUtc)
    {
        var allowlist = unresolvedGroups
            .Select(x => new VerifiedMultiOutcomeGroupConfig(true, x.GroupKey, x.Title, x.MarketIds, x.ConditionIds, null, "Verified"))
            .ToArray();
        var diagnostics = BuildUnresolvedDiagnostics(allowlist, unresolvedGroups, repairByGroupKey, loggedGroupKeys);
        return BuildUnresolvedExport(diagnostics, timestampUtc);
    }

    public static AllowlistConfigValidationSummary AllowlistConfigValidation(IReadOnlyList<VerifiedMultiOutcomeGroupConfig> groups)
    {
        var total = groups.Count;
        var unique = groups.Select(x => x.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new AllowlistConfigValidationSummary(total, unique, Math.Max(0, total - unique), groups.Count(x => x.Enabled), groups.Count(x => !x.Enabled));
    }

    private static VerifiedUnresolvedGroupDiagnostic BuildUnresolvedDiagnostic(
        VerifiedMultiOutcomeGroupConfig config,
        IReadOnlyDictionary<string, ResolvedVerifiedGroup> resolvedByGroupKey,
        IReadOnlyDictionary<string, AllowlistRepairGroup> repairByGroupKey,
        IReadOnlySet<string> loggedGroupKeys)
    {
        resolvedByGroupKey.TryGetValue(config.GroupKey, out var resolved);
        repairByGroupKey.TryGetValue(config.GroupKey, out var repair);

        var recommendedAction = repair?.RecommendedAction ?? "NeedsManualReview";
        var repairConfidence = repair?.RepairConfidence ?? "None";
        var healthCategory = NormalizeUnresolvedHealthCategory(repair?.HealthCategory, recommendedAction);
        var validationStatus = resolved?.ValidationStatus ?? repair?.Status ?? "NotEvaluatedThisCycle";
        var reason = resolved?.RejectionReason ?? repair?.MismatchReason ?? repair?.Reason ?? "VerifiedGroupNotEvaluatedThisCycle";
        var sampleLogged = loggedGroupKeys.Contains(config.GroupKey);

        return new VerifiedUnresolvedGroupDiagnostic(
            config.GroupKey,
            reason,
            validationStatus,
            healthCategory,
            recommendedAction,
            repairConfidence,
            healthCategory.Equals("ReviewOnly", StringComparison.OrdinalIgnoreCase),
            healthCategory.Equals("BrokenConfig", StringComparison.OrdinalIgnoreCase),
            healthCategory.Equals("NeedsRefresh", StringComparison.OrdinalIgnoreCase),
            healthCategory.Equals("MonitoringOnly", StringComparison.OrdinalIgnoreCase),
            sampleLogged,
            !sampleLogged);
    }

    private static string NormalizeUnresolvedHealthCategory(string? healthCategory, string recommendedAction)
    {
        if (recommendedAction.Equals("NeedsManualReview", StringComparison.OrdinalIgnoreCase)) return "ReviewOnly";
        if (recommendedAction.Equals("DisableMissingMarkets", StringComparison.OrdinalIgnoreCase) || string.Equals(healthCategory, "BrokenConfig", StringComparison.OrdinalIgnoreCase)) return "BrokenConfig";
        if (recommendedAction.Equals("RefreshFromCandidateExport", StringComparison.OrdinalIgnoreCase) || string.Equals(healthCategory, "NeedsRefresh", StringComparison.OrdinalIgnoreCase)) return "NeedsRefresh";
        if (recommendedAction.Equals("KeepMonitoring", StringComparison.OrdinalIgnoreCase) || string.Equals(healthCategory, "MonitoringOnly", StringComparison.OrdinalIgnoreCase)) return "MonitoringOnly";
        if (recommendedAction.Equals("PruneMissingNoAskLegs", StringComparison.OrdinalIgnoreCase) || string.Equals(healthCategory, "NeedsPricingPrune", StringComparison.OrdinalIgnoreCase)) return "ReviewOnly";
        return string.IsNullOrWhiteSpace(healthCategory) ? "Other" : healthCategory;
    }
}
