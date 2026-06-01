namespace TradingBot.Models;

public enum FillSide
{
    Buy,
    Sell
}

public record SimulatedFillLevel(
    decimal Price,
    decimal Quantity,
    decimal Notional
);

public record OrderBookFillSimulationResult(
    string MarketId,
    string Question,
    string Outcome,
    FillSide Side,
    decimal RequestedQuantity,
    decimal FilledQuantity,
    decimal AveragePrice,
    decimal TotalNotional,
    bool IsComplete,
    List<SimulatedFillLevel> Levels
)
{
    public decimal RemainingQuantity => RequestedQuantity - FilledQuantity;
}