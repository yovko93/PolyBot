namespace TradingBot.Services;

public sealed record VerifiedPruneDryRunLeg(string MarketId, string? ConditionId, string? NoTokenId, decimal? NoAsk, decimal? NoAskQuantity, string? Source, string? FailureReason);

public sealed record VerifiedPruneDryRunResult(
    string Group,
    IReadOnlyList<string> MissingMarketIds,
    int OriginalLegs,
    int PrunedLegs,
    decimal OriginalConservativeNet,
    decimal PrunedConservativeNet,
    decimal OriginalRawNet,
    decimal PrunedRawNet,
    bool WouldBecomeActivePositive,
    bool WouldOpenIfPaperEligible,
    string Action,
    bool AutoApply,
    IReadOnlyList<VerifiedPruneDryRunLeg> CurrentLegList,
    IReadOnlyList<VerifiedPruneDryRunLeg> SuggestedPrunedLegList,
    IReadOnlyList<VerifiedPruneDryRunLeg> RemovedLegMetadata)
{
    public string ToLogLine()
        => $"[VERIFIED_PRUNE_DRY_RUN] Group={Group} MissingMarketIds=[{string.Join(",", MissingMarketIds)}] OriginalLegs={OriginalLegs} PrunedLegs={PrunedLegs} OriginalConservativeNet={OriginalConservativeNet:0.####} PrunedConservativeNet={PrunedConservativeNet:0.####} OriginalRawNet={OriginalRawNet:0.####} PrunedRawNet={PrunedRawNet:0.####} WouldBecomeActivePositive={WouldBecomeActivePositive.ToString().ToLowerInvariant()} WouldOpenIfPaperEligible={WouldOpenIfPaperEligible.ToString().ToLowerInvariant()} Action={Action} AutoApply={AutoApply.ToString().ToLowerInvariant()}";
}
