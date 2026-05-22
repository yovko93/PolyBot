using TradingBot.Api;
using TradingBot.Options;

namespace TradingBot.Tests;

public class OpportunityVisibilityFilterTests
{
    private static readonly OpportunityFilteringOptions Options = new();

    [Fact]
    public void NegativeSkippedOpportunity_IsHidden()
    {
        var opp = new OpportunityDto("1", DateTime.UtcNow, 1, "S", "G", "M", "B", -0.003m, -0.299m, 0, 0, 0, false, "SKIPPED", null, 1);
        Assert.False(OpportunityVisibilityFilter.IsVisibleOpportunity(opp, Options));
    }

    [Fact]
    public void ZeroSkippedOpportunity_IsHidden()
    {
        var opp = new OpportunityDto("1", DateTime.UtcNow, 1, "S", "G", "M", "B", 0m, 0m, 0, 0, 0, false, "SKIPPED", null, 1);
        Assert.False(OpportunityVisibilityFilter.IsVisibleOpportunity(opp, Options));
    }

    [Fact]
    public void PositiveSkippedOpportunity_IsVisible()
    {
        var opp = new OpportunityDto("1", DateTime.UtcNow, 1, "S", "G", "M", "B", 0.004m, 0.3m, 0, 0, 0, false, "SKIPPED", "MaxOpenPositionsReached", 1);
        Assert.True(OpportunityVisibilityFilter.IsVisibleOpportunity(opp, Options));
    }
}
