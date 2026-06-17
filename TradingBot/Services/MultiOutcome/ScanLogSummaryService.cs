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


public sealed record AllowlistPrimaryClassificationGroup(
    string GroupKey,
    string FinalPrimaryCategory,
    IReadOnlyList<string> PrimaryCategories,
    IReadOnlyList<string> Reasons);

public sealed record AllowlistPrimaryClassificationSummary(
    int Configured,
    int Healthy,
    int MonitoringOnly,
    int NeedsPricingPrune,
    int NeedsRefresh,
    int ReviewOnly,
    int BrokenConfig,
    int Disabled,
    int Ignored,
    int PrimaryCategorySum,
    bool Valid,
    IReadOnlyList<AllowlistPrimaryClassificationGroup> Groups,
    IReadOnlyList<string> MissingGroups,
    IReadOnlyList<AllowlistPrimaryClassificationGroup> DuplicateGroups)
{
    public int DuplicatePrimaryCategoryGroupCount => DuplicateGroups.Count;
    public int MissingPrimaryCategoryGroupCount => MissingGroups.Count;
}

public sealed record VerifiedUnresolvedExport(DateTime Timestamp, int Total, VerifiedUnresolvedCategoryCounts CategoryCounts, IReadOnlyList<VerifiedUnresolvedGroupDiagnostic> Groups);

public sealed record VerifiedUnresolvedExportRow(string GroupKey, string Reason, string ValidationStatus, string HealthCategory, string RecommendedAction, string RepairConfidence, bool IsSampleLogged, bool IsSuppressedInConsole);

public sealed record AllowlistConfigValidationSummary(int Total, int UniqueGroupKeys, int DuplicateGroupKeys, int Enabled, int Disabled)
{
    public string ToLogLine() => $"[ALLOWLIST_CONFIG_VALIDATION] Total={Total} UniqueGroupKeys={UniqueGroupKeys} DuplicateGroupKeys={DuplicateGroupKeys} Enabled={Enabled} Disabled={Disabled}";
}

public static class ScanLogSummaryService
{
    public static bool ShouldLogBatchScan(bool operationalQuietMode, bool logEveryScanCycle, bool logBatchScanInQuietMode, int scanId, int everyNBatches, bool fullCycleComplete, bool materialStateChange, bool hasExecutableOrPaperEvent, bool hasError)
    {
        if (!logEveryScanCycle) return false;
        if (hasError || hasExecutableOrPaperEvent) return true;
        if (!operationalQuietMode || logBatchScanInQuietMode) return true;
        // In quiet mode, [SCAN] is first-batch only; full-cycle and periodic progress are emitted
        // through [SCANNER_SUMMARY] so wrap batches do not spam console/UI recent logs.
        return scanId <= 1;
    }

    public static bool ShouldEmitScannerSummary(DateTime nowUtc, DateTime lastSummaryUtc, int everyMinutes, bool fullCycleComplete = false, bool hasError = false, bool hasPaperOpen = false, bool emitOnFullCycle = true, bool emitOnError = true, bool emitOnPaperOpen = true)
        => (emitOnFullCycle && fullCycleComplete)
            || (emitOnError && hasError)
            || (emitOnPaperOpen && hasPaperOpen)
            || (everyMinutes > 0 && (lastSummaryUtc == DateTime.MinValue || nowUtc - lastSummaryUtc >= TimeSpan.FromMinutes(everyMinutes)));

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
        => RejectedOnlyCandidateScanFingerprint(rejectedByReason.Values.Sum(), topReject, rejectedByReason, candidateCountBucketSize, reasonBucketSize);

    public static string RejectedOnlyCandidateScanFingerprint(int candidateCount, string topReject, IReadOnlyDictionary<string, int> rejectedByReason, int candidateCountBucketSize, int reasonBucketSize)
    {
        // Rejected-only scans are operationally useful only when the dominant reject class changes,
        // an executable appears, or a coarse candidate/reason bucket changes. Quiet-mode callers
        // pass 25 so normal churn such as 8 -> 14 -> 23 stays quiet.
        var candidateBucket = Math.Max(25, candidateCountBucketSize);
        var reasonBucket = Math.Max(25, reasonBucketSize);
        var reasonBuckets = string.Join(",", rejectedByReason
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key}:{(int)Math.Floor(x.Value / (decimal)reasonBucket)}"));
        return $"top:{topReject}|candidateBucket:{(int)Math.Floor(candidateCount / (decimal)candidateBucket)}|reasonBuckets:{reasonBuckets}";
    }

    public static string RejectedOnlyCandidateScanFingerprint(string topReject, IReadOnlyDictionary<string, int> rejectedByReason, int reasonBucketSize)
        => RejectedOnlyCandidateScanFingerprint(rejectedByReason.Values.Sum(), topReject, rejectedByReason, reasonBucketSize, reasonBucketSize);

