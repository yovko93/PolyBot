namespace TradingBot.Services.MultiOutcome;

public sealed record VerifiedScanMetricSummary(int ActiveExecutableCount, int ExperimentalCandidateCount, decimal? BestExperimentalNet, decimal? BestAlternateProfileNet);

public static class VerifiedScanMetrics
{
    public static VerifiedScanMetricSummary Summarize(IEnumerable<VerifiedBasketScreener.ScreenResult> rows, string experimentalProfile, decimal threshold)
    {
        var list = rows.ToArray();
        decimal ExperimentalNet(VerifiedBasketScreener.ScreenResult row) => row.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals(experimentalProfile, StringComparison.OrdinalIgnoreCase))?.NetEdge ?? decimal.MinValue;
        var active = list.Where(x => x.ActiveProfileNetEdge > threshold).ToArray();
        var experimentalOnly = list.Where(x => x.ActiveProfileNetEdge <= threshold && ExperimentalNet(x) > threshold).ToArray();
        return new VerifiedScanMetricSummary(
            active.Length,
            experimentalOnly.Length,
            experimentalOnly.Length == 0 ? null : experimentalOnly.Select(ExperimentalNet).Max(),
            list.Length == 0 ? null : list.Select(ExperimentalNet).Max());
    }
}
