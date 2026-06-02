using TradingBot.Services;

namespace TradingBot.Services.MultiOutcome;

public sealed record DiscoveryHealthSummary(bool Healthy, bool Degraded, int Pages, int Active, string Reason, string ScanConfidence, int ExpectedMinActive)
{
    public string ToLogLine() => $"[DISCOVERY_HEALTH] Healthy={Healthy.ToString().ToLowerInvariant()} Degraded={Degraded.ToString().ToLowerInvariant()} Pages={Pages} Active={Active} Reason={Reason} ScanConfidence={ScanConfidence} ExpectedMinActive={ExpectedMinActive}";
}

public sealed record VerifiedUnresolvedSampleSummary(int Total, int SamplesShown, int More)
{
    public string ToLogLine() => $"[VERIFIED_UNRESOLVED_SUMMARY] Total={Total} SamplesShown={SamplesShown} More={More}";
}

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

    public static string CandidateScanFingerprint(int candidateCount, string topReject, IReadOnlyDictionary<string, int> rejectedByReason, int countBucketSize)
    {
        var bucket = Math.Max(1, countBucketSize);
        var reasonBuckets = string.Join(",", rejectedByReason.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value / bucket}"));
        return $"count:{candidateCount / bucket}|top:{topReject}|reasons:{reasonBuckets}";
    }

    public static string ProfileComparisonFingerprint(IReadOnlyList<VerifiedBasketScreener.ScreenResult> rows, decimal netDelta)
    {
        var bucket = netDelta <= 0m ? 0.002m : netDelta;
        static long EdgeBucket(decimal value, decimal bucket) => (long)Math.Round(value / bucket, MidpointRounding.AwayFromZero);
        return string.Join("|", rows.Take(5).Select(row =>
        {
            var conservative = row.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("Conservative", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? row.ActiveProfileNetEdge;
            var poly = row.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m;
            return $"{row.GroupKey}:{EdgeBucket(row.GrossEdge, bucket)}:{EdgeBucket(conservative, bucket)}:{EdgeBucket(poly, bucket)}:{row.Classification}";
        }));
    }

    public static VerifiedUnresolvedSampleSummary UnresolvedSampleSummary(int total, int sampleLimit)
    {
        var shown = Math.Min(Math.Max(0, sampleLimit), Math.Max(0, total));
        return new VerifiedUnresolvedSampleSummary(total, shown, Math.Max(0, total - shown));
    }

    public static AllowlistConfigValidationSummary AllowlistConfigValidation(IReadOnlyList<VerifiedMultiOutcomeGroupConfig> groups)
    {
        var total = groups.Count;
        var unique = groups.Select(x => x.GroupKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return new AllowlistConfigValidationSummary(total, unique, Math.Max(0, total - unique), groups.Count(x => x.Enabled), groups.Count(x => !x.Enabled));
    }
}
