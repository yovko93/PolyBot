using System.Text.Json.Nodes;

namespace TradingBot.Models;

public enum AllowlistRepairHealthCategory
{
    Healthy,
    MonitoringOnly,
    NeedsPricingPrune,
    NeedsRefresh,
    BrokenConfig,
    Disabled,
    Ignored
}

public enum AllowlistRepairRecommendedAction
{
    Keep,
    KeepMonitoring,
    PruneMissingNoAskLegs,
    RefreshFromCandidateExport,
    DisableMissingMarkets,
    DisableUntilBetterPricing,
    RemoveFromAllowlist,
    NeedsManualReview
}

public sealed record AllowlistRepairMatch(
    string CandidateGroupKey,
    decimal Score,
    decimal TitleSimilarity,
    decimal MarketOverlap,
    decimal ConditionOverlap,
    decimal OutcomeSemanticScore,
    int PricedLegs,
    int MissingNoAsk,
    IReadOnlyList<string> AddedMarketIds,
    IReadOnlyList<string> RemovedMarketIds,
    string Confidence);

public sealed record AllowlistRepairClassification(
    string GroupKey,
    string HealthCategory,
    string RecommendedAction,
    string RepairConfidence,
    string Reason,
    IReadOnlyList<string> MissingMarketIds,
    IReadOnlyList<string> MissingNoAskMarketIds,
    JsonNode? SuggestedPrunedTemplate,
    JsonNode? SuggestedRefreshedTemplate,
    AllowlistRepairMatch? RepairMatch,
    int ConsecutiveMatchMisses);

public sealed record AllowlistRepairSummary(
    int ConfiguredGroups,
    int Healthy,
    int MonitoringOnly,
    int NeedsPricingPrune,
    int NeedsRefresh,
    int BrokenConfig,
    int Disabled,
    int Ignored,
    int Broken,
    bool InvariantOk);

public sealed record AllowlistRepairSuggestion(
    string GroupKey,
    string Action,
    string Confidence,
    JsonNode? SuggestedJson,
    string ExpectedResultAfterManualApply,
    string CopyInstructions);

public sealed record AllowlistRepairReport(
    DateTime Timestamp,
    AllowlistRepairSummary Summary,
    int ConfiguredGroups,
    int Healthy,
    int MonitoringOnly,
    int Broken,
    int NeedsRefresh,
    int NeedsPricingPrune,
    int BrokenConfig,
    int Disabled,
    int Ignored,
    IReadOnlyList<AllowlistRepairGroup> Groups,
    IReadOnlyList<AllowlistRepairSuggestion> RepairSuggestions,
    string CopyInstructions);

public sealed record AllowlistRepairGroup(
    string GroupKey,
    string Title,
    bool Enabled,
    string Status,
    string HealthCategory,
    bool Resolved,
    bool Evaluated,
    int ConfiguredMarketCount,
    int ResolvedMarketCount,
    int MissingMarketCount,
    IReadOnlyList<string> MissingMarketIds,
    int NoAskResolved,
    int MissingNoAsk,
    IReadOnlyList<string> MissingNoAskMarketIds,
    string? MismatchReason,
    string? PricingReason,
    string RecommendedAction,
    string RepairConfidence,
    string Reason,
    JsonNode? SuggestedPrunedTemplate,
    JsonNode? SuggestedRefreshedTemplate,
    AllowlistRepairMatch? RepairMatch,
    int ConsecutiveMatchMisses,
    IReadOnlyList<string> Notes,
    string ExpectedResultAfterManualApply,
    string CopyInstructions);

public sealed record AllowlistRepairSuggestedConfig(
    DateTime Timestamp,
    string Note,
    AllowlistRepairSummary Summary,
    IReadOnlyList<AllowlistRepairSuggestedGroup> Groups);

public sealed record AllowlistRepairSuggestedGroup(
    string GroupKey,
    string Title,
    bool CurrentEnabled,
    string SuggestedAction,
    bool SuggestedEnabled,
    JsonNode? SuggestedTemplate,
    JsonNode? Diff,
    IReadOnlyList<string> Notes);
