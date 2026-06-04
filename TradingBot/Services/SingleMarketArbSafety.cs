using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record SingleMarketDataQualityResult(bool IsValid, string Status, string Reason);
public sealed record SingleMarketFillSimulationResult(bool Passed, string Reason, decimal Quantity, decimal FullyFillableQty, decimal YesAveragePrice, decimal NoAveragePrice, decimal SimulatedCost, decimal AdjustedEdgePerShare, decimal ExpectedProfit);

public class SingleMarketDataQualityValidator(SingleMarketArbOptions options)
{
    public SingleMarketDataQualityResult Validate(Market market, BinaryOrderBookSnapshot book, DateTime nowUtc)
    {
        if (book.YesAsk is null) return Reject("MissingYesAsk");
        if (book.NoAsk is null) return Reject("MissingNoAsk");
        if (book.YesAsk.Price <= 0m || book.YesAsk.Price > 1m) return Reject("InvalidYesAsk");
        if (book.NoAsk.Price <= 0m || book.NoAsk.Price > 1m) return Reject("InvalidNoAsk");
        if (string.Equals(book.YesTokenId, book.NoTokenId, StringComparison.OrdinalIgnoreCase)) return Reject("SameYesNoTokenId");
        if (!string.Equals(market.id, book.MarketId, StringComparison.OrdinalIgnoreCase)) return Reject("MarketIdMismatch");
        if (string.IsNullOrWhiteSpace(market.conditionId)) return Reject("MissingConditionId");
        if (!IsOutcomeMappingVerified(market)) return Reject("TokenOutcomeMappingUnverified");
        var ageMs = Math.Abs((nowUtc - book.TimestampUtc).TotalMilliseconds);
        if (ageMs > options.MaxOrderbookAgeMs) return Reject("StaleOrderbook");
        var rawSum = book.YesAsk.Price + book.NoAsk.Price;
        if (options.RejectSuspiciousAskSum && (rawSum < options.MinReasonableYesNoAskSum || rawSum > options.MaxReasonableYesNoAskSum)) return Reject("SuspiciousYesNoAskSum");
        return new(true, "Passed", "Ok");
    }

    private static bool IsOutcomeMappingVerified(Market market)
    {
        if (market.outcomes.Count < 2 || market.clobTokenIds.Count < 2) return false;
        var hasYes = market.outcomes.Any(x => string.Equals(x, "yes", StringComparison.OrdinalIgnoreCase));
        var hasNo = market.outcomes.Any(x => string.Equals(x, "no", StringComparison.OrdinalIgnoreCase));
        return hasYes && hasNo;
    }

    private static SingleMarketDataQualityResult Reject(string reason) => new(false, "Rejected", reason);
}

public class SingleMarketFillSimulator
{
    public SingleMarketFillSimulationResult Simulate(BinaryOrderBookSnapshot book, decimal plannedQty, decimal feeBuffer, decimal slippageBuffer)
    {
        if (book.YesAsk is null) return Reject("MissingYesAsk", plannedQty, 0m);
        if (book.NoAsk is null) return Reject("MissingNoAsk", plannedQty, 0m);
        var fillable = Math.Min(book.YesAsk.Size, book.NoAsk.Size);
        if (fillable < plannedQty) return Reject("PartialFillRisk", plannedQty, fillable);
        var cost = plannedQty * (book.YesAsk.Price + book.NoAsk.Price + feeBuffer + slippageBuffer);
        var edge = 1m - (book.YesAsk.Price + book.NoAsk.Price + feeBuffer + slippageBuffer);
        return new(true, "FullyFillable", plannedQty, fillable, book.YesAsk.Price, book.NoAsk.Price, cost, edge, edge * plannedQty);
    }

    private static SingleMarketFillSimulationResult Reject(string reason, decimal qty, decimal fillable) => new(false, reason, qty, fillable, 0m, 0m, 0m, 0m, 0m);
}
