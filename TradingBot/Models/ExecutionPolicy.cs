namespace TradingBot.Models;

public class ExecutionPolicy
{
    public decimal MaxNotionalPerTrade { get; init; } = 100m;
    public decimal MinNotionalPerTrade { get; init; } = 5m;

    public decimal MinEdgePerShare { get; init; } = 0.003m;
    public decimal MinExpectedProfit { get; init; } = 0.25m;

    public decimal MaxLockedCapital { get; init; } = 300m;

    public int MaxLegsPerBasket { get; init; } = 50;

    public int MaxOpenPositions { get; set; } = 5;

    public decimal MaxExposurePerGroup { get; set; } = 100m;

    public bool AllowBasketArbs { get; init; } = true;
    public bool AllowSingleMarketArbs { get; init; } = true;
    public bool AllowCompleteSetSellArbs { get; init; } = true;
    public bool AllowThresholdArbs { get; init; } = true;

    public bool EnableSizingLogs { get; init; } = false;
}