using TradingBot.Models;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class OpportunityFamilyRankingTests
{
    [Fact]
    public void Build_excludes_missing_no_ask_raw_spike_from_priced_positive_families()
    {
        var invalidSpike = Audit("m-invalid", rawEdge: 0.997m, afterSafetyEdge: 0.997m, rejectedReason: "MissingNoAsk");
        var validNearBreakEven = Audit("m-valid", rawEdge: -0.001m, afterSafetyEdge: -0.003m, rejectedReason: "BelowMinEdge");

        var ranking = OpportunityFamilyRankingService.Build(new[] { invalidSpike, validNearBreakEven }, Array.Empty<TradingBot.Api.VerifiedGroupDiagnosticDto>(), Array.Empty<TradingBot.Api.VerifiedGroupPricingDto>(), null);

        Assert.Equal(0, ranking.PositiveFamilies);
        Assert.Equal("SingleMarketBuyBoth|SingleMarketBuyBoth|other|binary|2|High|BelowMinEdge|notExecutable", ranking.BestPricedFamily);
        Assert.Equal(-0.003m, ranking.BestPricedAfterSafetyEdge);
        Assert.DoesNotContain("MissingNoAsk", ranking.BestPricedFamily);
        Assert.True(ranking.InvalidRawSpikeFamiliesCount > 0);
        Assert.Equal(0.997m, ranking.InvalidRawSpikeBestEdge);
        Assert.Equal("MissingNoAsk", ranking.InvalidRawSpikeTopReason);
    }

    [Fact]
    public void WithConsistency_flags_positive_family_without_strategy_positive_counter()
    {
        var validPositive = Audit("m-positive", rawEdge: 0.02m, afterSafetyEdge: 0.01m, rejectedReason: "None");
        var ranking = OpportunityFamilyRankingService.Build(new[] { validPositive }, Array.Empty<TradingBot.Api.VerifiedGroupDiagnosticDto>(), Array.Empty<TradingBot.Api.VerifiedGroupPricingDto>(), null);

        var consistent = OpportunityFamilyRankingService.WithConsistency(ranking, totalPositive: 0, singleMarketValidAfterSafetyPositive: 0);

        Assert.False(consistent.RankingConsistent);
        Assert.Equal("PositiveFamilyWithoutValidPositiveCandidate", consistent.RankingConsistencyReason);
    }

    private static SingleMarketOpportunityAuditDto Audit(string marketId, decimal rawEdge, decimal afterSafetyEdge, string rejectedReason)
        => new(
            marketId,
            $"condition-{marketId}",
            $"Question {marketId}",
            0.5m,
            0.5m,
            1m - rawEdge,
            rawEdge,
            afterSafetyEdge,
            afterSafetyEdge,
            10m,
            0m,
            0m,
            rejectedReason,
            null,
            false,
            false,
            false,
            false,
            DateTime.UtcNow);
}
