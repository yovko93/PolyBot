namespace TradingBot.Models;

public enum LiveOrderSide
{
    Buy,
    Sell
}

public record LiveOrderIntent(
    string MarketId,
    string Question,
    string Outcome,
    LiveOrderSide Side,
    decimal LimitPrice,
    decimal Quantity,
    string ClientOrderId,
    string Strategy,
    string CandidateKey
);

public record LiveOrderDryRunPlan(
    string CandidateKey,
    string Strategy,
    decimal Quantity,
    decimal TotalNotional,
    decimal ExpectedProfit,
    decimal EdgePerShare,
    IReadOnlyList<LiveOrderIntent> Orders,
    string Notes
);
