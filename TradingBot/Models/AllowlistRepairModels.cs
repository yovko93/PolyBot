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

public sealed record AllowlistRepairReport(
    DateTime Timestamp,
    int ConfiguredGroups,
    int Healthy,
    int Broken,
    int NeedsRefresh,
    int NeedsPricingPrune,
    IReadOnlyList<AllowlistRepairGroup> Groups);

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
    JsonNode? SuggestedPrunedTemplate,
    JsonNode? SuggestedRefreshedTemplate,
    IReadOnlyList<string> Notes,
    string CopyInstructions);

public sealed record AllowlistRepairSuggestedConfig(
    DateTime Timestamp,
    string Note,
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
