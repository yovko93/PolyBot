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
}
