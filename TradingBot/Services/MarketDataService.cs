using Newtonsoft.Json;
using TradingBot.Models;

namespace TradingBot.Services;

public class MarketDataService
{
    private readonly HttpClient _http;

    public MarketDataService(HttpClient http)
    {
        _http = http;
    }

    public async Task<List<Market>> GetMarketsAsync()
    {
        var allMarkets = new List<Market>();

        const int pageSize = 100;
        const int maxMarkets = 1000;

        for (int offset = 0; offset < maxMarkets; offset += pageSize)
        {
            var url =
                "https://gamma-api.polymarket.com/markets" +
                "?active=true" +
                "&closed=false" +
                "&archived=false" +
                $"&limit={pageSize}" +
                $"&offset={offset}";

            try
            {
                var json = await _http.GetStringAsync(url);

                var batch = JsonConvert.DeserializeObject<List<Market>>(json)
                            ?? new List<Market>();

                if (batch.Count == 0)
                    break;

                allMarkets.AddRange(batch);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MARKET DATA ERROR] {ex.Message}");
                break;
            }
        }

        return allMarkets;
    }
}