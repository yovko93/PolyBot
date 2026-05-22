namespace TradingBot.Models;

public enum PreTradeDecision { Approved, Rejected }
public enum RejectionReason { None, NegativeEdge, NonPositiveExpectedProfit, EdgeAfterCostsTooLow, NonExecutableQuantity, MaxNotionalPerTrade, MaxDailyNotional, MaxOpenPositions, MaxExposurePerMarket, StaleOrderbook, MarketNotOpen, StrategyDisabled, ModeDisabled, KillSwitchEnabled, AlreadyExecuted, DuplicateOpportunity, LiveTradingDisabled }
public enum ExecutionResultStatus { Simulated, Rejected, Failed }
public enum FillStatus { NotSubmitted, Simulated, Submitted, PartiallyFilled, Filled, Cancelled, Failed }

public sealed record PreTradeValidationResult(bool IsValid, PreTradeDecision Decision, RejectionReason RejectionReason, IReadOnlyList<string> Warnings, decimal EffectiveEdgeAfterCosts, decimal MaxAllowedQuantity, decimal AdjustedQuantity, DateTime Timestamp);

public sealed record ExecutionLegResult(string Exchange, string MarketId, FillStatus FillStatus, decimal FilledQuantity, decimal RemainingQuantity, decimal AverageFillPrice);

public sealed record ExecutionResult(string Mode, ExecutionResultStatus Status, string OrderPlanId, string OpportunityId, IReadOnlyList<OrderPlanLeg> SimulatedOrders, RejectionReason RejectionReason, IReadOnlyList<ExecutionLegResult> LegResults, bool PartialFillRiskDetected, DateTime Timestamp);

public sealed record OrderPlan(string OrderPlanId, string OpportunityId, string Strategy, string ExecutionMode, DateTime CreatedAt, IReadOnlyList<OrderPlanLeg> Legs, decimal TotalNotional, decimal ExpectedPayout, decimal ExpectedProfit, decimal EdgePerShare, decimal FeesEstimate, decimal SlippageEstimate, decimal MaxSlippage, PreTradeValidationResult ValidationResult, string Status);

public sealed record OrderPlanLeg(string Exchange, string MarketId, string InstrumentId, string Side, string Outcome, decimal Price, decimal Quantity, decimal MaxPrice, decimal EstimatedCost, string OrderType, string? TimeInForce, bool DryRunOnly);

public sealed record RiskSnapshot(int OpenPositions, decimal TotalExposure, IReadOnlyDictionary<string, decimal> ExposureByMarket, IReadOnlyDictionary<string, decimal> ExposureByStrategy, decimal DailyNotional, IReadOnlyDictionary<string, int> RejectedByReason, int ConsecutiveFailures, bool KillSwitchEnabled, DateTime Timestamp);

public sealed record ExecutionOpportunity(string OpportunityId, string Strategy, string MarketId, decimal EdgePerShare, decimal ExpectedProfit, decimal Quantity, decimal Price, DateTime OrderbookTimestampUtc, bool MarketOpen = true, bool StrategyEnabled = true, decimal FeesPerShare = 0, decimal SlippagePerShare = 0);
public sealed record ExecutionAuditEntry(DateTime Timestamp, string EventType, string OpportunityId, string Message);
