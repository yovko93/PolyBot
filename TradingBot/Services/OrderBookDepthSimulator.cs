using System.Globalization;
using TradingBot.Models;

namespace TradingBot.Services;

public class OrderBookDepthSimulator
{
    private readonly OrderBookService _orderBooks;

    public OrderBookDepthSimulator(OrderBookService orderBooks)
    {
        _orderBooks = orderBooks;
    }

    public async Task<FillSimulationResult?> SimulateBuyAsync(
        Market market,
        string outcome,
        decimal targetQuantity,
        CancellationToken ct = default)
    {
        var tokenId = GetTokenIdForOutcome(market, outcome);

        if (string.IsNullOrWhiteSpace(tokenId))
            return null;

        var book = await _orderBooks.GetOrderBookAsync(tokenId, ct);

        if (book?.asks == null || book.asks.Count == 0)
            return null;

        var levels = book.asks
            .Select(ToParsedLevel)
            .Where(x => x != null)
            .Select(x => x!.Value)
            .Where(x => x.Price > 0 && x.Size > 0)
            .OrderBy(x => x.Price)
            .ToList();

        return SimulateFill(
            market,
            outcome,
            FillSide.Buy,
            targetQuantity,
            levels
        );
    }

    public async Task<FillSimulationResult?> SimulateSellAsync(
        Market market,
        string outcome,
        decimal targetQuantity,
        CancellationToken ct = default)
    {
        var tokenId = GetTokenIdForOutcome(market, outcome);

        if (string.IsNullOrWhiteSpace(tokenId))
            return null;

        var book = await _orderBooks.GetOrderBookAsync(tokenId, ct);

        if (book?.bids == null || book.bids.Count == 0)
            return null;

        var levels = book.bids
            .Select(ToParsedLevel)
            .Where(x => x != null)
            .Select(x => x!.Value)
            .Where(x => x.Price > 0 && x.Size > 0)
            .OrderByDescending(x => x.Price)
            .ToList();

        return SimulateFill(
            market,
            outcome,
            FillSide.Sell,
            targetQuantity,
            levels
        );
    }

    private static FillSimulationResult SimulateFill(
        Market market,
        string outcome,
        FillSide side,
        decimal targetQuantity,
        List<(decimal Price, decimal Size)> levels)
    {
        var remaining = targetQuantity;
        var filled = 0m;
        var totalNotional = 0m;
        var usedLevels = new List<SimulatedFillLevel>();

        foreach (var level in levels)
        {
            if (remaining <= 0)
                break;

            var quantityAtLevel = Math.Min(remaining, level.Size);
            var notional = quantityAtLevel * level.Price;

            filled += quantityAtLevel;
            totalNotional += notional;
            remaining -= quantityAtLevel;

            usedLevels.Add(new SimulatedFillLevel(
                Price: level.Price,
                Quantity: quantityAtLevel,
                Notional: notional
            ));
        }

        var averagePrice = filled > 0
            ? totalNotional / filled
            : 0m;

        return new FillSimulationResult(
            MarketId: market.id,
            Question: market.question,
            Outcome: outcome.ToUpperInvariant(),
            Side: side,
            RequestedQuantity: targetQuantity,
            FilledQuantity: filled,
            AveragePrice: averagePrice,
            TotalNotional: totalNotional,
            IsComplete: filled >= targetQuantity,
            Levels: usedLevels
        );
    }

    private static (decimal Price, decimal Size)? ToParsedLevel(ClobBookLevel level)
    {
        if (!TryParseDecimal(level.price, out var price))
            return null;

        if (!TryParseDecimal(level.size, out var size))
            return null;

        return (price, size);
    }

    private static bool TryParseDecimal(string? value, out decimal result)
    {
        return decimal.TryParse(
            value,
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out result
        );
    }

    private static string? GetTokenIdForOutcome(Market market, string wantedOutcome)
    {
        wantedOutcome = wantedOutcome.Trim().ToLowerInvariant();

        if (market.clobTokenIds == null || market.clobTokenIds.Count < 2)
            return null;

        if (market.outcomes != null && market.outcomes.Count == market.clobTokenIds.Count)
        {
            for (int i = 0; i < market.outcomes.Count; i++)
            {
                var outcome = market.outcomes[i]
                    .Trim()
                    .ToLowerInvariant();

                if (outcome == wantedOutcome)
                    return market.clobTokenIds[i];
            }
        }

        // Polymarket binary fallback:
        // index 0 = YES, index 1 = NO
        if (wantedOutcome == "yes")
            return market.clobTokenIds[0];

        if (wantedOutcome == "no")
            return market.clobTokenIds[1];

        return null;
    }
}