    public static string RepairSuggestionStableHash(string groupKey, string action, string confidence, IEnumerable<string> addedIds, IEnumerable<string> removedIds, int missingNoAsk, bool locked, bool quarantined)
    {
        var added = string.Join(",", addedIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        var removed = string.Join(",", removedIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return $"group:{groupKey}|action:{action}|confidence:{confidence}|added:{added}|removed:{removed}|missing:{missingNoAsk}|locked:{locked.ToString().ToLowerInvariant()}|quarantined:{quarantined.ToString().ToLowerInvariant()}";
    }

    public static string RepairActionDirectionFingerprint(string groupKey, string? previousAction, string currentAction)
        => $"group:{groupKey}|direction:{previousAction ?? string.Empty}->{currentAction}";

    public static bool IsWomenUsOpenRepairFlipFlop(string groupKey, string? previousAction, string currentAction, string reasonForChange)
        => groupKey.Equals("winner:2026 women s us open|kind:generic", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(previousAction)
            && !previousAction.Equals(currentAction, StringComparison.OrdinalIgnoreCase)
            && (reasonForChange.Equals("RepairSnapshotReclassified", StringComparison.OrdinalIgnoreCase)
                || reasonForChange.Equals("NoMatchAcrossSnapshots", StringComparison.OrdinalIgnoreCase));

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
        return $"unresolvedTotal:{counts.Total}|broken:{counts.BrokenConfig}|refresh:{counts.NeedsRefresh}|review:{counts.ReviewOnly}|monitor:{counts.MonitoringOnly}|other:{counts.Other}|groups:{groupSetFingerprint}|activeExecutable:{Math.Max(0, activeExecutable)}|paperOpened:{Math.Max(0, paperOpened)}|bestActiveNetBucket:{edgeBucket}";
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


    public static AllowlistPrimaryClassificationSummary AllowlistPrimaryClassification(
        IReadOnlyList<VerifiedMultiOutcomeGroupConfig> configuredGroups,
        IReadOnlyList<AllowlistRepairGroup> repairGroups)
    {
        var repairByGroup = repairGroups
            .GroupBy(x => x.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Last(), StringComparer.OrdinalIgnoreCase);
        var groups = new List<AllowlistPrimaryClassificationGroup>();
        var missing = new List<string>();

        foreach (var cfg in configuredGroups)
        {
            repairByGroup.TryGetValue(cfg.GroupKey, out var repair);
            var candidates = CandidatePrimaryCategories(cfg, repair).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var final = FinalPrimaryCategory(candidates);
            var reasons = CandidateReasons(cfg, repair).ToArray();
            if (string.IsNullOrWhiteSpace(final))
            {
                missing.Add(cfg.GroupKey);
                continue;
            }
            groups.Add(new AllowlistPrimaryClassificationGroup(cfg.GroupKey, final, candidates, reasons));
        }

        var duplicates = groups.Where(x => x.PrimaryCategories.Count > 1).ToArray();
        var healthy = groups.Count(x => x.FinalPrimaryCategory.Equals("Healthy", StringComparison.OrdinalIgnoreCase));
        var monitoring = groups.Count(x => x.FinalPrimaryCategory.Equals("MonitoringOnly", StringComparison.OrdinalIgnoreCase));
        var prune = groups.Count(x => x.FinalPrimaryCategory.Equals("NeedsPricingPrune", StringComparison.OrdinalIgnoreCase));
        var refresh = groups.Count(x => x.FinalPrimaryCategory.Equals("NeedsRefresh", StringComparison.OrdinalIgnoreCase));
        var review = groups.Count(x => x.FinalPrimaryCategory.Equals("ReviewOnly", StringComparison.OrdinalIgnoreCase));
        var broken = groups.Count(x => x.FinalPrimaryCategory.Equals("BrokenConfig", StringComparison.OrdinalIgnoreCase));
        var disabled = groups.Count(x => x.FinalPrimaryCategory.Equals("Disabled", StringComparison.OrdinalIgnoreCase));
        var ignored = groups.Count(x => x.FinalPrimaryCategory.Equals("Ignored", StringComparison.OrdinalIgnoreCase));
        var sum = healthy + monitoring + prune + refresh + review + broken + disabled + ignored;
        return new AllowlistPrimaryClassificationSummary(configuredGroups.Count, healthy, monitoring, prune, refresh, review, broken, disabled, ignored, sum, sum == configuredGroups.Count && duplicates.Length == 0 && missing.Count == 0, groups, missing, duplicates);
    }

    private static IEnumerable<string> CandidatePrimaryCategories(VerifiedMultiOutcomeGroupConfig cfg, AllowlistRepairGroup? repair)
    {
        if (repair is null) yield break;
        var health = repair.HealthCategory ?? string.Empty;
        var action = repair.RecommendedAction ?? string.Empty;
        var reason = repair.Reason ?? string.Empty;
        if (health.Equals("PricingUnavailable", StringComparison.OrdinalIgnoreCase)) yield return "MonitoringOnly";
        if (IsPrimaryCategory(health)) yield return health;
        if (!cfg.Enabled) yield return "Disabled";
        if (action.Equals("KeepMonitoring", StringComparison.OrdinalIgnoreCase)) yield return "MonitoringOnly";
        if (action.Equals("PruneMissingNoAskLegs", StringComparison.OrdinalIgnoreCase)) yield return "NeedsPricingPrune";
        if (action.Equals("RefreshFromCandidateExport", StringComparison.OrdinalIgnoreCase)) yield return "NeedsRefresh";
        if (action.Equals("DisableMissingMarkets", StringComparison.OrdinalIgnoreCase)) yield return "BrokenConfig";
        if (action.Equals("NeedsManualReview", StringComparison.OrdinalIgnoreCase) || reason.Contains("LockedManualReview", StringComparison.OrdinalIgnoreCase) || reason.Contains("ManualReview", StringComparison.OrdinalIgnoreCase) || reason.Contains("ManualLock", StringComparison.OrdinalIgnoreCase)) yield return "ReviewOnly";
    }

    private static IEnumerable<string> CandidateReasons(VerifiedMultiOutcomeGroupConfig cfg, AllowlistRepairGroup? repair)
    {
        if (!cfg.Enabled) yield return "DisabledConfig";
        if (repair is null) { yield return "MissingRepairGroup"; yield break; }
        if (!string.IsNullOrWhiteSpace(repair.HealthCategory)) yield return $"HealthCategory:{repair.HealthCategory}";
        if (!string.IsNullOrWhiteSpace(repair.RecommendedAction)) yield return $"RecommendedAction:{repair.RecommendedAction}";
        if (!string.IsNullOrWhiteSpace(repair.Reason)) yield return $"Reason:{repair.Reason}";
        if (repair.MissingNoAskMarketIds.Count > 0) yield return "MissingNoAsk";
        if (repair.MissingMarketIds.Count > 0) yield return "DisableMissingMarkets";
    }

    private static bool IsPrimaryCategory(string value)
        => value.Equals("Healthy", StringComparison.OrdinalIgnoreCase)
            || value.Equals("MonitoringOnly", StringComparison.OrdinalIgnoreCase)
            || value.Equals("NeedsPricingPrune", StringComparison.OrdinalIgnoreCase)
            || value.Equals("NeedsRefresh", StringComparison.OrdinalIgnoreCase)
            || value.Equals("ReviewOnly", StringComparison.OrdinalIgnoreCase)
            || value.Equals("BrokenConfig", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Disabled", StringComparison.OrdinalIgnoreCase)
            || value.Equals("Ignored", StringComparison.OrdinalIgnoreCase);

    private static string FinalPrimaryCategory(IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0) return string.Empty;
        foreach (var category in new[] { "Disabled", "Ignored", "ReviewOnly", "NeedsPricingPrune", "NeedsRefresh", "BrokenConfig", "MonitoringOnly", "Healthy" })
            if (candidates.Contains(category, StringComparer.OrdinalIgnoreCase)) return category;
        return string.Empty;
    }

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
        if (recommendedAction.Equals("NeedsManualReview", StringComparison.OrdinalIgnoreCase) || string.Equals(healthCategory, "ReviewOnly", StringComparison.OrdinalIgnoreCase)) return "ReviewOnly";
        if (recommendedAction.Equals("PruneMissingNoAskLegs", StringComparison.OrdinalIgnoreCase) || string.Equals(healthCategory, "NeedsPricingPrune", StringComparison.OrdinalIgnoreCase)) return "NeedsPricingPrune";
        if (recommendedAction.Equals("RefreshFromCandidateExport", StringComparison.OrdinalIgnoreCase) || string.Equals(healthCategory, "NeedsRefresh", StringComparison.OrdinalIgnoreCase)) return "NeedsRefresh";
        if (recommendedAction.Equals("DisableMissingMarkets", StringComparison.OrdinalIgnoreCase) || string.Equals(healthCategory, "BrokenConfig", StringComparison.OrdinalIgnoreCase)) return "BrokenConfig";
        if (recommendedAction.Equals("KeepMonitoring", StringComparison.OrdinalIgnoreCase) || string.Equals(healthCategory, "MonitoringOnly", StringComparison.OrdinalIgnoreCase) || string.Equals(healthCategory, "PricingUnavailable", StringComparison.OrdinalIgnoreCase)) return "MonitoringOnly";
        return string.IsNullOrWhiteSpace(healthCategory) ? "Other" : healthCategory;
    }
}
