using System.ComponentModel.DataAnnotations;

namespace TradingBot.Options;

public class TradingBotOptions
{
    public const string SectionName = "TradingBot";
    public const string LegacyScannerSectionName = "Scanner";

    [Range(1, int.MaxValue)] public int ScanIntervalMs { get; set; } = 3000;
    [Range(1, int.MaxValue)] public int MaxConcurrentRequests { get; set; } = 5;
    public string Mode { get; set; } = "AllPaginatedRolling";
    [Range(0, int.MaxValue)] public int MaxMarketsToDiscover { get; set; } = 0;
    [Range(1, int.MaxValue)] public int AbsoluteMaxMarketsSafetyCap { get; set; } = 10000;
    [Range(1, int.MaxValue)] public int DiscoveryPageSize { get; set; } = 200;
    [Range(1, int.MaxValue)] public int ScanBatchSize { get; set; } = 250;
    [Range(1, int.MaxValue)] public int MaxOrderbooksPerCycle { get; set; } = 500;
    [Range(1, int.MaxValue)] public int MaxConcurrentOrderbookRequests { get; set; } = 20;
    [Range(1, int.MaxValue)] public int FullDiscoveryIntervalMinutes { get; set; } = 10;
    public bool EnableRollingScan { get; set; } = true;
    public bool PriorityMode { get; set; } = true;
    [Range(0, 100)] public int HighPriorityBatchPercent { get; set; } = 70;
    [Range(0, 100)] public int LowPriorityBatchPercent { get; set; } = 30;
    [Range(1, int.MaxValue)] public int RecentlyPositiveEdgeBoostMinutes { get; set; } = 30;
    [Range(0.0, double.MaxValue)] public decimal MinLiquidity { get; set; } = 0m;
    [Range(0.0, double.MaxValue)] public decimal MinVolume24h { get; set; } = 0m;
    public bool LogNoOpportunityCycles { get; set; } = false;
    public bool LogEmptyOpportunityCycles { get; set; } = false;
    public bool LogExecutableRankingOnlyWhenNotEmpty { get; set; } = true;
    public bool LogDiscoveryPages { get; set; } = false;
    public bool LogDiscoverySummary { get; set; } = true;
    public bool LogRawMarketSamples { get; set; } = false;
    [Range(1, 50)] public int RawMarketSampleCount { get; set; } = 3;
    [Range(0, int.MaxValue)] public int MarketScanLimit { get; set; } = 1000;
    public bool UseAllDiscoveredMarkets { get; set; } = true;
    [Range(0, int.MaxValue)] public int MaxMarketsInPool { get; set; } = 0;
    [Range(0, int.MaxValue)] public int PriorityPoolLimit { get; set; } = 0;
    public bool LogPrefetchSummary { get; set; } = true;
    [Range(100, int.MaxValue)] public int OrderbookRequestTimeoutMs { get; set; } = 3000;
    [Range(1, int.MaxValue)] public int RateLimitBackoffMs { get; set; } = 1500;
    [Range(0.0, double.MaxValue)] public decimal LogMinEdgeToLog { get; set; } = 0.001m;
    public bool ShowNegativeEdgeOpportunities { get; set; } = false;
    public bool ShowZeroEdgeOpportunities { get; set; } = false;
    public bool LogScanSummary { get; set; } = true;
    public bool LogPrefetchDetails { get; set; } = false;
    public bool LogCompactScanSummary { get; set; } = true;
    public bool LogEveryScanCycle { get; set; } = true;
    public bool LogEmptyExecutableRanking { get; set; } = false;
    public bool LogOnlyExecutableOpportunities { get; set; } = false;
    public bool EnableLiveExecution { get; set; } = false;
    public bool EnablePaperTrading { get; set; } = true;
    public bool PaperOnly { get; set; } = true;
    public bool EnableExperimentalProfilePaper { get; set; } = false;
    public string ExperimentalPaperProfile { get; set; } = "PolymarketApprox";
    public bool RequireStableExperimentalSignals { get; set; } = true;
    public int RequiredConsecutiveExperimentalScans { get; set; } = 5;
    public decimal MinExperimentalNetEdgePerBasket { get; set; } = 0.001m;
    public int ExperimentalCooldownMinutes { get; set; } = 60;
    public bool AllowRawOnlyPaper { get; set; } = false;
    public bool AllowOrderbookOnlyPaper { get; set; } = false;
    public string ExecutionMode { get; set; } = "PAPER";
    [Range(0.0001, double.MaxValue)] public decimal MinEdgePerShare { get; set; } = 0.003m;
    [Range(0.0, double.MaxValue)] public decimal MinExpectedProfit { get; set; } = 0.25m;
    [Range(0.01, double.MaxValue)] public decimal MaxNotionalPerTrade { get; set; } = 100m;
    [Range(1, int.MaxValue)] public int MaxOpenPositions { get; set; } = 5;
    [Range(0.01, double.MaxValue)] public decimal MinNotionalPerTrade { get; set; } = 5m;
    [Range(0.01, double.MaxValue)] public decimal MaxLockedCapital { get; set; } = 300m;
    [Range(0.01, double.MaxValue)] public decimal MaxExposurePerGroup { get; set; } = 100m;
    [Range(0.0, double.MaxValue)] public decimal SingleMarketSlippage { get; set; } = 0.001m;
    [Range(0.0, double.MaxValue)] public decimal SingleMarketFees { get; set; } = 0.001m;
    [Required] public string ListenUrl { get; set; } = "http://localhost:5000";
    [Range(1000, int.MaxValue)] public int HeartbeatIntervalMs { get; set; } = 3000;
    [Range(1, int.MaxValue)] public int ExternalApiTimeoutSeconds { get; set; } = 10;
    public DiagnosticsOptions Diagnostics { get; set; } = new();
    public MultiOutcomeArbitrageOptions MultiOutcomeArbitrage { get; set; } = new();
    public MultiOutcomeLoggingOptions Logging { get; set; } = new();
    public MultiOutcomeReviewOptions MultiOutcomeReview { get; set; } = new();
    public RuntimeStateOptions RuntimeState { get; set; } = new();
    public MarketDiscoveryOptions MarketDiscovery { get; set; } = new();
    public SignalROptions SignalR { get; set; } = new();
    public RuntimeHealthOptions RuntimeHealth { get; set; } = new();
}
public class MarketDiscoveryOptions
{
    public int RequestTimeoutMs { get; set; } = 15000;
    public int MaxRetriesPerPage { get; set; } = 3;
    public int RetryBackoffMs { get; set; } = 1000;
    public bool ContinueWithPartialDiscoveryOnError { get; set; } = true;
    public bool TreatSafetyCapAsWarning { get; set; } = false;
}

