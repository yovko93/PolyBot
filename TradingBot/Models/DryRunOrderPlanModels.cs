namespace TradingBot.Models;

public enum BasketOrderPlanStatus { Draft, Validated, Rejected, Expired, PaperOnly }

public sealed record OrderIntent(
    string Id,
    string OpportunityId,
    string GroupKey,
    string Strategy,
    string MarketId,
    string ConditionId,
    string Question,
    string TokenId,
    string Outcome,
    string Side,
    string PositionSide,
    decimal Price,
    decimal Quantity,
    decimal EstimatedCost,
    string OrderType,
    string TimeInForce,
    bool ReduceOnly,
    bool DryRunOnly,
    DateTime CreatedAt
);

public sealed record BasketOrderPlan(
    string Id,
    string OpportunityId,
    string GroupKey,
    string Title,
    string Strategy,
    string ActiveProfile,
    bool DryRunOnly,
    DateTime CreatedAt,
    DateTime ExpiresAt,
    BasketOrderPlanStatus Status,
    int LegsCount,
    decimal PlannedQty,
    decimal GuaranteedPayout,
    decimal CostPerBasket,
    decimal TotalEstimatedCost,
    decimal ExpectedProfit,
    decimal NetEdge,
    decimal MaxNotional,
    IReadOnlyList<OrderIntent> Orders,
    IReadOnlyList<string> ValidationWarnings,
    IReadOnlyList<string> ValidationErrors
);
