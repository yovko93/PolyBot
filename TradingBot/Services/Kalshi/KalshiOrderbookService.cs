using System.Net;
using System.Net.Http.Json;
using TradingBot.Models.Kalshi;
using TradingBot.Models.Normalized;

namespace TradingBot.Services.Kalshi;

public class KalshiOrderbookService(HttpClient http, KalshiOptions options)
{
    public async Task<ExchangeOrderbook?> GetNormalizedOrderbookAsync(string ticker, CancellationToken ct)
    {
        var uri = $"{options.BaseUrl.TrimEnd('/')}/markets/{ticker}/orderbook";
        try
        {
            using var resp = await http.GetAsync(uri, ct);
            if (resp.StatusCode is HttpStatusCode.TooManyRequests)
            {
                Console.WriteLine("[KALSHI] rate limited orderbook");
                return null;
            }
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                Console.WriteLine("[KALSHI] orderbook endpoint requires auth");
                return null;
            }
            resp.EnsureSuccessStatusCode();
            var ob = await resp.Content.ReadFromJsonAsync<KalshiOrderbookResponse>(cancellationToken: ct);
            if (ob?.Orderbook is null) return null;
            return KalshiOrderbookNormalizer.Normalize(ticker, ticker, ob.Orderbook, uri);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[KALSHI] orderbook fetch error {ticker}: {ex.Message}");
            return null;
        }
    }
}

public static class KalshiOrderbookNormalizer
{
    public static ExchangeOrderbook Normalize(string marketId, string title, KalshiOrderbook orderbook, string source)
    {
        decimal ToProb(decimal p) => p > 1 ? p / 100m : p;
        var yesBid = orderbook.Yes?.OrderByDescending(x => x.Price).FirstOrDefault();
        var noBid = orderbook.No?.OrderByDescending(x => x.Price).FirstOrDefault();
        var bestYesBid = yesBid is null ? (decimal?)null : ToProb(yesBid.Price);
        var bestNoBid = noBid is null ? (decimal?)null : ToProb(noBid.Price);
        decimal? bestYesAsk = bestNoBid.HasValue ? 1m - bestNoBid.Value : null;
        decimal? bestNoAsk = bestYesBid.HasValue ? 1m - bestYesBid.Value : null;
        var yesQty = noBid?.Quantity ?? 0m;
        var noQty = yesBid?.Quantity ?? 0m;
        return new ExchangeOrderbook("KALSHI", marketId, title, bestYesBid, bestYesAsk, bestNoBid, bestNoAsk, yesQty, noQty, DateTime.UtcNow, "active", source);
    }
}
