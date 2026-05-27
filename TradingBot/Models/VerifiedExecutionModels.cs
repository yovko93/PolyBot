namespace TradingBot.Models;

public sealed record VerifiedMultiOutcomeOpportunityLeg(
    string MarketId,
    string ConditionId,
    string Question,
    string Outcome,
    string NoTokenId,
    decimal NoAsk,
    decimal NoAskQuantity,
    string NoAskSource,
    decimal PlannedQty,
    decimal PlannedCost
);

public sealed record VerifiedMultiOutcomeOpportunity(
    string Id,
    string Strategy,
    string GroupKey,
    string Title,
    string VerificationStatus,
    int LegsCount,
    decimal GuaranteedPayout,
    decimal NoAskSum,
    decimal GrossEdge,
    decimal NetEdge,
    string ActiveCostProfile,
    decimal ExecutableQty,
    decimal ExpectedProfit,
    decimal MaxNotional,
    decimal EstimatedCost,
    string Status,
    IReadOnlyList<VerifiedMultiOutcomeOpportunityLeg> Legs
);

public sealed record VerifiedBasketPreTradeValidationResult(
    bool Approved,
    string Reason,
    decimal NetEdge,
    decimal Quantity,
    decimal EstimatedCost,
    decimal ExpectedProfit,
    string? IdempotencyKey = null
);

public sealed record ExecutionAuditEvent(
    DateTime Timestamp,
    string OpportunityId,
    string GroupKey,
    string Strategy,
    string Stage,
    string Status,
    string Reason,
    decimal NetEdge,
    decimal ExpectedProfit,
    decimal Cost,
    decimal Qty,
    string Details
);
