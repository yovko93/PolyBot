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
    decimal? BestRejectedRawEdge,
    string TopRejectReason,
    int TopRejectCount,
    IReadOnlyDictionary<string, int> RejectedByReason,
    IReadOnlyDictionary<string, int> DataQualityRejectedByReason,
    int DataQualityRejectedRawPositive = 0,
    int ValidRawPositive = 0,
    int ValidAfterCostPositive = 0,
    int ValidAfterSafetyPositive = 0,
    int RejectedByFill = 0,
    int RejectedByDepth = 0,
    int RejectedByRisk = 0,
    int RejectedByPaperDiagnosticsLimitedGate = 0,
    decimal? BestRawEdge = null,
    decimal? BestAfterCostEdge = null,
    decimal? BestAfterSafetyEdge = null,
    decimal? BestExecutableEdge = null,
    string BestRejectedReason = "None");


public record SingleMarketFullCycleSummary(
    long CycleId,
    int BatchesSeen,
    int MarketsScanned,
    int DataQualityRejected,
    int BelowMinEdge,
    int PositiveEdge,
    int EdgeStable,
    int ExecutionReady,
    int FillPassed,
    int PaperOpened,
    IReadOnlyDictionary<string, int> RejectCountsByReason,
    decimal? BestValidEdge,
    decimal? BestRejectedRawEdge,
    int HighSeverityRejectCount,
    IReadOnlyList<SingleMarketDataQualityRejectSampleDto> SampledRejects);

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

public record SingleMarketOpportunityAuditDto(
    [property: JsonPropertyName("marketId")] string MarketId,
    [property: JsonPropertyName("conditionId")] string? ConditionId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("yesAsk")] decimal YesAsk,
    [property: JsonPropertyName("noAsk")] decimal NoAsk,
    [property: JsonPropertyName("rawCost")] decimal RawCost,
    [property: JsonPropertyName("rawEdge")] decimal RawEdge,
    [property: JsonPropertyName("afterCostEdge")] decimal AfterCostEdge,
    [property: JsonPropertyName("afterSafetyEdge")] decimal AfterSafetyEdge,
    [property: JsonPropertyName("availableQty")] decimal AvailableQty,
    [property: JsonPropertyName("executableQty")] decimal ExecutableQty,
    [property: JsonPropertyName("notionalAtCap")] decimal NotionalAtCap,
    [property: JsonPropertyName("rejectedReason")] string RejectedReason,
    [property: JsonPropertyName("dataQualityReason")] string? DataQualityReason,
    [property: JsonPropertyName("fillPassed")] bool FillPassed,
    [property: JsonPropertyName("depthPassed")] bool DepthPassed,
    [property: JsonPropertyName("riskPassed")] bool RiskPassed,
    [property: JsonPropertyName("paperDiagnosticsLimitedGatePassed")] bool PaperDiagnosticsLimitedGatePassed,
    [property: JsonPropertyName("timestampUtc")] DateTime TimestampUtc);

public record SingleMarketArbSnapshotDto(
    DateTime TimestampUtc,
    long ScanId,
    SingleMarketScanSummaryDto Summary,
    IReadOnlyList<SingleMarketArbOpportunityDto> PositiveCandidates,
    IReadOnlyList<SingleMarketNearMissDto> TopNearMisses,
    IReadOnlyList<SingleMarketOpportunityAuditDto> TopOpportunityAuditNearMisses,
    IReadOnlyList<SingleMarketDataQualityRejectSampleDto> DataQualityRejectSamples,
    IReadOnlyList<SingleMarketPaperExecutionDto> PaperExecutions);
