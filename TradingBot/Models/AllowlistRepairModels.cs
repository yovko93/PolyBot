using System.Text.Json.Nodes;

namespace TradingBot.Models;

public enum AllowlistRepairHealthCategory
{
    Healthy,
    MonitoringOnly,
    PricingUnavailable,
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
    int ChangedConditionIds,
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

public sealed record AllowlistRepairSnapshot(
    string SnapshotId,
    DateTime CreatedAt,
    string DiscoveryId,
    string CandidateExportPath,
    int CandidateGroupsCount,
    int VerifiedGroupsCount,
    string Source,
    IReadOnlyList<AllowlistRepairGroup> RepairResults);

public sealed record AllowlistRepairCategoryCounts(
    int Healthy,
    int MonitoringOnly,
    int NeedsPricingPrune,
    int NeedsRefresh,
    int BrokenConfig,
    int Disabled,
    int Ignored,
    int BrokenTotal,
    int HasMissingNoAsk,
    int HasMarketMismatch,
    int HasCandidateRefreshMatch,
    int HasSuggestedTemplate);

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
    string SnapshotId,
    DateTime Timestamp,
    AllowlistRepairSummary Summary,
    AllowlistRepairCategoryCounts CategoryCounts,
    bool InvariantResult,
    AllowlistRepairSnapshot Snapshot,
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
    string RepairSnapshotId,
    int ActionVersion,
    string? PreviousAction,
    string CurrentAction,
    DateTime ActionChangedAt,
    string ReasonForChange,
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


public sealed record AllowlistRefreshDiagnosticsExport(
    DateTime Timestamp,
    string SnapshotId,
    bool AutoApplyAllowed,
    IReadOnlyList<AllowlistRefreshDiagnosticsItem> Items);

public sealed record AllowlistRefreshDiagnosticsItem(
    string GroupKey,
    IReadOnlyList<string> CurrentConfiguredMarketIds,
    IReadOnlyList<string> CurrentConfiguredTokenIds,
    int ConfiguredLegCount,
    string ResolverStatus,
    int DiscoveredCandidateMatches,
    string BestCandidateGroupKey,
    decimal BestCandidateScore,
    IReadOnlyList<string> MatchedMarketIds,
    IReadOnlyList<string> MissingMarketIds,
    IReadOnlyList<string> AddedMarketIds,
    IReadOnlyList<string> RemovedMarketIds,
    decimal OverlapRatio,
    decimal TitleSimilarity,
    bool CategoryKindMatch,
    string Reason,
    string RecommendedAction,
    string Confidence,
    bool AutoApplyAllowed);

public sealed record AllowlistRepairSuggestedConfig(
    string SnapshotId,
    DateTime GeneratedAt,
    DateTime Timestamp,
    string Note,
    AllowlistRepairSummary Summary,
    AllowlistRepairCategoryCounts CategoryCounts,
    IReadOnlyList<AllowlistRepairSuggestedGroup> Groups);

public sealed record AllowlistRepairSuggestedGroup(
    string GroupKey,
    string Title,
    bool CurrentEnabled,
    string HealthCategory,
    string RecommendedAction,
    string RepairConfidence,
    string SuggestedAction,
    bool SuggestedEnabled,
    JsonNode? SuggestedTemplate,
    JsonNode? SuggestedPrunedTemplate,
    JsonNode? SuggestedRefreshedTemplate,
    JsonNode? Diff,
    IReadOnlyList<string> Notes,
    string CopyInstructions);


public sealed record AllowlistRepairPatchPreview(
    DateTime Timestamp,
    string SnapshotId,
    string SourceConfigPath,
    string Mode,
    bool WillOverwriteRealConfig,
    AllowlistRepairPatchSummary Summary,
    IReadOnlyList<AllowlistRepairPatchItem> Patches,
    AllowlistRepairPostApplyValidationPlan PostApplyValidationPlan,
    AllowlistRepairManualApplyInstructions ManualApplyInstructions,
    AllowlistPatchedPreviewValidation PatchedPreviewValidation);

public sealed record AllowlistRepairPatchSummary(
    int TotalConfigured,
    int PatchableHighConfidence,
    int PatchableMediumConfidence,
    int LowConfidenceReviewOnly,
    int DisabledSuggested,
    int ExpectedHealthyAfterApply,
    int Quarantined,
    int NoOp,
    int Locked);

public sealed record AllowlistRepairPatchItem(
    string GroupKey,
    string CurrentAction,
    string Confidence,
    string PatchType,
    JsonNode? CurrentGroup,
    JsonNode? ProposedGroup,
    JsonNode? Diff,
    IReadOnlyList<string> RiskNotes,
    string ManualInstructions,
    string ExpectedResultAfterApply);

public sealed record AllowlistRepairManualApplyInstructions(
    string SourcePreviewFile,
    string TargetRealConfigPath,
    IReadOnlyList<string> BackupInstructions,
    IReadOnlyList<string> GroupsToApply,
    IReadOnlyList<string> GroupsNotToApply,
    IReadOnlyList<string> ExpectedLogsAfterRestart);

public sealed record AllowlistPatchedPreviewValidation(
    int TotalGroups,
    int UniqueGroupKeys,
    int DuplicateGroupKeys,
    bool Valid,
    IReadOnlyList<string> Reasons);

public sealed record AllowlistRepairPostApplyValidationPlan(
    IReadOnlyList<string> Steps,
    int ExpectedConfiguredCount,
    int ExpectedResolvedCount,
    int ExpectedMissingNoAskCount,
    IReadOnlyList<string> GroupsExpectedToBecomeHealthyOrResolved,
    IReadOnlyList<string> GroupsStillExpectedReviewOnly,
    IReadOnlyList<string> CommandsOrEndpointsToCheck,
    IReadOnlyList<string> ExpectedOutcomes);

public sealed record AllowlistRepairPatchExport(
    AllowlistRepairPatchPreview PatchPreview,
    JsonNode PatchedPreviewConfig,
    JsonNode? PatchedPreviewWithMetadata);

public sealed record AllowlistPatchValidationResult(string GroupKey, IReadOnlyList<string> RemovedMarketIds, bool Valid, int FinalLegs);

public sealed record AllowlistRepairHistoryEntry(
    string GroupKey,
    string SnapshotId,
    string Action,
    IReadOnlyList<string> AddedMarketIds,
    IReadOnlyList<string> RemovedMarketIds,
    string DiffHash,
    string InverseDiffHash,
    DateTime Timestamp);

public sealed record AllowlistRepairHistoryDiff(IReadOnlyList<string> AddedMarketIds, IReadOnlyList<string> RemovedMarketIds);

public sealed record AllowlistRepairHistoryGroup(
    string GroupKey,
    IReadOnlyList<AllowlistRepairHistoryEntry> Snapshots,
    string LastDiffHash,
    string InverseDiffHash,
    IReadOnlyList<string> LastAddedMarketIds,
    IReadOnlyList<string> LastRemovedMarketIds,
    IReadOnlyList<string> PreviousAddedMarketIds,
    IReadOnlyList<string> PreviousRemovedMarketIds,
    bool OscillationDetected,
    IReadOnlyList<string> OscillatingMarketIds,
    bool Locked,
    string QuarantineReason,
    DateTime LastUpdatedAt,
    bool Patchable,
    AllowlistRepairHistoryDiff CurrentDiff,
    AllowlistRepairHistoryDiff PreviousDiff,
    string RecommendedAction,
    string RepairConfidence,
    string Reason)
{
    public IReadOnlyList<AllowlistRepairHistoryEntry> LastSnapshots => Snapshots;
    public IReadOnlyList<string> AddedMarketIds => LastAddedMarketIds;
    public IReadOnlyList<string> RemovedMarketIds => LastRemovedMarketIds;
    public string DiffHash => LastDiffHash;
}

public sealed record AllowlistRepairHistoryExport(DateTime Timestamp, IReadOnlyList<AllowlistRepairHistoryGroup> Groups);
