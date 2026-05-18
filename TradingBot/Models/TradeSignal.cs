namespace TradingBot.Models;

public enum TradeAction
{
    Buy,
    Sell,
    None
}

public class TradeSignal
{
    public TradeAction Action { get; set; }
    public decimal Price { get; set; }

    public static TradeSignal None => new TradeSignal
    {
        Action = TradeAction.None
    };
}