using TradingBot.Models;

namespace TradingBot.Engines;

public class StrategyEngine
{
    public TradeSignal Evaluate(string marketId, decimal yesAsk, decimal yesBid, List<Position> positions)
    {
        var openPositions = positions
            .Where(p => p.MarketId == marketId && p.IsOpen)
            .ToList();

        // EXIT
        foreach (var pos in openPositions)
        {
            var change = (yesBid - pos.EntryPrice) / pos.EntryPrice;

            if (change >= 0.20m || change <= -0.10m)
            {
                return new TradeSignal
                {
                    Action = TradeAction.Sell,
                    Price = yesBid
                };
            }
        }

        // ENTRY (купуваш на ask!)
        if (!openPositions.Any() && yesAsk < 0.3m)
        {
            return new TradeSignal
            {
                Action = TradeAction.Buy,
                Price = yesAsk
            };
        }

        return TradeSignal.None;
    }
}