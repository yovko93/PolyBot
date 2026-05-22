using System.Net;
using System.Net.Http.Json;
using TradingBot.Models.Kalshi;

namespace TradingBot.Services.Kalshi;

public class KalshiMarketDataService(HttpClient http, KalshiOptions options)
{
    public async Task<List<KalshiMarket>> GetMarketsAsync(CancellationToken ct)
    {
        var requestUri = $"{options.BaseUrl.TrimEnd('/')}/markets?limit={options.MaxMarkets}";
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var resp = await http.GetAsync(requestUri, ct);
                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                {
                    Console.WriteLine("[KALSHI] market fetch unauthorized; running read-only without credentials");
                    return [];
                }
                resp.EnsureSuccessStatusCode();
                var parsed = await resp.Content.ReadFromJsonAsync<KalshiMarketResponse>(cancellationToken: ct);
                return parsed?.Markets ?? [];
            }
            catch when (attempt < 3)
            {
                await Task.Delay(200 * attempt, ct);
            }
        }

        return [];
    }
}
