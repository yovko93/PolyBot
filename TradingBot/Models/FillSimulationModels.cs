namespace TradingBot.Models;

public enum FillSimulationStatus
{
    FullyFillable,
    PartiallyFillable,
    NotFillable,
    Rejected,
    StaleOrderbook,
    MissingOrderbook
}

public sealed record LegFillSimulation(
    string MarketId,
    string ConditionId,
    string Question,
    string TokenId,
    string Side,
    string PositionSide,
    decimal RequestedQty,
    decimal LimitPrice,
    decimal AvailableQtyAtOrBelowLimit,
    decimal SimulatedFilledQty,
    decimal SimulatedAveragePrice,
    decimal SimulatedCost,
    FillSimulationStatus FillStatus,
    string? RejectionReason,
    DateTime? BookTimestamp,
    bool IsStale
);

public sealed record FillSimulationResult(
    string Id,
    string OrderPlanId,
    string GroupKey,
    string Strategy,
    DateTime CreatedAt,
    FillSimulationStatus Status,
    int RequestedOrdersCount,
    int SimulatedFilledOrdersCount,
    int SimulatedPartialOrdersCount,
    int SimulatedRejectedOrdersCount,
    decimal RequestedQty,
    decimal FullyFillableQty,
    decimal SafeExecutableQty,
    decimal UnsafeQty,
    decimal EstimatedFilledCost,
    decimal EstimatedWorstCaseCost,
    decimal EstimatedExpectedProfit,
    decimal WorstCaseExposure,
    decimal PlannedGrossEdgePerBasket,
    decimal PlannedNetEdgePerBasket,
    decimal FillAdjustedGrossEdgePerBasket,
    decimal FillAdjustedNetEdgePerBasket,
    decimal PlannedExpectedProfit,
    decimal FillAdjustedExpectedProfit,
    decimal EstimatedFilledCostPerBasket,
    decimal SimulatedNoAskSum,
    decimal EstimatedFees,
    decimal EstimatedSlippage,
    decimal SafetyBuffer,
    string ProfileUsed,
    bool PartialFillRisk,
    bool AllOrNoneRecommended,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Errors,
    IReadOnlyList<LegFillSimulation> LegResults
)
{
    public string? WorstLeg => LegResults
        .OrderBy(x => x.AvailableQtyAtOrBelowLimit)
        .ThenByDescending(x => x.IsStale)
        .FirstOrDefault(x => x.FillStatus != FillSimulationStatus.FullyFillable)?.MarketId
        ?? LegResults.OrderBy(x => x.AvailableQtyAtOrBelowLimit).FirstOrDefault()?.MarketId;
}

public sealed record CachedOrderBookSnapshot(
    string TokenId,
    string MarketId,
    DateTime TimestampUtc,
    IReadOnlyList<BookQuote> Asks,
    IReadOnlyList<BookQuote> Bids
);