public class DiagnosticsOptions
{
    public bool EnableOpportunityDiagnostics { get; set; } = true;
    public bool TrackNearMisses { get; set; } = true;
    public int NearMissTopN { get; set; } = 25;
    public decimal NearMissMaxNegativeNetEdge { get; set; } = 0.02m;
    public bool IncludeRawEdgeBreakdown { get; set; } = true;
    public bool IncludeNegativeEdgeInDiagnostics { get; set; } = true;
    public bool IncludeNegativeEdgeInMainOpportunities { get; set; } = false;
    public bool LogNearMissSummary { get; set; } = true;
    public bool LogNearMissDetails { get; set; } = false;
    public bool EnableThresholdSimulation { get; set; } = true;
    public bool LogInsufficientLiquiditySamples { get; set; } = true;
    public int InsufficientLiquiditySampleCount { get; set; } = 10;
    public bool EnableNearMissDiagnostics { get; set; } = true;
    public bool EnableMultiOutcomeNearMisses { get; set; } = true;
    public bool DebuggerSafeMode { get; set; } = false;
    public int MultiOutcomeNearMissTopN { get; set; } = 25;
    public decimal MultiOutcomeNearMissMaxNegativeEdge { get; set; } = 0.05m;
}

public class MultiOutcomeArbitrageOptions
{
    public bool Enabled { get; set; } = true;
    public bool AllowVerifiedGroups { get; set; } = true;
    public bool AllowHighConfidenceGroups { get; set; } = false;
    public bool AllowCandidateGroupsForExecution { get; set; } = false;
    public bool AllowGenericGroupsForExecution { get; set; } = false;
    public bool AllowlistOnlyExecution { get; set; } = true;
    public decimal MaxEdgePerBasket { get; set; } = 0.20m;
    public decimal MaxExpectedProfitRatio { get; set; } = 0.50m;
    public decimal SuspiciousMinCostPerBasket { get; set; } = 0.01m;
    public int MinOutcomes { get; set; } = 2;
    public int MaxOutcomes { get; set; } = 50;
    public decimal MinMultiOutcomeEdge { get; set; } = 0.001m;
    public decimal MinExpectedProfit { get; set; } = 0.10m;
    public decimal MaxNotionalPerGroup { get; set; } = 100m;
    public decimal SlippageBufferPerLeg { get; set; } = 0.0005m;
    public decimal FeePerLeg { get; set; } = 0.001m;
    public decimal SafetyBufferPerGroup { get; set; } = 0.001m;
    public decimal NearExecutableCostReductionThreshold { get; set; } = 0.01m;
    public decimal FarFromExecutableCostReductionThreshold { get; set; } = 0.05m;
    public bool EnableSensitivityDiagnostics { get; set; } = true;
    public bool RequireAllNoPrices { get; set; } = true;
    public bool RequireAllMarketsActive { get; set; } = true;
    public decimal MinExecutableQty { get; set; } = 1m;
    public bool EvaluateVerifiedGroupsAgainstFullPool { get; set; } = true;
    public bool VerifiedGroupOrderbookPrefetchEnabled { get; set; } = true;
    public int MaxVerifiedGroupsPerCycle { get; set; } = 10;
    public int MaxVerifiedGroupLegs { get; set; } = 100;
    public bool LogVerifiedGroupMismatchDetails { get; set; } = true;
    public int VerifiedGroupMismatchSampleSize { get; set; } = 5;
    public bool AllowPartialVerifiedGroupEvaluation { get; set; } = true;
    public int MinResolvedMarketsForVerifiedGroup { get; set; } = 2;
    public bool RequireExactOutcomeCount { get; set; } = false;
    public bool VerifiedGroupUseOrderbookCache { get; set; } = true;
    public bool VerifiedGroupAllowHttpFetchOnCacheMiss { get; set; } = true;
    public int VerifiedGroupOrderbookMaxAgeMs { get; set; } = 5000;
    public int MaxVerifiedGroupOrderbookRequestsPerCycle { get; set; } = 200;
    public CostProfilesOptions CostProfiles { get; set; } = CostProfilesOptions.CreateDefault();
    public VerifiedGroupTriageOptions VerifiedGroupTriage { get; set; } = new();
}

