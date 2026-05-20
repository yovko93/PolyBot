namespace TradingBot.Models;

public enum LiveOrderSide
{
    BUY = 0,
    SELL = 1
}

public enum LiveOrderType
{
    GTC,
    FOK,
    FAK,
    GTD
}

public sealed record OrderLegCandidate(
    string Strategy,
    string GroupKey,
    string Question,
    string TokenId,
    string Outcome,
    LiveOrderSide Side,
    decimal Price,
    decimal Size,
    decimal EdgePerShare
);