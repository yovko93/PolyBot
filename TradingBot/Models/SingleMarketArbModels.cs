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

public record SingleMarketScanSummaryDto(
    DateTime TimestampUtc,
    long ScanId,
    int Scanned,
    int BookOk,
    int BothAsks,
    int DataQualityRejected,
    int BelowMinEdge,
    int PositiveEdge,
    int EdgeStable,
    int ExecutionReady,
    int FillPassed,
    int PaperOpened,
    decimal? BestEdgeSeen,
    string TopRejectReason,
    int TopRejectCount,
    IReadOnlyDictionary<string, int> RejectedByReason,
    IReadOnlyDictionary<string, int> DataQualityRejectedByReason);

public record SingleMarketDataQualityRejectSampleDto(
    DateTime TimestampUtc,
    string MarketId,
    string? ConditionId,
    string Title,
    string Reason,
    decimal? YesAsk,
    decimal? NoAsk,
    decimal RawSum,
    decimal EdgePerShare);

public record SingleMarketNearMissDto(
    DateTime TimestampUtc,
    string MarketId,
    string? ConditionId,
    string Title,
    decimal YesAsk,
    decimal NoAsk,
    decimal RawSum,
    decimal EdgePerShare,
    decimal RequiredImprovement);

public record SingleMarketArbSnapshotDto(
    DateTime TimestampUtc,
    long ScanId,
    SingleMarketScanSummaryDto Summary,
    IReadOnlyList<SingleMarketArbOpportunityDto> PositiveCandidates,
    IReadOnlyList<SingleMarketNearMissDto> TopNearMisses,
    IReadOnlyList<SingleMarketDataQualityRejectSampleDto> DataQualityRejectSamples,
    IReadOnlyList<SingleMarketPaperExecutionDto> PaperExecutions);