public class VerifiedGroupTriageOptions
{
    public decimal HopelessGrossEdgeThreshold { get; set; } = -0.25m;
    public decimal NearExecutableCostReductionThreshold { get; set; } = 0.01m;
    public bool KeepRawPositiveGroups { get; set; } = true;
    public bool RecommendDisableHopelessGroups { get; set; } = true;
}

public sealed class CostProfilesOptions
{
    public string ActiveProfile { get; set; } = "Conservative";
    public Dictionary<string, CostProfileConfig> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static CostProfilesOptions CreateDefault()
    {
        return new CostProfilesOptions
        {
            ActiveProfile = "Conservative",
            Profiles = new Dictionary<string, CostProfileConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["Conservative"] = new("FixedPerLeg", 0.001m, 0.0005m, 0.001m),
                ["PolymarketApprox"] = new("NoneOrExternalized", 0m, 0.0005m, 0.001m),
                ["OrderbookOnly"] = new("None", 0m, 0m, 0.001m),
                ["RawOnly"] = new("None", 0m, 0m, 0m)
            }
        };
    }
}

public sealed record CostProfileConfig(string FeeModel, decimal FeePerLeg, decimal SlippageBufferPerLeg, decimal SafetyBufferPerGroup);

public class MultiOutcomeLoggingOptions
{
    public bool LogRepeatedArbDetected { get; set; } = false;
    public int LogDuplicatePositionEveryNCycles { get; set; } = 50;
    public bool LogExecutionStateChangesOnly { get; set; } = true;
    public bool LogVerifiedMismatchDetails { get; set; } = true;
    public int LogVerifiedMismatchEveryNCycles { get; set; } = 100;
    public bool LogVerifiedMismatchOnChangeOnly { get; set; } = true;
    public bool DebugVerifiedMismatch { get; set; } = false;
    public bool LogVerifiedBasketDetails { get; set; } = false;
    public bool LogMultiOutcomeSummary { get; set; } = true;
    public bool LogMultiOutcomeDetailsOnlyWhenExecutable { get; set; } = true;
    public bool LogRejectedMultiOutcomeCandidates { get; set; } = false;
    public int RejectedCandidateSampleSize { get; set; } = 5;
    public bool LogRejectedCandidateSummary { get; set; } = true;
    public bool LogRejectedMultiOutcomeSummary { get; set; } = true;
    public int RejectedMultiOutcomeSampleSize { get; set; } = 5;
    public bool LogBookCacheMissDetails { get; set; } = false;
    public int BookCacheMissSampleSize { get; set; } = 5;
    public int LogVerifiedBasketEveryNCycles { get; set; } = 10;
    public bool LogVerifiedBasketOnlyOnChange { get; set; } = true;
    public bool LogVerifiedBasketRanking { get; set; } = true;
    public int LogVerifiedBasketRankingEveryNCycles { get; set; } = 10;
    public bool LogVerifiedBasketOnlyOnChangeRanking { get; set; } = true;
    public bool LogScanConfigEachCycle { get; set; } = false;
    public bool LogVerifiedBasketDetailsOnChangeOnly { get; set; } = true;
    public int LogVerifiedBasketDetailsEveryNCycles { get; set; } = 50;
    public int LogProfileComparisonEveryNCycles { get; set; } = 10;
    public bool LogProfileComparisonSummary { get; set; } = true;
    public bool LogNearExecutableOnlyOnChange { get; set; } = true;
    public int LogCandidateScanEveryNCycles { get; set; } = 25;
    public bool LogCandidateScanOnChangeOnly { get; set; } = true;
    public bool LogCandidateScanWhenExecutableOnly { get; set; } = true;
    public bool LogCandidateScanWhenRejectDistributionChanges { get; set; } = true;
    public int LogVerifiedScanEveryNCycles { get; set; } = 25;
    public int LogAllowlistHealthEveryNCycles { get; set; } = 25;
    public bool LogVerifiedScanOnChangeOnly { get; set; } = true;
    public int LogPortfolioEveryNCycles { get; set; } = 25;
    public bool LogPortfolioOnChangeOnly { get; set; } = true;
    public bool LogAllowlistHealthOnChangeOnly { get; set; } = true;
    public int LogVerifiedGroupPricingEveryNCycles { get; set; } = 25;
    public bool LogPaperMtmOnChangeOnly { get; set; } = true;
    public int LogPaperMtmEveryNCycles { get; set; } = 25;
    public bool LogExecutionSuppressionSummary { get; set; } = true;
    public bool LogRepeatedSizingForOpenPosition { get; set; } = false;
}


