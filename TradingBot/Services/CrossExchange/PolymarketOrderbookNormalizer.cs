using TradingBot.Models;
using TradingBot.Models.Normalized;

namespace TradingBot.Services.CrossExchange;

public static class PolymarketOrderbookNormalizer
{
    public static ExchangeOrderbook Normalize(BinaryOrderBookSnapshot snapshot)
    {
        return new ExchangeOrderbook(
            "POLYMARKET",
            snapshot.MarketId,
            snapshot.Question,
            snapshot.YesBid?.Price,
            snapshot.YesAsk?.Price,
            snapshot.NoBid?.Price,
            snapshot.NoAsk?.Price,
            snapshot.YesAsk?.Size ?? 0,
            snapshot.NoAsk?.Size ?? 0,
            DateTime.UtcNow,
            "active",
            "OrderBookService");
    }
}
