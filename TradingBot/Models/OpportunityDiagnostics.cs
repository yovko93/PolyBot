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
public record StrategyDescriptor(string StrategyType, string ExpectedFrequency, string WhyUsuallyNegative, IReadOnlyList<string> BetterAlternativeStrategies);
public record StrategyBreakdownItem(string StrategyName, int MarketsEvaluated, int Candidates, int RawPositive, int NetPositive, int Executable, decimal BestRawEdge, decimal BestNetEdge, decimal AverageNetEdge, int NearMissCount, string TopSkipReason);
public record NearMissOpportunity(string MarketId, string Question, string Strategy, decimal YesAsk, decimal NoAsk, decimal GrossCost, decimal RawEdge, decimal EstimatedFees, decimal SlippageBuffer, decimal SafetyBuffer, decimal NetCost, decimal NetEdge, decimal ExecutableQty, decimal ExpectedProfit, string SkipReason, decimal MissingToRawBreakEven, decimal MissingToNetBreakEven, int Rank);
public record ThresholdSimulationSnapshot(int ActualPositive, int ZeroBufferPositive, int ZeroFeePositive, int RawPositive, int RelaxedMinEdgePositive, int RelaxedMinProfitPositive);
public record StrategyRecommendationSnapshot(string Message, IReadOnlyList<string> RecommendedStrategies);
public record MultiOutcomeGroupDiagnosticsSnapshot(int GroupsDetected, int GroupsWith2Outcomes, int GroupsWith3PlusOutcomes, int GroupsEvaluated, int GroupsWithAllNoPrices, decimal BestNoBasketEdge, int ExecutableNoBasketCount, string SkippedGroupReason);
public record OpportunityDiagnosticsSnapshot(string ScanId, DateTime Timestamp, int MarketsScanned, int BooksLoaded, int BooksWithBothSides, int CandidatesEvaluated, int RawPositiveCount, int NetPositiveCount, int ExecutableCount, int NearMissCount, decimal BestRawEdge, decimal BestNetEdge, decimal AverageRawEdge, decimal AverageNetEdge, decimal BestGrossCost, decimal BestNetCost, decimal FeesApplied, decimal SlippageApplied, decimal SafetyBufferApplied, IReadOnlyList<SkipReasonSummary> SkipReasons, IReadOnlyList<StrategyBreakdownItem> StrategyBreakdown, long DurationMs, IReadOnlyList<NearMissOpportunity> NearMissTopN, ThresholdSimulationSnapshot ThresholdSimulation, StrategyRecommendationSnapshot StrategyRecommendation, StrategyDescriptor SingleMarketStrategyDescription, MultiOutcomeGroupDiagnosticsSnapshot MultiOutcomeGroupDiagnostics);