public class MultiOutcomeReviewOptions
{
    public bool Enabled { get; set; } = true;
    public int TopCandidateGroupsForReview { get; set; } = 50;
    public int MaxMarketsPerCandidateGroup { get; set; } = 100;
    public int MinDetectedMarkets { get; set; } = 2;
    public string SortBy { get; set; } = "BestNetEdge";
    public bool ExportCandidates { get; set; } = true;
    public int ExportIntervalMinutes { get; set; } = 5;
    public string ExportPath { get; set; } = "exports/multi-outcome-candidates-latest.json";
    public string ExportReviewPath { get; set; } = "exports/multi-outcome-review-report-latest.json";
    public bool ExportVerifiedPricing { get; set; } = true;
    public string ExportVerifiedPricingPath { get; set; } = "exports/verified-group-pricing-latest.json";
    public bool IncludeSuggestedPrunedAllowlist { get; set; } = true;
    public bool AllowUnpricedLegsInTemplate { get; set; } = false;
    public string ExportVerifiedTriagePath { get; set; } = "exports/verified-group-triage-latest.json";
    public string ExportNextGroupsToVerifyPath { get; set; } = "exports/next-groups-to-verify-latest.json";
    public string ExportSuggestedVerifiedGroupsPath { get; set; } = "exports/verified-multi-outcome-groups-suggested.json";
}

public class RuntimeStateOptions
{
    public int MaxRejectedMultiOutcomeSamples { get; set; } = 100;
    public int MaxVerifiedBasketEdgeHistoryPerGroup { get; set; } = 200;
    public int MaxCandidateGroupsForReview { get; set; } = 50;
    public int MaxMultiOutcomeDiagnosticsHistory { get; set; } = 100;
    public int MaxRecentLogs { get; set; } = 500;
    public int MaxScannerStatsHistory { get; set; } = 500;
    public int MaxProfileComparisonHistory { get; set; } = 200;
    public int MaxRejectedCandidateSamples { get; set; } = 100;
    public int MaxExecutionAuditEvents { get; set; } = 500;
    public int MaxSignalREventBuffer { get; set; } = 100;
    public int OrderbookCacheTtlSeconds { get; set; } = 30;
    public int MarketCacheTtlMinutes { get; set; } = 30;
}

public class SignalROptions
{
    public int MaxPayloadItems { get; set; } = 100;
    public int MaxRecentLogsToBroadcast { get; set; } = 100;
    public int MaxDiagnosticsItemsToBroadcast { get; set; } = 50;
}

public class RuntimeHealthOptions
{
    public bool Enabled { get; set; } = true;
    public int LogEveryMinutes { get; set; } = 5;
}
