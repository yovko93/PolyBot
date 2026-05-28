using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed class DryRunFillSimulator
{
    public FillSimulationResult Simulate(
        BasketOrderPlan plan,
        IReadOnlyDictionary<string, CachedOrderBookSnapshot?> orderbooksByToken,
        IReadOnlyDictionary<string, BinaryOrderBookSnapshot?> snapshotsByMarket,
        ExecutionOptions options,
        DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var warnings = new List<string>();
        var errors = new List<string>();
        var legResults = new List<LegFillSimulation>();
        var maxAge = TimeSpan.FromMilliseconds(Math.Max(1, options.MaxOrderbookAgeMsForFillSimulation));

        foreach (var order in plan.Orders)
        {
            var leg = SimulateLeg(order, orderbooksByToken, snapshotsByMarket, maxAge, now, warnings, errors);
            legResults.Add(leg);
        }

        var availableByLeg = legResults.Select(x => x.AvailableQtyAtOrBelowLimit).ToArray();
        var fullyFillableQty = availableByLeg.Length == 0 ? 0m : availableByLeg.Min();
        var safeExecutableQty = Math.Min(plan.PlannedQty, fullyFillableQty);
        var unsafeQty = Math.Max(0m, plan.PlannedQty - safeExecutableQty);
        var fillableRatio = plan.PlannedQty > 0 ? safeExecutableQty / plan.PlannedQty : 0m;
        var partialFillRisk = unsafeQty > 0m || legResults.Any(x => x.FillStatus != FillSimulationStatus.FullyFillable);
        var allOrNoneRecommended = options.RequireAllLegsFillable || !options.AllowPartialBasketFill;

        FillSimulationStatus status;
        if (legResults.Any(x => x.FillStatus == FillSimulationStatus.MissingOrderbook)) status = FillSimulationStatus.MissingOrderbook;
        else if (legResults.Any(x => x.FillStatus == FillSimulationStatus.StaleOrderbook)) status = FillSimulationStatus.StaleOrderbook;
        else if (legResults.Any(x => x.FillStatus == FillSimulationStatus.Rejected)) status = FillSimulationStatus.Rejected;
        else if (legResults.All(x => x.FillStatus == FillSimulationStatus.FullyFillable) && safeExecutableQty >= plan.PlannedQty && fillableRatio >= options.MinFillableQtyRatio) status = FillSimulationStatus.FullyFillable;
        else if (safeExecutableQty > 0m) status = FillSimulationStatus.PartiallyFillable;
        else status = FillSimulationStatus.NotFillable;

        if (options.RequireAllLegsFillable && status != FillSimulationStatus.FullyFillable) errors.Add("RequireAllLegsFillable");
        if (!options.AllowPartialBasketFill && status == FillSimulationStatus.PartiallyFillable) errors.Add("PartialBasketFillDisabled");
        if (fillableRatio < options.MinFillableQtyRatio) warnings.Add($"FillableQtyRatioBelowMinimum:{fillableRatio:0.####}");

        var estimatedFilledCost = legResults.Sum(x => RepriceCost(x, safeExecutableQty));
        var worstCaseFilledLegs = legResults.Where(x => x.SimulatedFilledQty > 0m).Sum(x => x.SimulatedCost);
        var guaranteedPayout = plan.GuaranteedPayout * safeExecutableQty;
        var estimatedExpectedProfit = guaranteedPayout - estimatedFilledCost;
        var worstCaseExposure = Math.Max(0m, worstCaseFilledLegs - guaranteedPayout);

        return new FillSimulationResult(
            Guid.NewGuid().ToString("N"),
            plan.Id,
            plan.GroupKey,
            plan.Strategy,
            now,
            status,
            plan.Orders.Count,
            legResults.Count(x => x.FillStatus == FillSimulationStatus.FullyFillable),
            legResults.Count(x => x.FillStatus == FillSimulationStatus.PartiallyFillable),
            legResults.Count(x => x.FillStatus is FillSimulationStatus.Rejected or FillSimulationStatus.MissingOrderbook or FillSimulationStatus.StaleOrderbook or FillSimulationStatus.NotFillable),
            plan.PlannedQty,
            fullyFillableQty,
            safeExecutableQty,
            unsafeQty,
            estimatedFilledCost,
            worstCaseFilledLegs,
            estimatedExpectedProfit,
            worstCaseExposure,
            partialFillRisk,
            allOrNoneRecommended,
            warnings,
            errors,
            legResults);
    }

    private static LegFillSimulation SimulateLeg(
        OrderIntent order,
        IReadOnlyDictionary<string, CachedOrderBookSnapshot?> orderbooksByToken,
        IReadOnlyDictionary<string, BinaryOrderBookSnapshot?> snapshotsByMarket,
        TimeSpan maxAge,
        DateTime now,
        List<string> warnings,
        List<string> errors)
    {
        snapshotsByMarket.TryGetValue(order.MarketId, out var marketSnapshot);
        if (marketSnapshot is null)
        {
            errors.Add($"MissingOrderbook:{order.MarketId}");
            return Empty(order, FillSimulationStatus.MissingOrderbook, "MissingOrderbook", null, false);
        }

        if (!string.Equals(order.PositionSide, "NO", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(order.TokenId, marketSnapshot.NoTokenId, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"RejectedWrongToken:{order.MarketId}");
            return Empty(order, FillSimulationStatus.Rejected, "OrderTokenDoesNotMatchNoToken", null, false);
        }

        orderbooksByToken.TryGetValue(order.TokenId, out var book);
        if (book is null)
        {
            var topNoAsk = marketSnapshot.NoAsk;
            if (topNoAsk is null)
            {
                errors.Add($"MissingOrderbook:{order.MarketId}");
                return Empty(order, FillSimulationStatus.MissingOrderbook, "MissingOrderbook", null, false);
            }

            book = new CachedOrderBookSnapshot(order.TokenId, order.MarketId, now, new[] { topNoAsk }, Array.Empty<BookQuote>());
            warnings.Add($"DepthUnavailableUsedTopOfBook:{order.MarketId}");
        }

        var isStale = now - book.TimestampUtc > maxAge;
        if (isStale)
        {
            errors.Add($"StaleOrderbook:{order.MarketId}");
            return Empty(order, FillSimulationStatus.StaleOrderbook, "StaleOrderbook", book.TimestampUtc, true);
        }

        var levels = book.Asks
            .Where(x => x.Price > 0m && x.Size > 0m && x.Price <= order.Price)
            .OrderBy(x => x.Price)
            .ToArray();
        var available = levels.Sum(x => x.Size);
        if (available <= 0m)
        {
            warnings.Add($"NotFillable:{order.MarketId}");
            return new LegFillSimulation(order.MarketId, order.ConditionId, order.Question, order.TokenId, order.Side, order.PositionSide, order.Quantity, order.Price, 0m, 0m, 0m, 0m, FillSimulationStatus.NotFillable, "NoAskAtOrBelowLimit", book.TimestampUtc, false);
        }

        var fillQty = Math.Min(order.Quantity, available);
        var remaining = fillQty;
        var cost = 0m;
        foreach (var level in levels)
        {
            if (remaining <= 0m) break;
            var take = Math.Min(remaining, level.Size);
            cost += take * level.Price;
            remaining -= take;
        }

        var avg = fillQty > 0m ? cost / fillQty : 0m;
        var status = fillQty >= order.Quantity ? FillSimulationStatus.FullyFillable : FillSimulationStatus.PartiallyFillable;
        return new LegFillSimulation(order.MarketId, order.ConditionId, order.Question, order.TokenId, order.Side, order.PositionSide, order.Quantity, order.Price, available, fillQty, avg, cost, status, status == FillSimulationStatus.FullyFillable ? null : "InsufficientQuantityAtLimit", book.TimestampUtc, false);
    }

    private static LegFillSimulation Empty(OrderIntent order, FillSimulationStatus status, string reason, DateTime? ts, bool stale)
        => new(order.MarketId, order.ConditionId, order.Question, order.TokenId, order.Side, order.PositionSide, order.Quantity, order.Price, 0m, 0m, 0m, 0m, status, reason, ts, stale);

    private static decimal RepriceCost(LegFillSimulation leg, decimal qty)
        => leg.SimulatedFilledQty <= 0m || qty <= 0m ? 0m : qty * leg.SimulatedAveragePrice;
}
