using System.Text.Json;
using TradingBot.Models.Normalized;

namespace TradingBot.Services.CrossExchange;

public class MarketPairConfigLoader(string configPath)
{
    public List<CrossExchangeMarketPair> Load()
    {
        if (!File.Exists(configPath)) return [];
        var json = File.ReadAllText(configPath);
        var pairs = JsonSerializer.Deserialize<List<CrossExchangeMarketPair>>(json, new JsonSerializerOptions{PropertyNameCaseInsensitive=true}) ?? [];
        return pairs.Where(p => !string.IsNullOrWhiteSpace(p.CanonicalKey) && !string.IsNullOrWhiteSpace(p.PolymarketMarketId) && !string.IsNullOrWhiteSpace(p.KalshiTicker)).ToList();
    }
}
