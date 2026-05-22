using Newtonsoft.Json;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public record MarketDiscoverySummary(int MarketsDiscovered, int PagesFetched, int DuplicatesRemoved, int InactiveSkipped, int ActiveMarketsAvailable);

public class MarketDataService
{
    private readonly HttpClient _http;

    public MarketDataService(HttpClient http)
    {
        _http = http;
    }

    public async Task<(List<Market> Markets, MarketDiscoverySummary Summary)> GetMarketsAsync(TradingBotOptions options, CancellationToken ct = default)
    {
        var allMarkets = new List<Market>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageSize = options.DiscoveryPageSize;
        var cap = Math.Max(1, options.AbsoluteMaxMarketsSafetyCap);
        var softLimit = options.MaxMarketsToDiscover;
        var pages = 0;
        var duplicates = 0;
        var inactive = 0;

        for (var offset = 0; offset < cap; offset += pageSize)
        {
            if (ct.IsCancellationRequested) break;
            if (softLimit > 0 && allMarkets.Count >= softLimit) break;

            var url =
                "https://gamma-api.polymarket.com/markets" +
                "?active=true" +
                "&closed=false" +
                "&archived=false" +
                "&accepting_orders=true" +
                "&order=volume24hr" +
                "&ascending=false" +
                $"&limit={pageSize}" +
                $"&offset={offset}";

            List<Market> batch;
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(options.OrderbookRequestTimeoutMs));
                var json = await _http.GetStringAsync(url, timeoutCts.Token);
                batch = JsonConvert.DeserializeObject<List<Market>>(json) ?? new List<Market>();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MARKET DATA ERROR] {ex.Message}");
                break;
            }

            pages++;
            if (options.LogDiscoveryPages)
                Console.WriteLine($"[DISCOVERY] Page={pages} Count={batch.Count} TotalSoFar={allMarkets.Count + batch.Count}");
            if (batch.Count == 0) break;

            foreach (var market in batch)
            {
                if (market is null) continue;
                if (market.active != true || market.closed == true || market.archived == true || market.accepting_orders != true)
                {
                    inactive++;
                    continue;
                }

                var key = $"{market.conditionId}|{market.id}|{string.Join(',', market.clobTokenIds ?? new List<string>())}";
                if (!seen.Add(key))
                {
                    duplicates++;
                    continue;
                }

                if (market.liquidityNum < options.MinLiquidity || market.volume24hrNum < options.MinVolume24h)
                {
                    inactive++;
                    continue;
                }

                allMarkets.Add(market);
                if (softLimit > 0 && allMarkets.Count >= softLimit) break;
                if (allMarkets.Count >= cap) break;
            }

            if (allMarkets.Count >= cap) break;
        }

        if (options.LogDiscoveryPages)
            Console.WriteLine($"[DISCOVERY] Completed ActiveMarkets={allMarkets.Count} Pages={pages}");
        var summary = new MarketDiscoverySummary(allMarkets.Count, pages, duplicates, inactive, allMarkets.Count);
        return (allMarkets, summary);
    }
}
