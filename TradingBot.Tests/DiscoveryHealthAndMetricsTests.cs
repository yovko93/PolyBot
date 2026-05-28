using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class DiscoveryHealthAndMetricsTests
{
    [Fact]
    public void Partial_Discovery_Is_Marked_PartialConfidence_And_BlocksPaper()
    {
        var summary = new MarketDiscoverySummary(PagesFetched: 71, ActiveMarketsAvailable: 6011, RawLoadedTotal: 7100, DiscoveryHealthy: false, StoppedReason: "OperationCanceled", LastDiscoveryError: "timeout", DiscoveryDegraded: true, PartialDiscovery: true, ExpectedMinActiveMarkets: 8000, ExpectedMinPagesFetched: 95, FailedPages: new[] { 72 }, RetriesAttempted: 3);
        var health = DiscoveryHealthFactory.FromSummary(summary, requireHealthyDiscoveryForPaperOpen: true);
        Assert.False(health.Healthy);
        Assert.True(health.Degraded);
        Assert.True(health.Partial);
        Assert.Equal("PartialDiscovery", health.ScanConfidence);
        Assert.True(health.PaperExecutionBlockedByDiscoveryHealth);
    }

    [Fact]
    public void No_ExperimentalCandidates_Returns_Null_BestExperimentalNet()
    {
        var rows = new[] { Row("nba", conservative: 0.0015m, polymarketApprox: 0.0045m) };
        var summary = VerifiedScanMetrics.Summarize(rows, "PolymarketApprox", 0.001m);
        Assert.Equal(1, summary.ActiveExecutableCount);
        Assert.Equal(0, summary.ExperimentalCandidateCount);
        Assert.Null(summary.BestExperimentalNet);
        Assert.Equal(0.0045m, summary.BestAlternateProfileNet);
    }

    [Fact]
    public void Negative_Conservative_Positive_PolymarketApprox_Is_ExperimentalOnly()
    {
        var rows = new[] { Row("experimental", conservative: -0.0005m, polymarketApprox: 0.0045m) };
        var summary = VerifiedScanMetrics.Summarize(rows, "PolymarketApprox", 0.001m);
        Assert.Equal(0, summary.ActiveExecutableCount);
        Assert.Equal(1, summary.ExperimentalCandidateCount);
        Assert.Equal(0.0045m, summary.BestExperimentalNet);
    }

    private static VerifiedBasketScreener.ScreenResult Row(string group, decimal conservative, decimal polymarketApprox)
    {
        var profiles = new[]
        {
            new VerifiedBasketScreener.ProfileResult("Conservative", "", 0, 0, 0, conservative, conservative, conservative > 0, false),
            new VerifiedBasketScreener.ProfileResult("PolymarketApprox", "", 0, 0, 0, polymarketApprox, polymarketApprox, polymarketApprox > 0, true),
            new VerifiedBasketScreener.ProfileResult("RawOnly", "", 0, 0, 0, polymarketApprox, polymarketApprox, polymarketApprox > 0, true)
        };
        return new VerifiedBasketScreener.ScreenResult(group, 3, 2m, 1.99m, 0.01m, conservative, conservative > 0 ? 1m : 0m, conservative, "", "", "Conservative", profiles, Array.Empty<VerifiedBasketScreener.QuantityScenarioResult>(), "None", DateTime.UtcNow, 0m, false, "", Array.Empty<string>(), polymarketApprox, VerifiedBasketScreener.ExecutionStatus.NotExecutable);
    }
}
