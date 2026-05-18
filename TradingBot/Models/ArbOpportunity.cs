namespace TradingBot.Models;

public record ArbLeg(
    string MarketId,
    string Question,
    string Outcome,
    decimal Price,
    decimal Size
);

public record ArbOpportunity(
   ArbLeg Leg1,
    ArbLeg Leg2,
    decimal Quantity,
    decimal CostPerShare,
    decimal GrossEdgePerShare,
    decimal ExpectedProfit,
    double SemanticScore,
    string Engine = "Unknown",
    string Strategy = "TWO_LEG_ARB"
);