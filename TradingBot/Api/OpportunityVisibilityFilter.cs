using TradingBot.Options;

namespace TradingBot.Api;

public static class OpportunityVisibilityFilter
{
    public static bool IsVisibleOpportunity(OpportunityDto o, OpportunityFilteringOptions f)
    {
        if (f.ShowSkippedOnlyIfPositiveEdge && string.Equals(o.Status, "SKIPPED", StringComparison.OrdinalIgnoreCase) && o.EdgePerShare <= 0) return false;
        if (f.HideNegativeExpectedProfit && o.ExpectedProfit <= 0) return false;
        if (f.HideNegativeEdge && o.EdgePerShare < 0) return false;
        if (f.HideZeroEdge && o.EdgePerShare == 0) return false;
        return o.EdgePerShare >= f.MinEdgeToDisplay;
    }
}
