using System.Text.Json.Serialization;

namespace TradingBot.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SingleMarketArbState
{
    NotExecutable,
    CandidateDetected,
    EdgePending,
    EdgeStable,
    ExecutionReadinessPending,
    ExecutionStable,
    DryRunPlanCreated,
    FillSimulationPassed,
    PaperOpened,
    SuppressedDuplicate,
    Rejected
}

public record SingleMarketArbOpportunityDto(
    string Id,
    DateTime TimestampUtc,
    string MarketId,
    string? ConditionId,
    string Question,
    string Strategy,
    SingleMarketArbState State,
    decimal YesAsk,
    decimal NoAsk,
    decimal RawAskSum,
    decimal EdgePerShare,
    decimal ExpectedProfit,
    decimal Quantity,
    decimal PlannedNotional,
    string DataQualityStatus,
    string FillSimulationStatus,
    string PaperStatus,
    string? Reason,
    int ConsecutiveEdgeScans,
    int ConsecutiveExecutionReadyScans,
    bool PaperOnly);

public record SingleMarketPaperExecutionDto(
    string Id,
    DateTime TimestampUtc,
    string MarketId,
    string Question,
    string Strategy,
    decimal Quantity,
    decimal SimulatedYesPrice,
    decimal SimulatedNoPrice,
    decimal SimulatedCost,
    decimal EdgePerShare,
    decimal ExpectedProfit,
    decimal CashAfter,
    decimal LockedAfter,
    decimal EquityAfter,
    string PaperStatus,
    bool PaperOnly);
