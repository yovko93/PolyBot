using TradingBot.Services;

namespace TradingBot.Services.MultiOutcome;

public sealed record DiscoveryHealthSummary(bool Healthy, bool Degraded, int Pages, int Active, string Reason, string ScanConfidence, int ExpectedMinActive)
{
    public string ToLogLine() => $"[DISCOVERY_HEALTH] Healthy={Healthy.ToString().ToLowerInvariant()} Degraded={Degraded.ToString().ToLowerInvariant()} Pages={Pages} Active={Active} Reason={Reason} ScanConfidence={ScanConfidence} ExpectedMinActive={ExpectedMinActive}";
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

    public static long EdgeBucket(decimal value, decimal bucket)
        => bucket <= 0m ? (long)(value * 1_000_000m) : (long)Math.Floor(value / bucket);

    public static string CandidateScanFingerprint(
        int candidatesDetected,
        int candidatesVerified,
        int executableGroups,
        string topReject,
        IReadOnlyDictionary<string, int> rejectedByReason,
        int significantCountDelta)
    {
        var bucketSize = Math.Max(10, significantCountDelta);
        var rejected = Math.Max(0, candidatesDetected - candidatesVerified);
        var reasonBuckets = string.Join(",", rejectedByReason
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key}:{x.Value / bucketSize}"));
        var executableBucket = executableGroups > 0 ? $"exec:{executableGroups}" : "exec:0";
        return $"c:{candidatesDetected / bucketSize}|r:{rejected / bucketSize}|top:{topReject}|reasons:{reasonBuckets}|{executableBucket}";
    }

    public static string ProfileComparisonFingerprint(IReadOnlyList<VerifiedBasketScreener.ScreenResult> baskets, decimal significantNetDelta)
    {
        var bucket = significantNetDelta <= 0m ? 0.002m : significantNetDelta;
        return string.Join("|", baskets.Take(5).Select(row =>
        {
            var conservative = row.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("Conservative", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? row.ActiveProfileNetEdge;
            var poly = row.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m;
            return $"{row.GroupKey}:gross:{EdgeBucket(row.GrossEdge, bucket)}:cons:{EdgeBucket(conservative, bucket)}:poly:{EdgeBucket(poly, bucket)}:{row.Classification}";
        }));
    }

    public static string? UnresolvedSummaryLog(int total, int samplesShown)
    {
        var more = Math.Max(0, total - samplesShown);
        return more <= 0 ? null : $"[VERIFIED_UNRESOLVED_SUMMARY] Total={total} SamplesShown={samplesShown} More={more}";
    }
}
