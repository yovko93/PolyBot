namespace TradingBot.Options;

public class OpportunityFilteringOptions
{
    public const string SectionName = "OpportunityFiltering";
    public bool HideNegativeEdge { get; set; } = true;
    public bool HideZeroEdge { get; set; } = true;
    public bool HideNegativeExpectedProfit { get; set; } = true;
    public bool ShowSkippedOnlyIfPositiveEdge { get; set; } = true;
    public decimal MinEdgeToDisplay { get; set; } = 0.0001m;
    public bool EnableDebugNegativeEdgeView { get; set; } = false;
}
