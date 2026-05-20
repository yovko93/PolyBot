namespace TradingBot.Models;

public record BasketArbLeg(
    string MarketId,
    string TokenId,
    string Question,
    string Outcome,
    decimal Price,
    decimal Size
);

public record BasketArbOpportunity(
    string GroupKey,
    string Strategy,
    List<BasketArbLeg> Legs,
    decimal Quantity,
    decimal CostPerShare,
    decimal GuaranteedPayoutPerShare,
    decimal EdgePerShare,
    decimal ExpectedProfit
);