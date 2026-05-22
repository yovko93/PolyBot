using System.Text.Json;
using System.Text.Json.Serialization;
using TradingBot.Models.Normalized;

namespace TradingBot.Services.CrossExchange;

public class MarketPairConfigLoader(string configPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public List<CrossExchangeMarketPair> Load()
    {
        if (!File.Exists(configPath)) return [];
        var json = File.ReadAllText(configPath);
        var pairs = JsonSerializer.Deserialize<List<CrossExchangeMarketPair>>(json, JsonOptions) ?? [];
        return pairs.Where(p => !string.IsNullOrWhiteSpace(p.CanonicalKey) && !string.IsNullOrWhiteSpace(p.PolymarketMarketId) && !string.IsNullOrWhiteSpace(p.KalshiTicker)).ToList();
    }
}
