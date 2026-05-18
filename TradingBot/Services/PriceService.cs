using System.Collections.Concurrent;

namespace TradingBot.Services;

public class PriceService
{
    private readonly ConcurrentDictionary<string, decimal> _prices = new();

    public void UpdatePrice(string marketId, decimal price)
    {
        _prices[marketId] = price;
    }

    public decimal? GetPrice(string marketId)
    {
        return _prices.TryGetValue(marketId, out var p) ? p : null;
    }
}