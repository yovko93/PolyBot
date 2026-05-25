namespace TradingBot.Models;

public enum OpportunitySkipReason
{
    NegativeEdge,
    ZeroEdge,
    BelowMinEdgeThreshold,
    BelowMinExpectedProfit,
    InsufficientLiquidity,
    MissingYesAsk,
    MissingNoAsk,
    StaleOrderbook,
    MarketClosed,
    ExceedsMaxNotional,
    AlreadyExecuted,
    RiskLimitExceeded,
    StrategyDisabled,
    InvalidPriceNormalization,
    Unknown
}

public record SkipReasonSummary(string Reason, int Count, string? ExampleMarket, decimal? ExampleEdge);
public record StrategyBreakdownItem(string StrategyName, int MarketsEvaluated, int CandidatesGenerated, int PositiveEdgeCount, int ExecutableCount, decimal BestEdge, int NearMissCount, string MostCommonSkipReason);
public record NearMissOpportunity(string MarketId, string Question, string Strategy, decimal YesAsk, decimal NoAsk, decimal GrossCost, decimal EstimatedFees, decimal SlippageBuffer, decimal NetCost, decimal EdgePerShare, decimal ExpectedProfit, decimal ExecutableQty, decimal MissingToBreakEven, string SkipReason, int Rank);
public record OpportunityDiagnosticsSnapshot(string ScanId, DateTime Timestamp, int MarketsScanned, int BooksLoaded, int BooksWithBothSides, int CandidatesEvaluated, int PositiveEdgeCount, int ExecutableCount, int NearMissCount, decimal BestEdgeSeen, decimal WorstEdgeSeen, decimal AverageEdge, decimal MedianEdge, decimal ClosestToArbitrage, IReadOnlyList<SkipReasonSummary> SkipReasons, IReadOnlyList<StrategyBreakdownItem> StrategyBreakdown, long DurationMs, IReadOnlyList<NearMissOpportunity> NearMissTopN, IReadOnlyDictionary<string, int> ThresholdSimulation);
