using System.Diagnostics;

namespace TradingBot.Api;

public sealed record RuntimeHealthSnapshot(
    string ProcessRunId,
    DateTime StartedAtUtc,
    string ScannerInstanceId,
    long DiagnosticsCounterMismatchCount,
    string DiagnosticsCounterMismatchLastReason,
    double ProcessMemoryMb,
    double GcTotalMemoryMb,
    double PrivateMemoryMb,
    double WorkingSetMb,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    int ThreadCount,
    int HandleCount,
    TimeSpan Uptime,
    long ScannerCycleId,
    long LastScanDurationMs,
    int RecentLogsCount,
    int ScannerHistoryCount,
    int CandidateSnapshotCount,
    int RepairHistoryCount,
    int UnresolvedDiagnosticsCount,
    int DryRunOrderPlansCount,
    int FillSimulationsCount,
    int ExecutionAuditCount,
    int SignalREventBufferCount,
    int OrderbookCacheCount,
    int MarketCacheCount,
    int ExportQueueCount,
    int PatchPreviewItemsCount,
    int SingleMarketOpportunitiesCount,
    int SingleMarketNearMissesCount,
    int SingleMarketDataQualitySamplesCount,
    int SingleMarketExecutionsCount,
    int PaperPhase,
    int PaperOpenPositions,
    int PaperClosedPositions,
    int PaperSettlements,
    decimal PaperRealizedPnl,
    decimal PaperLocked,
    decimal PaperCash,
    decimal PaperEquity,
    decimal PaperTotalExposure,
    int PaperOpenCountLastHour,
    int PaperPretradeRejects,
    int PaperDuplicateSuppressions,
    IReadOnlyList<string> PaperOpenPositionKeys,
    int PaperOpenMarketIdsCount,
    IReadOnlyList<string> PaperOpenMarketIdsSample,
    int PaperInFlightOpens,
    int PaperDuplicateDedupeEntries,
    int PaperStaleDedupeEntriesCleared,
    int PaperSettlementRejects,
    int PaperDuplicateSettlementSuppressions,
    int PaperLifecycleEvents,
    int PaperOpenEvents,
    int PaperCloseEvents,
    long LiveTradingBlockedCount,
    int PaperExecutionsCount,
    long SigningAttempts,
    int DuplicateGroupKeyWarnings,
    long QuietSuppressedTotal,
    long EmittedLogs,
    int LogGateCacheSize,
    long QuietCappedSuppressions,
    IReadOnlyDictionary<string, long> QuietSuppressedByCategory,
    IReadOnlyDictionary<string, long> EmittedByCategory,
    long BatchBookRequests,
    long BatchBookBadRequests,
    long BatchBookTimeouts,
    long BatchBookRetrySuccesses,
    long BatchBookInvalidTokens,
    long BatchBookSuppressedErrors,
    long BatchBookSplitRetriesAttempted,
    long BatchBookSplitRetrySucceeded,
    long BatchBookSplitRetryFailed,
    long BatchBookSingleTokenFailures,
    long BatchBookSingleTokenQuarantined,
    long BatchBookSkippedQuarantinedTokens,
    long BatchBookSkippedMarketsWithQuarantinedTokens,
    long BatchBookRepeatedInvalidTokenAfterQuarantine,
    int OrderbookUnavailableMarkets,
    int InvalidTokenQuarantineActive,
    long InvalidTokenQuarantineAdded,
    long InvalidTokenQuarantineExpired,
    long BatchBookRequestsAvoidedByQuarantine,
    long MarketsSkippedByInvalidTokenQuarantine,
    int MarketOrderbookQuarantineActive,
    long MarketOrderbookQuarantineAdded,
    long MarketOrderbookQuarantineExpired,
    long MarketsSkippedByMarketOrderbookQuarantine,
    long BatchBookRequestsAvoidedByMarketQuarantine,
    bool OrderbookCircuitBreakerActive,
    string OrderbookCircuitBreakerState,
    long OrderbookCircuitBreakerOpenCount,
    long OrderbookCircuitBreakerCooldownRemainingSeconds,
    long OrderbookCircuitBreakerHalfOpenAttempts,
    long OrderbookCircuitBreakerHalfOpenSucceeded,
    long OrderbookCircuitBreakerHalfOpenFailed,
    DateTime? OrderbookCircuitBreakerHalfOpenStartedUtc,
    long OrderbookCircuitBreakerHalfOpenAgeSeconds,
    long OrderbookCircuitBreakerHalfOpenMaxSeconds,
    long OrderbookCircuitBreakerHalfOpenTimedOutCount,
    long BatchBookCanaryTimeouts,
    long BatchBookCanaryInvalidTokens,
    long BatchBookCanaryOrderbookUnavailable,
    string OrderbookCircuitBreakerLastHalfOpenFailureReason,
    long OrderbookCircuitBreakerCooldownExtensions,
    string OrderbookCircuitBreakerLastOpenReason,
    DateTime? OrderbookCircuitBreakerRecoveringSinceUtc,
    long OrderbookCircuitBreakerRecoveryRemainingSeconds,
    long OrderbookCircuitBreakerReopenedAfterClose,
    long OrderbookRequestsBlockedByCircuitBreaker,
    long OrderbookPostCloseBadRequests,
    long OrderbookPostCloseInvalidTokens,
    long SingleTokenIsolationBudgetExhausted,
    long BatchBookBadRequestsPreventedEstimate,
    long BatchBookCanaryRequests,
    long BatchBookCanaryBadRequests,
    long BatchBookRecoveryRequests,
    long BatchBookRecoveryBadRequests,
    long OrderbookRecoveryLimitedRequests,
    long OrderbookRecoveryLimitedMarkets,
    long OrderbookRecoveryBadRequests,
    long OrderbookRecoveryInvalidTokens,
    long OrderbookRecoverySucceededCount,
    long OrderbookRecoveryFailedCount,
    long BatchBookNormalRequests,
    long BatchBookNormalBadRequests,
    long BatchBookNormalRequestsBeforeBreakerOpen,
    long BatchBookNormalBadRequestsBeforeBreakerOpen,
    long BatchBookNormalRequestsAfterBreakerOpen,
    long BatchBookNormalBadRequestsAfterBreakerOpen,
    long QuarantinedMarketsReintroducedBlocked,
    long QuarantinedTokensReintroducedBlocked,
    int AllowlistHealthy,
    int AllowlistMonitoringOnly,
    int AllowlistNeedsPricingPrune,
    int AllowlistNeedsRefresh,
    int AllowlistReviewOnly,
    int AllowlistMismatch,
    int AllowlistBrokenConfig,
    int AllowlistDisabled,
    int AllowlistIgnored,
    int AllowlistClassificationTotal,
    bool AllowlistClassificationValid,
    int AllowlistRefreshPreviewCandidates,
    int AllowlistRefreshHighConfidence,
    int AllowlistRefreshFinalNoCandidate,
    int AllowlistRefreshFinalSemanticConflict,
    int AllowlistRefreshFinalLowConfidence,
    int AllowlistRefreshFinalUnstable,
    int AllowlistRefreshFinalPreviewOnly,
    int AllowlistRefreshFinalLockedManualReview,
    int AllowlistRefreshActionExplainedSuppressed,
    int AllowlistRefreshUnstableGroups,
    int AllowlistRefreshActionFlipFlops,
    bool AllowlistRefreshAutoApply,
    bool DiscoveryHealthy,
    bool DiscoveryUsingLastHealthySnapshot,
    int DiscoveryLastHealthySnapshotAgeSeconds,
    int DiscoveryPartialAttemptCount,
    string DiscoveryLastFailureReason,
    bool ScannerPausedByDiscoveryGuard,
    int DiscoveryGuardSkippedCycles,
    bool DiscoveryGuardUsingLastHealthySnapshot,
    int DiscoveryGuardBlockedNewMarkets,
    bool LongRunStable,
    string LongRunBlockingReason,
    bool DiscoveryStable,
    bool OrderbookRecoveredAfterDegradation,
    DateTime? LastDegradationUtc,
    DateTime? LastRecoveryUtc,
    bool DiscoveryBootstrapHealthy,
    int DiscoveryBootstrapRetryCount,
    DateTime? DiscoveryBootstrapLastAttemptUtc,
    DateTime? DiscoveryBootstrapNextRetryUtc,
    int DiscoveryBootstrapBackoffSeconds,
    string DiscoveryBootstrapFailureReason,
    int DiscoveryRetryBackoffSeconds,
    int DiscoveryRetriesSuppressedByBackoff,
    bool DiscoveryPersistedSnapshotLoaded,
    int DiscoveryPersistedSnapshotAgeSeconds,
    int DiscoveryPersistedSnapshotActiveMarkets,
    bool AllowlistEvaluationSkipped,
    string AllowlistEvaluationSkippedReason,
    bool AllowlistClassificationBlockedByDiscovery,
    string DiscoveryBlockedReason,
    string DiscoverySelectedSource,
    bool DiscoveryScannerSafeSourceAvailable,
    bool DiscoverySourceAuditOnly,
    bool DiscoverySourceAuditExportWritten,
    string DiscoverySourceAuditExportPath,
    int DiscoverySourceAuditSources,
    int DiscoverySourceAuditScannerSafeSources,
    string DiscoverySourceAuditRecommendedAction,
    string SoakReadiness,
    string SoakReadinessReason,
    bool DiscoveryReducedUniverse,
    int ReducedUniverseMarkets,
    int ReducedUniverseRawMarkets,
    int ReducedUniverseFilteredMarkets,
    int ReducedUniverseExcludedInvalidTokens,
    int ReducedUniverseExcludedQuarantinedMarkets,
    int ReducedUniverseExcludedBadHistory,
    int ReducedUniverseOrderbookEligibleMarkets,
    bool ReducedUniverseOrderbookStable,
    bool ReducedUniverseScanPausedByOrderbookHealth,
    bool ReducedUniverseOrderbookRecoveryMode,
    long ReducedUniverseOrderbookRecoveryCleanWindowSeconds,
    long ReducedUniversePostRecoveryBadRequests,
    bool MarketOrderbookQuarantineLifecycleBalanced,
    bool InvalidTokenQuarantineLifecycleBalanced,
    string OrderbookQuarantineLifecycleMismatchReason,
    long InFlightBeforeBreakerCompletedAfterOpen,
    long InFlightBeforeBreakerBadRequestsAfterOpen,
    long TruePostBreakerNormalRequests,
    long TruePostBreakerBadRequests,
    int ReducedUniverseBadHistoryActive,
    bool ReducedUniverseBadHistoryLoaded,
    int ReducedUniverseBadHistoryExpired,
    long SingleMarketScanPausedByOrderbookHealth,
    long SingleMarketPausedCycles,
    long SingleMarketNormalCycles,
    long SingleMarketFullCyclesCompleted,
    bool PaperDiagnosticsLimitedEligible,
    string PaperDiagnosticsLimitedBlockedReason,
    bool PaperDiagnosticsLimitedEnabled,
    string PaperDiagnosticsLimitedAllowedStrategy,
    int PaperDiagnosticsLimitedMaxOpenPositions,
    decimal PaperDiagnosticsLimitedMaxPaperNotionalPerTrade,
    decimal PaperDiagnosticsLimitedMaxPaperTotalExposure,
    int PaperDiagnosticsLimitedOpensLastHour,
    string PaperDiagnosticsLimitedGateLastRejectReason,
    int PaperDiagnosticsLimitedPaperOpened,
    bool OrderbookStableNow,
    int OrderbookStableWindowMinutes,
    long BatchBookBadRequestsDeltaWindow,
    long BatchBookInvalidTokensDeltaWindow,
    long PostBreakerBadRequestsDeltaWindow,
    bool ReducedUniverseOrderbookStableNow,
    int ReducedUniverseMaxMarkets,
    string ReducedUniverseSource,
    bool PaperExecutionGloballyBlockedByDiscovery,
    int PaperBlockedByDiscoveryMode,
    bool StrategyExecutionGloballyBlocked,
    string DiagnosticsUniverse,
    bool TradingReadiness,
    bool AllowReducedUniverseDiagnosticsOnly,
    bool ReducedUniverseRequireExplicitFlag,
    bool ReducedUniverseExplicitFlagSatisfied,
    string ReducedUniverseActivationBlockedReason,
    int RuntimeStatusExportWriteFailures,
    int RuntimeStatusExportReadFailures,
    string RuntimeStatusExportLastFailureReason,
    int RuntimeStatusExportRecoveredCount,
    bool RuntimeStatusExportStable,
    int SingleMarketRawCandidates,
    int SingleMarketDataQualityRejected,
    int SingleMarketDataQualityRejectedRawPositive,
    int SingleMarketBelowMinEdge,
    int SingleMarketPositiveBeforeCost,
    int SingleMarketPositiveAfterCost,
    int SingleMarketPositiveAfterSafety,
    int SingleMarketValidRawPositive,
    int SingleMarketValidAfterCostPositive,
    int SingleMarketValidAfterSafetyPositive,
    int SingleMarketEdgeStable,
    int SingleMarketExecutionReady,
    int SingleMarketRejectedByFill,
    int SingleMarketRejectedByDepth,
    int SingleMarketRejectedByRisk,
    int SingleMarketRejectedByPaperDiagnosticsLimitedGate,
    decimal? SingleMarketBestRawEdge,
    decimal? SingleMarketBestAfterCostEdge,
    decimal? SingleMarketBestAfterSafetyEdge,
    decimal? SingleMarketBestExecutableEdge,
    string SingleMarketBestRejectedReason,
    int SingleMarketValidEdgeSamples,
    string SingleMarketEdgeDistributionSampleMode,
    int SingleMarketEdgeDistributionCapacity,
    long SingleMarketEdgeDistributionDroppedSamples,
    decimal? SingleMarketRawEdgeMin,
    decimal? SingleMarketRawEdgeP01,
    decimal? SingleMarketRawEdgeP05,
    decimal? SingleMarketRawEdgeP10,
    decimal? SingleMarketRawEdgeP25,
    decimal? SingleMarketRawEdgeP50,
    decimal? SingleMarketRawEdgeP75,
    decimal? SingleMarketRawEdgeP90,
    decimal? SingleMarketRawEdgeP95,
    decimal? SingleMarketRawEdgeP99,
    decimal? SingleMarketRawEdgeMax,
    decimal? SingleMarketAfterCostEdgeMin,
    decimal? SingleMarketAfterCostEdgeP01,
    decimal? SingleMarketAfterCostEdgeP05,
    decimal? SingleMarketAfterCostEdgeP10,
    decimal? SingleMarketAfterCostEdgeP25,
    decimal? SingleMarketAfterCostEdgeP50,
    decimal? SingleMarketAfterCostEdgeP75,
    decimal? SingleMarketAfterCostEdgeP90,
    decimal? SingleMarketAfterCostEdgeP95,
    decimal? SingleMarketAfterCostEdgeP99,
    decimal? SingleMarketAfterCostEdgeMax,
    decimal? SingleMarketAfterSafetyEdgeMin,
    decimal? SingleMarketAfterSafetyEdgeP01,
    decimal? SingleMarketAfterSafetyEdgeP05,
    decimal? SingleMarketAfterSafetyEdgeP10,
    decimal? SingleMarketAfterSafetyEdgeP25,
    decimal? SingleMarketAfterSafetyEdgeP50,
    decimal? SingleMarketAfterSafetyEdgeP75,
    decimal? SingleMarketAfterSafetyEdgeP90,
    decimal? SingleMarketAfterSafetyEdgeP95,
    decimal? SingleMarketAfterSafetyEdgeP99,
    decimal? SingleMarketAfterSafetyEdgeMax,
    int SingleMarketAfterSafetyEdgeBelowMinus5bp,
    int SingleMarketAfterSafetyEdgeMinus5bpToMinus2bp,
    int SingleMarketAfterSafetyEdgeMinus2bpToMinus1bp,
    int SingleMarketAfterSafetyEdgeMinus1bpTo0,
    int SingleMarketAfterSafetyEdge0To1bp,
    int SingleMarketAfterSafetyEdge1bpTo5bp,
    int SingleMarketAfterSafetyEdgeAbove5bp,
    IReadOnlyDictionary<string, TradingBot.Services.StrategyRuntimeCounterSnapshot> StrategyCounters)
{
    public string ToLogLine()
    {
        var suppressed = string.Join(",", QuietSuppressedByCategory.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value}"));
        var emitted = string.Join(",", EmittedByCategory.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value}"));
        var strategies = string.Join(",", StrategyCounters.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => FormatStrategyRuntimeHealth(x.Key, x.Value)));
        var strategyScanCounts = string.Join(",", StrategyCounters.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value.Scanned}"));
        var strategyCandidates = string.Join(",", StrategyCounters.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value.Candidates}"));
        var strategyPositiveEdges = string.Join(",", StrategyCounters.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value.PositiveEdges}"));
        var strategyExecutionReady = string.Join(",", StrategyCounters.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value.ExecutionReady}"));
        var strategyPaperOpened = string.Join(",", StrategyCounters.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value.PaperOpened}"));
        var strategyRejectedByReason = string.Join(",", StrategyCounters.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:[{FormatRejectedReasons(x.Value.RejectedByReason)}]"));
        return $"[RUNTIME_HEALTH] ProcessRunId={ProcessRunId} StartedAtUtc={StartedAtUtc:O} ScannerInstanceId={ScannerInstanceId} DiscoveryHealthy={DiscoveryHealthy.ToString().ToLowerInvariant()} DiscoveryReducedUniverse={DiscoveryReducedUniverse.ToString().ToLowerInvariant()} DiscoveryMode={DiscoverySelectedSource} DiscoveryScannerSafeSourceAvailable={DiscoveryScannerSafeSourceAvailable.ToString().ToLowerInvariant()} ReducedUniverseMarkets={ReducedUniverseMarkets} ReducedUniverseRawMarkets={ReducedUniverseRawMarkets} ReducedUniverseFilteredMarkets={ReducedUniverseFilteredMarkets} ReducedUniverseExcludedInvalidTokens={ReducedUniverseExcludedInvalidTokens} ReducedUniverseExcludedQuarantinedMarkets={ReducedUniverseExcludedQuarantinedMarkets} ReducedUniverseExcludedBadHistory={ReducedUniverseExcludedBadHistory} ReducedUniverseOrderbookEligibleMarkets={ReducedUniverseOrderbookEligibleMarkets} ReducedUniverseOrderbookStable={ReducedUniverseOrderbookStable.ToString().ToLowerInvariant()} ReducedUniverseScanPausedByOrderbookHealth={ReducedUniverseScanPausedByOrderbookHealth.ToString().ToLowerInvariant()} ReducedUniverseOrderbookRecoveryMode={ReducedUniverseOrderbookRecoveryMode.ToString().ToLowerInvariant()} ReducedUniverseOrderbookRecoveryCleanWindowSeconds={ReducedUniverseOrderbookRecoveryCleanWindowSeconds} ReducedUniversePostRecoveryBadRequests={ReducedUniversePostRecoveryBadRequests} MarketOrderbookQuarantineLifecycleBalanced={MarketOrderbookQuarantineLifecycleBalanced.ToString().ToLowerInvariant()} InvalidTokenQuarantineLifecycleBalanced={InvalidTokenQuarantineLifecycleBalanced.ToString().ToLowerInvariant()} OrderbookQuarantineLifecycleMismatchReason={OrderbookQuarantineLifecycleMismatchReason} InFlightBeforeBreakerCompletedAfterOpen={InFlightBeforeBreakerCompletedAfterOpen} InFlightBeforeBreakerBadRequestsAfterOpen={InFlightBeforeBreakerBadRequestsAfterOpen} TruePostBreakerNormalRequests={TruePostBreakerNormalRequests} TruePostBreakerBadRequests={TruePostBreakerBadRequests} ReducedUniverseBadHistoryActive={ReducedUniverseBadHistoryActive} ReducedUniverseBadHistoryLoaded={ReducedUniverseBadHistoryLoaded.ToString().ToLowerInvariant()} ReducedUniverseBadHistoryExpired={ReducedUniverseBadHistoryExpired} SingleMarketScanPausedByOrderbookHealth={SingleMarketScanPausedByOrderbookHealth} SingleMarketPausedCycles={SingleMarketPausedCycles} SingleMarketNormalCycles={SingleMarketNormalCycles} SingleMarketFullCyclesCompleted={SingleMarketFullCyclesCompleted} PaperDiagnosticsLimitedEnabled={PaperDiagnosticsLimitedEnabled.ToString().ToLowerInvariant()} PaperDiagnosticsLimitedEligible={PaperDiagnosticsLimitedEligible.ToString().ToLowerInvariant()} PaperDiagnosticsLimitedBlockedReason={PaperDiagnosticsLimitedBlockedReason} PaperDiagnosticsLimitedAllowedStrategy={PaperDiagnosticsLimitedAllowedStrategy} PaperDiagnosticsLimitedMaxOpenPositions={PaperDiagnosticsLimitedMaxOpenPositions} PaperDiagnosticsLimitedMaxPaperNotionalPerTrade={PaperDiagnosticsLimitedMaxPaperNotionalPerTrade:0.####} PaperDiagnosticsLimitedMaxPaperTotalExposure={PaperDiagnosticsLimitedMaxPaperTotalExposure:0.####} PaperDiagnosticsLimitedOpensLastHour={PaperDiagnosticsLimitedOpensLastHour} PaperDiagnosticsLimitedGateLastRejectReason={PaperDiagnosticsLimitedGateLastRejectReason} PaperDiagnosticsLimitedPaperOpened={PaperDiagnosticsLimitedPaperOpened} OrderbookStableNow={OrderbookStableNow.ToString().ToLowerInvariant()} OrderbookStableWindowMinutes={OrderbookStableWindowMinutes} BatchBookBadRequestsDeltaWindow={BatchBookBadRequestsDeltaWindow} BatchBookInvalidTokensDeltaWindow={BatchBookInvalidTokensDeltaWindow} PostBreakerBadRequestsDeltaWindow={PostBreakerBadRequestsDeltaWindow} ReducedUniverseOrderbookStableNow={ReducedUniverseOrderbookStableNow.ToString().ToLowerInvariant()} ReducedUniverseMaxMarkets={ReducedUniverseMaxMarkets} ReducedUniverseSource={ReducedUniverseSource} PaperExecutionGloballyBlockedByDiscovery={PaperExecutionGloballyBlockedByDiscovery.ToString().ToLowerInvariant()} PaperBlockedByDiscoveryMode={PaperBlockedByDiscoveryMode} StrategyExecutionGloballyBlocked={StrategyExecutionGloballyBlocked.ToString().ToLowerInvariant()} DiagnosticsUniverse={DiagnosticsUniverse} AllowReducedUniverseDiagnosticsOnly={AllowReducedUniverseDiagnosticsOnly.ToString().ToLowerInvariant()} ReducedUniverseRequireExplicitFlag={ReducedUniverseRequireExplicitFlag.ToString().ToLowerInvariant()} ReducedUniverseExplicitFlagSatisfied={ReducedUniverseExplicitFlagSatisfied.ToString().ToLowerInvariant()} ReducedUniverseActivationBlockedReason={ReducedUniverseActivationBlockedReason} TradingReadiness={TradingReadiness.ToString().ToLowerInvariant()} SoakReadiness={SoakReadiness} SoakReadinessReason={SoakReadinessReason} LongRunStable={LongRunStable.ToString().ToLowerInvariant()} DiscoveryStable={DiscoveryStable.ToString().ToLowerInvariant()} PaperPretradeRejects={PaperPretradeRejects} PaperExecutions={PaperExecutionsCount} LiveTradingBlocked={LiveTradingBlockedCount} SigningAttempts={SigningAttempts} BatchBookRequests={BatchBookRequests} BatchBookBadRequests={BatchBookBadRequests} BatchBookInvalidTokens={BatchBookInvalidTokens} SingleMarketRawCandidates={SingleMarketRawCandidates} SingleMarketDataQualityRejected={SingleMarketDataQualityRejected} SingleMarketDataQualityRejectedRawPositive={SingleMarketDataQualityRejectedRawPositive} SingleMarketBelowMinEdge={SingleMarketBelowMinEdge} SingleMarketPositiveBeforeCost={SingleMarketPositiveBeforeCost} SingleMarketPositiveAfterCost={SingleMarketPositiveAfterCost} SingleMarketPositiveAfterSafety={SingleMarketPositiveAfterSafety} SingleMarketValidRawPositive={SingleMarketValidRawPositive} SingleMarketValidAfterCostPositive={SingleMarketValidAfterCostPositive} SingleMarketValidAfterSafetyPositive={SingleMarketValidAfterSafetyPositive} SingleMarketEdgeStable={SingleMarketEdgeStable} SingleMarketExecutionReady={SingleMarketExecutionReady} SingleMarketRejectedByFill={SingleMarketRejectedByFill} SingleMarketRejectedByDepth={SingleMarketRejectedByDepth} SingleMarketRejectedByRisk={SingleMarketRejectedByRisk} SingleMarketRejectedByPaperDiagnosticsLimitedGate={SingleMarketRejectedByPaperDiagnosticsLimitedGate} SingleMarketBestRawEdge={SingleMarketBestRawEdge?.ToString("0.####") ?? "N/A"} SingleMarketBestAfterCostEdge={SingleMarketBestAfterCostEdge?.ToString("0.####") ?? "N/A"} SingleMarketBestAfterSafetyEdge={SingleMarketBestAfterSafetyEdge?.ToString("0.####") ?? "N/A"} SingleMarketBestExecutableEdge={SingleMarketBestExecutableEdge?.ToString("0.####") ?? "N/A"} SingleMarketBestRejectedReason={SingleMarketBestRejectedReason} SingleMarketValidEdgeSamples={SingleMarketValidEdgeSamples} SingleMarketEdgeDistributionSampleMode={SingleMarketEdgeDistributionSampleMode} SingleMarketEdgeDistributionCapacity={SingleMarketEdgeDistributionCapacity} SingleMarketEdgeDistributionDroppedSamples={SingleMarketEdgeDistributionDroppedSamples} SingleMarketRawEdgeMin={Fmt(SingleMarketRawEdgeMin)} SingleMarketRawEdgeP01={Fmt(SingleMarketRawEdgeP01)} SingleMarketRawEdgeP05={Fmt(SingleMarketRawEdgeP05)} SingleMarketRawEdgeP10={Fmt(SingleMarketRawEdgeP10)} SingleMarketRawEdgeP25={Fmt(SingleMarketRawEdgeP25)} SingleMarketRawEdgeP50={Fmt(SingleMarketRawEdgeP50)} SingleMarketRawEdgeP75={Fmt(SingleMarketRawEdgeP75)} SingleMarketRawEdgeP90={Fmt(SingleMarketRawEdgeP90)} SingleMarketRawEdgeP95={Fmt(SingleMarketRawEdgeP95)} SingleMarketRawEdgeP99={Fmt(SingleMarketRawEdgeP99)} SingleMarketRawEdgeMax={Fmt(SingleMarketRawEdgeMax)} SingleMarketAfterCostEdgeMin={Fmt(SingleMarketAfterCostEdgeMin)} SingleMarketAfterCostEdgeP01={Fmt(SingleMarketAfterCostEdgeP01)} SingleMarketAfterCostEdgeP05={Fmt(SingleMarketAfterCostEdgeP05)} SingleMarketAfterCostEdgeP10={Fmt(SingleMarketAfterCostEdgeP10)} SingleMarketAfterCostEdgeP25={Fmt(SingleMarketAfterCostEdgeP25)} SingleMarketAfterCostEdgeP50={Fmt(SingleMarketAfterCostEdgeP50)} SingleMarketAfterCostEdgeP75={Fmt(SingleMarketAfterCostEdgeP75)} SingleMarketAfterCostEdgeP90={Fmt(SingleMarketAfterCostEdgeP90)} SingleMarketAfterCostEdgeP95={Fmt(SingleMarketAfterCostEdgeP95)} SingleMarketAfterCostEdgeP99={Fmt(SingleMarketAfterCostEdgeP99)} SingleMarketAfterCostEdgeMax={Fmt(SingleMarketAfterCostEdgeMax)} SingleMarketAfterSafetyEdgeMin={Fmt(SingleMarketAfterSafetyEdgeMin)} SingleMarketAfterSafetyEdgeP01={Fmt(SingleMarketAfterSafetyEdgeP01)} SingleMarketAfterSafetyEdgeP05={Fmt(SingleMarketAfterSafetyEdgeP05)} SingleMarketAfterSafetyEdgeP10={Fmt(SingleMarketAfterSafetyEdgeP10)} SingleMarketAfterSafetyEdgeP25={Fmt(SingleMarketAfterSafetyEdgeP25)} SingleMarketAfterSafetyEdgeP50={Fmt(SingleMarketAfterSafetyEdgeP50)} SingleMarketAfterSafetyEdgeP75={Fmt(SingleMarketAfterSafetyEdgeP75)} SingleMarketAfterSafetyEdgeP90={Fmt(SingleMarketAfterSafetyEdgeP90)} SingleMarketAfterSafetyEdgeP95={Fmt(SingleMarketAfterSafetyEdgeP95)} SingleMarketAfterSafetyEdgeP99={Fmt(SingleMarketAfterSafetyEdgeP99)} SingleMarketAfterSafetyEdgeMax={Fmt(SingleMarketAfterSafetyEdgeMax)} SingleMarketAfterSafetyEdgeBelowMinus5bp={SingleMarketAfterSafetyEdgeBelowMinus5bp} SingleMarketAfterSafetyEdgeMinus5bpToMinus2bp={SingleMarketAfterSafetyEdgeMinus5bpToMinus2bp} SingleMarketAfterSafetyEdgeMinus2bpToMinus1bp={SingleMarketAfterSafetyEdgeMinus2bpToMinus1bp} SingleMarketAfterSafetyEdgeMinus1bpTo0={SingleMarketAfterSafetyEdgeMinus1bpTo0} SingleMarketAfterSafetyEdge0To1bp={SingleMarketAfterSafetyEdge0To1bp} SingleMarketAfterSafetyEdge1bpTo5bp={SingleMarketAfterSafetyEdge1bpTo5bp} SingleMarketAfterSafetyEdgeAbove5bp={SingleMarketAfterSafetyEdgeAbove5bp} OrderbookCircuitBreakerActive={OrderbookCircuitBreakerActive.ToString().ToLowerInvariant()} Strategies={{{strategies}}} StrategyPaperOpened={{{strategyPaperOpened}}} Uptime={Uptime}";
    }


    private static string Fmt(decimal? value) => value.HasValue ? value.Value.ToString("0.####") : "N/A";

    private static string FormatStrategyRuntimeHealth(string key, TradingBot.Services.StrategyRuntimeCounterSnapshot value)
    {
        var baseText = $"{key}:{value.Mode}:scan={value.Scanned}:books={value.Books}:cand={value.Candidates}:positive={value.PositiveEdges}:ready={value.ExecutionReady}:execCand={value.ExecutionCandidates}:shadow={value.ShadowWouldOpen}:paper={value.PaperOpened}:edgeStable={value.EdgeStable}:blockedByMode={value.BlockedByMode}:blockedByPaperDiagnosticsLimitedGate={value.BlockedByPaperDiagnosticsLimitedGate}:blockedByOrderbookHealth={value.BlockedByOrderbookHealth}:blockedByRisk={value.BlockedByRisk}:blockedByFill={value.BlockedByFill}:blockedByDepth={value.BlockedByDepth}:bestAfterSafetyEdge={value.BestEdge?.ToString("0.####") ?? "N/A"}:bestRejectedReason={value.TopSkipReason}:validPriced={value.ValidPriced}:invalidOrUnpriced={value.InvalidOrUnpriced}:dataQualityRejected={value.DataQualityRejected}:unverified={value.Unverified}:reviewOnly={value.ReviewOnly}:missingPricing={value.MissingPricing}:bestCandidateValid={value.BestCandidateValid.ToString().ToLowerInvariant()}:bestCandidatePriced={value.BestCandidatePriced.ToString().ToLowerInvariant()}:bestCandidateExecutableLike={value.BestCandidateExecutableLike.ToString().ToLowerInvariant()}:bestCandidateReason={value.BestCandidateReason}:diagBlocked={value.DiagnosticsOnlyBlocked}:faults={value.Faults}";
        if (!key.Equals("VerifiedMultiOutcome", StringComparison.OrdinalIgnoreCase)) return baseText;
        return $"{baseText}:pricingMissingNoAsk={value.VerifiedPricingBlockedByMissingNoAsk}:pricingCircuitBreakerActive={value.VerifiedPricingBlockedByCircuitBreakerActive}:pricingMarketOrderbookQuarantined={value.VerifiedPricingBlockedByMarketOrderbookQuarantined}:pricingTokenQuarantined={value.VerifiedPricingBlockedByTokenQuarantined}:pricingEmptyBook={value.VerifiedPricingBlockedByEmptyBook}:pricingOrderbookUnavailable={value.VerifiedPricingBlockedByOrderbookUnavailable}:pricingQuarantinedToken={value.VerifiedPricingBlockedByQuarantinedToken}:activePositive={value.VerifiedActiveConservativePositive}:rawPositiveOnly={value.VerifiedRawPositiveOnly}:alternatePositive={value.VerifiedAlternateProfilePositive}:experimentalCandidates={value.VerifiedExperimentalProfileCandidate}:wouldOpenIfPaperEligible={value.VerifiedWouldOpenIfPaperEligible}:wouldOpenBlockedByStability={value.VerifiedRejectedByStability}:wouldOpenBlockedByRisk={value.VerifiedRejectedByRisk}:wouldOpenBlockedByFill={value.VerifiedWouldOpenBlockedByFill}:wouldOpenBlockedByDepth={value.VerifiedWouldOpenBlockedByDepth}:wouldOpenBlockedByCostProfile={value.VerifiedRejectedByCostProfile}:wouldOpenBlockedByUnknown={value.VerifiedWouldOpenBlockedByUnknown}:diagnosticsOnlyBlocked={value.VerifiedDiagnosticsOnlyBlocked}:executionReady={value.ExecutionReady}:paperOpened={value.PaperOpened}";
    }

    private static string FormatRejectedReasons(IReadOnlyDictionary<string, long> reasons)
        => reasons.Count == 0
            ? "None"
            : string.Join("|", reasons.OrderByDescending(r => r.Value).ThenBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Select(r => $"{r.Key}:{r.Value}"));

    public static bool ShouldLogAt(DateTime nowUtc, DateTime lastLoggedAtUtc, int everyMinutes)
        => lastLoggedAtUtc == DateTime.MinValue || nowUtc - lastLoggedAtUtc >= TimeSpan.FromMinutes(Math.Max(1, everyMinutes));

    public static RuntimeHealthSnapshot From(BotRuntimeState state) => From(state, null);

    public static RuntimeHealthSnapshot From(BotRuntimeState state, TradingBot.Options.TradingBotOptions? options)
    {
        state.ApplyReadinessInvariantCorrections(options?.MarketDiscovery.SourceAuditOnly ?? false);
        var p = Process.GetCurrentProcess();
        var sm = state.SingleMarketSnapshot.Summary;
        var dist = sm.EdgeDistribution ?? new TradingBot.Models.SingleMarketEdgeDistributionDto();
        var raw = dist.RawEdge ?? new TradingBot.Models.SingleMarketEdgeQuantilesDto();
        var afterCost = dist.AfterCostEdge ?? new TradingBot.Models.SingleMarketEdgeQuantilesDto();
        var afterSafety = dist.AfterSafetyEdge ?? new TradingBot.Models.SingleMarketEdgeQuantilesDto();
        var buckets = dist.ThresholdBuckets ?? new TradingBot.Models.SingleMarketAfterSafetyEdgeBucketsDto();
        var startedUtc = p.StartTime.Kind == DateTimeKind.Utc ? p.StartTime : p.StartTime.ToUniversalTime();
        return new RuntimeHealthSnapshot(
            ProcessRunId: state.ProcessRunId,
            StartedAtUtc: state.StartedAtUtc,
            ScannerInstanceId: state.ScannerInstanceId,
            DiagnosticsCounterMismatchCount: state.DiagnosticsCounterMismatchCount,
            DiagnosticsCounterMismatchLastReason: state.DiagnosticsCounterMismatchLastReason,
            ProcessMemoryMb: Math.Round(p.WorkingSet64 / 1024d / 1024d, 2),
            GcTotalMemoryMb: Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 2),
            PrivateMemoryMb: Math.Round(p.PrivateMemorySize64 / 1024d / 1024d, 2),
            WorkingSetMb: Math.Round(p.WorkingSet64 / 1024d / 1024d, 2),
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2),
            ThreadCount: p.Threads.Count,
            HandleCount: p.HandleCount,
            Uptime: DateTime.UtcNow - startedUtc,
            ScannerCycleId: state.ScannerStats.Sequence,
            LastScanDurationMs: state.ScannerStats.LastScanDurationMs,
            RecentLogsCount: state.Logs().Length,
            ScannerHistoryCount: state.ScannerStatsHistoryCount,
            CandidateSnapshotCount: state.CandidateSnapshotCount,
            RepairHistoryCount: state.RepairHistoryCount,
            UnresolvedDiagnosticsCount: state.UnresolvedDiagnosticsCount,
            DryRunOrderPlansCount: state.DryRunOrderPlansCount,
            FillSimulationsCount: state.FillSimulationsCount,
            ExecutionAuditCount: state.ExecutionAuditCount,
            SignalREventBufferCount: state.SignalREventBufferCount,
            OrderbookCacheCount: state.OrderbookCacheCount,
            MarketCacheCount: state.MarketCacheCount,
            ExportQueueCount: state.ExportQueueCount,
            PatchPreviewItemsCount: state.PatchPreviewItemsCount,
            SingleMarketOpportunitiesCount: state.SingleMarketOpportunitiesCount,
            SingleMarketNearMissesCount: state.SingleMarketSnapshot.TopNearMisses.Count,
            SingleMarketDataQualitySamplesCount: state.SingleMarketSnapshot.DataQualityRejectSamples.Count,
            SingleMarketExecutionsCount: state.SingleMarketExecutionsCount,
            PaperPhase: options?.TradingMode.PaperPhase ?? 0,
            PaperOpenPositions: state.PaperOpenPositions,
            PaperClosedPositions: state.PaperClosedPositions,
            PaperSettlements: state.PaperSettlements,
            PaperRealizedPnl: state.PaperRealizedPnl,
            PaperLocked: state.PaperLocked,
            PaperCash: state.PaperCash,
            PaperEquity: state.PaperEquity,
            PaperTotalExposure: state.PaperTotalExposure,
            PaperOpenCountLastHour: state.PaperOpenCountLastHour,
            PaperPretradeRejects: state.PaperPretradeRejects,
            PaperDuplicateSuppressions: state.PaperDuplicateSuppressions,
            PaperOpenPositionKeys: state.PaperOpenPositionKeys,
            PaperOpenMarketIdsCount: state.PaperOpenMarketIds.Count,
            PaperOpenMarketIdsSample: state.PaperOpenMarketIds.Take(10).ToArray(),
            PaperInFlightOpens: state.PaperInFlightOpens,
            PaperDuplicateDedupeEntries: state.PaperDuplicateDedupeEntries,
            PaperStaleDedupeEntriesCleared: state.PaperStaleDedupeEntriesCleared,
            PaperSettlementRejects: state.PaperSettlementRejects,
            PaperDuplicateSettlementSuppressions: state.PaperDuplicateSettlementSuppressions,
            PaperLifecycleEvents: state.PaperLifecycleEvents,
            PaperOpenEvents: state.PaperOpenEvents,
            PaperCloseEvents: state.PaperCloseEvents,
            LiveTradingBlockedCount: state.LiveTradingBlockedCount,
            PaperExecutionsCount: state.PaperExecutionsCount,
            SigningAttempts: TradingBot.Services.LiveTradingGuard.SigningAttempts,
            DuplicateGroupKeyWarnings: TradingBot.Services.GroupKeyDictionaryBuilder.DuplicateWarnings,
            QuietSuppressedTotal: state.QuietLogGateStats.QuietSuppressedTotal,
            EmittedLogs: state.QuietLogGateStats.EmittedLogs,
            LogGateCacheSize: state.QuietLogGateStats.LogGateCacheSize,
            QuietCappedSuppressions: state.QuietLogGateStats.CappedSuppressions,
            QuietSuppressedByCategory: state.QuietLogGateStats.QuietSuppressedByCategory,
            EmittedByCategory: state.QuietLogGateStats.EmittedByCategory,
            BatchBookRequests: state.OrderBookServiceStats.BatchRequests,
            BatchBookBadRequests: state.OrderBookServiceStats.BatchBadRequests,
            BatchBookTimeouts: state.OrderBookServiceStats.BatchTimeouts,
            BatchBookRetrySuccesses: state.OrderBookServiceStats.BatchRetrySuccesses,
            BatchBookInvalidTokens: state.OrderBookServiceStats.BatchInvalidTokens,
            BatchBookSuppressedErrors: state.OrderBookServiceStats.BatchSuppressedErrors,
            BatchBookSplitRetriesAttempted: state.OrderBookServiceStats.BatchBookSplitRetriesAttempted,
            BatchBookSplitRetrySucceeded: state.OrderBookServiceStats.BatchBookSplitRetrySucceeded,
            BatchBookSplitRetryFailed: state.OrderBookServiceStats.BatchBookSplitRetryFailed,
            BatchBookSingleTokenFailures: state.OrderBookServiceStats.BatchBookSingleTokenFailures,
            BatchBookSingleTokenQuarantined: state.OrderBookServiceStats.BatchBookSingleTokenQuarantined,
            BatchBookSkippedQuarantinedTokens: state.OrderBookServiceStats.BatchBookSkippedQuarantinedTokens,
            BatchBookSkippedMarketsWithQuarantinedTokens: state.OrderBookServiceStats.BatchBookSkippedMarketsWithQuarantinedTokens,
            BatchBookRepeatedInvalidTokenAfterQuarantine: state.OrderBookServiceStats.BatchBookRepeatedInvalidTokenAfterQuarantine,
            OrderbookUnavailableMarkets: state.OrderBookServiceStats.OrderbookUnavailableMarkets,
            InvalidTokenQuarantineActive: state.OrderBookServiceStats.InvalidTokenQuarantineActive,
            InvalidTokenQuarantineAdded: state.OrderBookServiceStats.InvalidTokenQuarantineAdded,
            InvalidTokenQuarantineExpired: state.OrderBookServiceStats.InvalidTokenQuarantineExpired,
            BatchBookRequestsAvoidedByQuarantine: state.OrderBookServiceStats.BatchBookRequestsAvoidedByQuarantine,
            MarketsSkippedByInvalidTokenQuarantine: state.OrderBookServiceStats.MarketsSkippedByInvalidTokenQuarantine,
            MarketOrderbookQuarantineActive: state.OrderBookServiceStats.MarketOrderbookQuarantineActive,
            MarketOrderbookQuarantineAdded: state.OrderBookServiceStats.MarketOrderbookQuarantineAdded,
            MarketOrderbookQuarantineExpired: state.OrderBookServiceStats.MarketOrderbookQuarantineExpired,
            MarketsSkippedByMarketOrderbookQuarantine: state.OrderBookServiceStats.MarketsSkippedByMarketOrderbookQuarantine,
            BatchBookRequestsAvoidedByMarketQuarantine: state.OrderBookServiceStats.BatchBookRequestsAvoidedByMarketQuarantine,
            OrderbookCircuitBreakerActive: state.OrderBookServiceStats.OrderbookCircuitBreakerActive,
            OrderbookCircuitBreakerState: state.OrderBookServiceStats.OrderbookCircuitBreakerState,
            OrderbookCircuitBreakerOpenCount: state.OrderBookServiceStats.OrderbookCircuitBreakerOpenCount,
            OrderbookCircuitBreakerCooldownRemainingSeconds: state.OrderBookServiceStats.OrderbookCircuitBreakerCooldownRemainingSeconds,
            OrderbookCircuitBreakerHalfOpenAttempts: state.OrderBookServiceStats.OrderbookCircuitBreakerHalfOpenAttempts,
            OrderbookCircuitBreakerHalfOpenSucceeded: state.OrderBookServiceStats.OrderbookCircuitBreakerHalfOpenSucceeded,
            OrderbookCircuitBreakerHalfOpenFailed: state.OrderBookServiceStats.OrderbookCircuitBreakerHalfOpenFailed,
            OrderbookCircuitBreakerHalfOpenStartedUtc: state.OrderBookServiceStats.OrderbookCircuitBreakerHalfOpenStartedUtc,
            OrderbookCircuitBreakerHalfOpenAgeSeconds: state.OrderBookServiceStats.OrderbookCircuitBreakerHalfOpenAgeSeconds,
            OrderbookCircuitBreakerHalfOpenMaxSeconds: state.OrderBookServiceStats.OrderbookCircuitBreakerHalfOpenMaxSeconds,
            OrderbookCircuitBreakerHalfOpenTimedOutCount: state.OrderBookServiceStats.OrderbookCircuitBreakerHalfOpenTimedOutCount,
            BatchBookCanaryTimeouts: state.OrderBookServiceStats.BatchBookCanaryTimeouts,
            BatchBookCanaryInvalidTokens: state.OrderBookServiceStats.BatchBookCanaryInvalidTokens,
            BatchBookCanaryOrderbookUnavailable: state.OrderBookServiceStats.BatchBookCanaryOrderbookUnavailable,
            OrderbookCircuitBreakerLastHalfOpenFailureReason: state.OrderBookServiceStats.OrderbookCircuitBreakerLastHalfOpenFailureReason,
            OrderbookCircuitBreakerCooldownExtensions: state.OrderBookServiceStats.OrderbookCircuitBreakerCooldownExtensions,
            OrderbookCircuitBreakerLastOpenReason: state.OrderBookServiceStats.OrderbookCircuitBreakerLastOpenReason,
            OrderbookCircuitBreakerRecoveringSinceUtc: state.OrderBookServiceStats.OrderbookCircuitBreakerRecoveringSinceUtc,
            OrderbookCircuitBreakerRecoveryRemainingSeconds: state.OrderBookServiceStats.OrderbookCircuitBreakerRecoveryRemainingSeconds,
            OrderbookCircuitBreakerReopenedAfterClose: state.OrderBookServiceStats.OrderbookCircuitBreakerReopenedAfterClose,
            OrderbookRequestsBlockedByCircuitBreaker: state.OrderBookServiceStats.OrderbookRequestsBlockedByCircuitBreaker,
            OrderbookPostCloseBadRequests: state.OrderBookServiceStats.OrderbookPostCloseBadRequests,
            OrderbookPostCloseInvalidTokens: state.OrderBookServiceStats.OrderbookPostCloseInvalidTokens,
            SingleTokenIsolationBudgetExhausted: state.OrderBookServiceStats.SingleTokenIsolationBudgetExhausted,
            BatchBookBadRequestsPreventedEstimate: state.OrderBookServiceStats.BatchBookBadRequestsPreventedEstimate,
            BatchBookCanaryRequests: state.OrderBookServiceStats.BatchBookCanaryRequests,
            BatchBookCanaryBadRequests: state.OrderBookServiceStats.BatchBookCanaryBadRequests,
            BatchBookRecoveryRequests: state.OrderBookServiceStats.BatchBookRecoveryRequests,
            BatchBookRecoveryBadRequests: state.OrderBookServiceStats.BatchBookRecoveryBadRequests,
            OrderbookRecoveryLimitedRequests: state.OrderBookServiceStats.OrderbookRecoveryLimitedRequests,
            OrderbookRecoveryLimitedMarkets: state.OrderBookServiceStats.OrderbookRecoveryLimitedMarkets,
            OrderbookRecoveryBadRequests: state.OrderBookServiceStats.OrderbookRecoveryBadRequests,
            OrderbookRecoveryInvalidTokens: state.OrderBookServiceStats.OrderbookRecoveryInvalidTokens,
            OrderbookRecoverySucceededCount: state.OrderBookServiceStats.OrderbookRecoverySucceededCount,
            OrderbookRecoveryFailedCount: state.OrderBookServiceStats.OrderbookRecoveryFailedCount,
            BatchBookNormalRequests: state.OrderBookServiceStats.BatchBookNormalRequests,
            BatchBookNormalBadRequests: state.OrderBookServiceStats.BatchBookNormalBadRequests,
            BatchBookNormalRequestsBeforeBreakerOpen: state.OrderBookServiceStats.BatchBookNormalRequestsBeforeBreakerOpen,
            BatchBookNormalBadRequestsBeforeBreakerOpen: state.OrderBookServiceStats.BatchBookNormalBadRequestsBeforeBreakerOpen,
            BatchBookNormalRequestsAfterBreakerOpen: state.OrderBookServiceStats.BatchBookNormalRequestsAfterBreakerOpen,
            BatchBookNormalBadRequestsAfterBreakerOpen: state.OrderBookServiceStats.BatchBookNormalBadRequestsAfterBreakerOpen,
            QuarantinedMarketsReintroducedBlocked: state.OrderBookServiceStats.QuarantinedMarketsReintroducedBlocked,
            QuarantinedTokensReintroducedBlocked: state.OrderBookServiceStats.QuarantinedTokensReintroducedBlocked,
            AllowlistHealthy: state.AllowlistHealthy,
            AllowlistMonitoringOnly: state.AllowlistMonitoringOnly,
            AllowlistNeedsPricingPrune: state.AllowlistNeedsPricingPrune,
            AllowlistNeedsRefresh: state.AllowlistNeedsRefresh,
            AllowlistReviewOnly: state.AllowlistReviewOnly,
            AllowlistMismatch: state.AllowlistMismatch,
            AllowlistBrokenConfig: state.AllowlistBrokenConfig,
            AllowlistDisabled: state.AllowlistDisabled,
            AllowlistIgnored: state.AllowlistIgnored,
            AllowlistClassificationTotal: state.AllowlistClassificationTotal,
            AllowlistClassificationValid: state.AllowlistClassificationValid,
            AllowlistRefreshPreviewCandidates: state.AllowlistRefreshPreviewCandidates,
            AllowlistRefreshHighConfidence: state.AllowlistRefreshHighConfidence,
            AllowlistRefreshFinalNoCandidate: state.AllowlistRefreshFinalNoCandidate,
            AllowlistRefreshFinalSemanticConflict: state.AllowlistRefreshFinalSemanticConflict,
            AllowlistRefreshFinalLowConfidence: state.AllowlistRefreshFinalLowConfidence,
            AllowlistRefreshFinalUnstable: state.AllowlistRefreshFinalUnstable,
            AllowlistRefreshFinalPreviewOnly: state.AllowlistRefreshFinalPreviewOnly,
            AllowlistRefreshFinalLockedManualReview: state.AllowlistRefreshFinalLockedManualReview,
            AllowlistRefreshActionExplainedSuppressed: state.AllowlistRefreshActionExplainedSuppressed,
            AllowlistRefreshUnstableGroups: state.AllowlistRefreshUnstableGroups,
            AllowlistRefreshActionFlipFlops: state.AllowlistRefreshActionFlipFlops,
            AllowlistRefreshAutoApply: options?.AllowlistRepair.RefreshPreview.AutoApply ?? false,
            DiscoveryHealthy: state.DiscoveryHealthy,
            DiscoveryUsingLastHealthySnapshot: state.DiscoveryUsingLastHealthySnapshot,
            DiscoveryLastHealthySnapshotAgeSeconds: state.DiscoveryLastHealthySnapshotAgeSeconds,
            DiscoveryPartialAttemptCount: state.DiscoveryPartialAttemptCount,
            DiscoveryLastFailureReason: state.DiscoveryLastFailureReason,
            ScannerPausedByDiscoveryGuard: state.ScannerPausedByDiscoveryGuard,
            DiscoveryGuardSkippedCycles: state.DiscoveryGuardSkippedCycles,
            DiscoveryGuardUsingLastHealthySnapshot: state.DiscoveryGuardUsingLastHealthySnapshot,
            DiscoveryGuardBlockedNewMarkets: state.DiscoveryGuardBlockedNewMarkets,
            LongRunStable: state.LongRunStable,
            LongRunBlockingReason: state.LongRunBlockingReason,
            DiscoveryStable: state.DiscoveryStable,
            OrderbookRecoveredAfterDegradation: state.OrderbookRecoveredAfterDegradation,
            LastDegradationUtc: state.LastDegradationUtc,
            LastRecoveryUtc: state.LastRecoveryUtc,
            DiscoveryBootstrapHealthy: state.DiscoveryBootstrapHealthy,
            DiscoveryBootstrapRetryCount: state.DiscoveryBootstrapRetryCount,
            DiscoveryBootstrapLastAttemptUtc: state.DiscoveryBootstrapLastAttemptUtc,
            DiscoveryBootstrapNextRetryUtc: state.DiscoveryBootstrapNextRetryUtc,
            DiscoveryBootstrapBackoffSeconds: state.DiscoveryBootstrapBackoffSeconds,
            DiscoveryBootstrapFailureReason: state.DiscoveryBootstrapFailureReason,
            DiscoveryRetryBackoffSeconds: state.DiscoveryRetryBackoffSeconds,
            DiscoveryRetriesSuppressedByBackoff: state.DiscoveryRetriesSuppressedByBackoff,
            DiscoveryPersistedSnapshotLoaded: state.DiscoveryPersistedSnapshotLoaded,
            DiscoveryPersistedSnapshotAgeSeconds: state.DiscoveryPersistedSnapshotAgeSeconds,
            DiscoveryPersistedSnapshotActiveMarkets: state.DiscoveryPersistedSnapshotActiveMarkets,
            AllowlistEvaluationSkipped: state.AllowlistEvaluationSkipped,
            AllowlistEvaluationSkippedReason: state.AllowlistEvaluationSkippedReason,
            AllowlistClassificationBlockedByDiscovery: state.AllowlistClassificationBlockedByDiscovery,
            DiscoveryBlockedReason: state.DiscoveryBlockedReason,
            DiscoverySelectedSource: state.DiscoverySelectedSource,
            DiscoveryScannerSafeSourceAvailable: state.DiscoveryScannerSafeSourceAvailable,
            DiscoverySourceAuditOnly: state.DiscoverySourceAuditOnly,
            DiscoverySourceAuditExportWritten: state.DiscoverySourceAuditExportWritten,
            DiscoverySourceAuditExportPath: state.DiscoverySourceAuditExportPath,
            DiscoverySourceAuditSources: state.DiscoverySourceAuditSources,
            DiscoverySourceAuditScannerSafeSources: state.DiscoverySourceAuditScannerSafeSources,
            DiscoverySourceAuditRecommendedAction: state.DiscoverySourceAuditRecommendedAction,
            SoakReadiness: state.SoakReadiness,
            SoakReadinessReason: state.SoakReadinessReason,
            DiscoveryReducedUniverse: state.DiscoveryReducedUniverse,
            ReducedUniverseMarkets: state.ReducedUniverseMarkets,
            ReducedUniverseRawMarkets: state.ReducedUniverseRawMarkets,
            ReducedUniverseFilteredMarkets: state.ReducedUniverseFilteredMarkets,
            ReducedUniverseExcludedInvalidTokens: state.ReducedUniverseExcludedInvalidTokens,
            ReducedUniverseExcludedQuarantinedMarkets: state.ReducedUniverseExcludedQuarantinedMarkets,
            ReducedUniverseExcludedBadHistory: state.ReducedUniverseExcludedBadHistory,
            ReducedUniverseOrderbookEligibleMarkets: state.ReducedUniverseOrderbookEligibleMarkets,
            ReducedUniverseOrderbookStable: state.ReducedUniverseOrderbookStable,
            ReducedUniverseScanPausedByOrderbookHealth: state.OrderBookServiceStats.ReducedUniverseScanPausedByOrderbookHealth,
            ReducedUniverseOrderbookRecoveryMode: state.OrderBookServiceStats.ReducedUniverseOrderbookRecoveryMode,
            ReducedUniverseOrderbookRecoveryCleanWindowSeconds: state.OrderBookServiceStats.ReducedUniverseOrderbookRecoveryCleanWindowSeconds,
            ReducedUniversePostRecoveryBadRequests: state.OrderBookServiceStats.ReducedUniversePostRecoveryBadRequests,
            MarketOrderbookQuarantineLifecycleBalanced: state.OrderBookServiceStats.MarketOrderbookQuarantineLifecycleBalanced,
            InvalidTokenQuarantineLifecycleBalanced: state.OrderBookServiceStats.InvalidTokenQuarantineLifecycleBalanced,
            OrderbookQuarantineLifecycleMismatchReason: state.OrderBookServiceStats.OrderbookQuarantineLifecycleMismatchReason,
            InFlightBeforeBreakerCompletedAfterOpen: state.OrderBookServiceStats.InFlightBeforeBreakerCompletedAfterOpen,
            InFlightBeforeBreakerBadRequestsAfterOpen: state.OrderBookServiceStats.InFlightBeforeBreakerBadRequestsAfterOpen,
            TruePostBreakerNormalRequests: state.OrderBookServiceStats.TruePostBreakerNormalRequests,
            TruePostBreakerBadRequests: state.OrderBookServiceStats.TruePostBreakerBadRequests,
            ReducedUniverseBadHistoryActive: state.OrderBookServiceStats.ReducedUniverseBadHistoryActive,
            ReducedUniverseBadHistoryLoaded: state.OrderBookServiceStats.ReducedUniverseBadHistoryLoaded,
            ReducedUniverseBadHistoryExpired: state.OrderBookServiceStats.ReducedUniverseBadHistoryExpired,
            SingleMarketScanPausedByOrderbookHealth: state.SingleMarketScanPausedByOrderbookHealth,
            SingleMarketPausedCycles: state.SingleMarketPausedCycles,
            SingleMarketNormalCycles: state.SingleMarketNormalCycles,
            SingleMarketFullCyclesCompleted: state.SingleMarketFullCyclesCompleted,
            PaperDiagnosticsLimitedEligible: IsPaperDiagnosticsLimitedEligible(state, options),
            PaperDiagnosticsLimitedBlockedReason: PaperDiagnosticsLimitedReason(state, options),
            PaperDiagnosticsLimitedEnabled: options?.PaperDiagnosticsLimited.Enabled ?? false,
            PaperDiagnosticsLimitedAllowedStrategy: options?.PaperDiagnosticsLimited.AllowedStrategy ?? "SingleMarketBuyBoth",
            PaperDiagnosticsLimitedMaxOpenPositions: options?.PaperDiagnosticsLimited.MaxOpenPositions ?? 1,
            PaperDiagnosticsLimitedMaxPaperNotionalPerTrade: options?.PaperDiagnosticsLimited.MaxPaperNotionalPerTrade ?? 5m,
            PaperDiagnosticsLimitedMaxPaperTotalExposure: options?.PaperDiagnosticsLimited.MaxPaperTotalExposure ?? 5m,
            PaperDiagnosticsLimitedOpensLastHour: state.PaperOpenCountLastHour,
            PaperDiagnosticsLimitedGateLastRejectReason: state.PaperPretradeRejectsByReason.TryGetValue("PaperBlockedByDiagnosticsLimitedGate", out var gateRejects) && gateRejects > 0 ? "PaperBlockedByDiagnosticsLimitedGate" : "None",
            PaperDiagnosticsLimitedPaperOpened: options?.PaperDiagnosticsLimited.Enabled == true ? state.PaperExecutionsCount : 0,
            OrderbookStableNow: ComputeOrderbookStableNow(state),
            OrderbookStableWindowMinutes: options?.RuntimeHealth.SoakTrendWindowMinutes ?? 0,
            BatchBookBadRequestsDeltaWindow: 0,
            BatchBookInvalidTokensDeltaWindow: 0,
            PostBreakerBadRequestsDeltaWindow: 0,
            ReducedUniverseOrderbookStableNow: ComputeOrderbookStableNow(state),
            ReducedUniverseMaxMarkets: state.ReducedUniverseMaxMarkets,
            ReducedUniverseSource: state.ReducedUniverseSource,
            PaperExecutionGloballyBlockedByDiscovery: state.PaperExecutionGloballyBlockedByDiscovery,
            PaperBlockedByDiscoveryMode: state.PaperBlockedByDiscoveryMode,
            StrategyExecutionGloballyBlocked: state.StrategyExecutionGloballyBlocked,
            DiagnosticsUniverse: state.DiagnosticsUniverse,
            TradingReadiness: state.TradingReadiness,
            AllowReducedUniverseDiagnosticsOnly: state.AllowReducedUniverseDiagnosticsOnly,
            ReducedUniverseRequireExplicitFlag: state.ReducedUniverseRequireExplicitFlag,
            ReducedUniverseExplicitFlagSatisfied: state.ReducedUniverseExplicitFlagSatisfied,
            ReducedUniverseActivationBlockedReason: state.ReducedUniverseActivationBlockedReason,
            RuntimeStatusExportWriteFailures: state.RuntimeStatusExportWriteFailures,
            RuntimeStatusExportReadFailures: state.RuntimeStatusExportReadFailures,
            RuntimeStatusExportLastFailureReason: state.RuntimeStatusExportLastFailureReason,
            RuntimeStatusExportRecoveredCount: state.RuntimeStatusExportRecoveredCount,
            RuntimeStatusExportStable: state.RuntimeStatusExportStable,
            SingleMarketRawCandidates: sm.BothAsks,
            SingleMarketDataQualityRejected: sm.DataQualityRejected,
            SingleMarketDataQualityRejectedRawPositive: sm.DataQualityRejectedRawPositive,
            SingleMarketBelowMinEdge: sm.BelowMinEdge,
            SingleMarketPositiveBeforeCost: sm.ValidRawPositive,
            SingleMarketPositiveAfterCost: sm.ValidAfterCostPositive,
            SingleMarketPositiveAfterSafety: sm.ValidAfterSafetyPositive,
            SingleMarketValidRawPositive: sm.ValidRawPositive,
            SingleMarketValidAfterCostPositive: sm.ValidAfterCostPositive,
            SingleMarketValidAfterSafetyPositive: sm.ValidAfterSafetyPositive,
            SingleMarketEdgeStable: sm.EdgeStable,
            SingleMarketExecutionReady: sm.ExecutionReady,
            SingleMarketRejectedByFill: sm.RejectedByFill,
            SingleMarketRejectedByDepth: sm.RejectedByDepth,
            SingleMarketRejectedByRisk: sm.RejectedByRisk,
            SingleMarketRejectedByPaperDiagnosticsLimitedGate: sm.RejectedByPaperDiagnosticsLimitedGate,
            SingleMarketBestRawEdge: sm.BestRawEdge,
            SingleMarketBestAfterCostEdge: sm.BestAfterCostEdge,
            SingleMarketBestAfterSafetyEdge: sm.BestAfterSafetyEdge,
            SingleMarketBestExecutableEdge: sm.BestExecutableEdge,
            SingleMarketBestRejectedReason: sm.BestRejectedReason,
            SingleMarketValidEdgeSamples: dist.ValidEdgeSamples,
            SingleMarketEdgeDistributionSampleMode: dist.SampleMode,
            SingleMarketEdgeDistributionCapacity: dist.Capacity,
            SingleMarketEdgeDistributionDroppedSamples: dist.DroppedSamples,
            SingleMarketRawEdgeMin: raw.Min,
            SingleMarketRawEdgeP01: raw.P01,
            SingleMarketRawEdgeP05: raw.P05,
            SingleMarketRawEdgeP10: raw.P10,
            SingleMarketRawEdgeP25: raw.P25,
            SingleMarketRawEdgeP50: raw.P50,
            SingleMarketRawEdgeP75: raw.P75,
            SingleMarketRawEdgeP90: raw.P90,
            SingleMarketRawEdgeP95: raw.P95,
            SingleMarketRawEdgeP99: raw.P99,
            SingleMarketRawEdgeMax: raw.Max,
            SingleMarketAfterCostEdgeMin: afterCost.Min,
            SingleMarketAfterCostEdgeP01: afterCost.P01,
            SingleMarketAfterCostEdgeP05: afterCost.P05,
            SingleMarketAfterCostEdgeP10: afterCost.P10,
            SingleMarketAfterCostEdgeP25: afterCost.P25,
            SingleMarketAfterCostEdgeP50: afterCost.P50,
            SingleMarketAfterCostEdgeP75: afterCost.P75,
            SingleMarketAfterCostEdgeP90: afterCost.P90,
            SingleMarketAfterCostEdgeP95: afterCost.P95,
            SingleMarketAfterCostEdgeP99: afterCost.P99,
            SingleMarketAfterCostEdgeMax: afterCost.Max,
            SingleMarketAfterSafetyEdgeMin: afterSafety.Min,
            SingleMarketAfterSafetyEdgeP01: afterSafety.P01,
            SingleMarketAfterSafetyEdgeP05: afterSafety.P05,
            SingleMarketAfterSafetyEdgeP10: afterSafety.P10,
            SingleMarketAfterSafetyEdgeP25: afterSafety.P25,
            SingleMarketAfterSafetyEdgeP50: afterSafety.P50,
            SingleMarketAfterSafetyEdgeP75: afterSafety.P75,
            SingleMarketAfterSafetyEdgeP90: afterSafety.P90,
            SingleMarketAfterSafetyEdgeP95: afterSafety.P95,
            SingleMarketAfterSafetyEdgeP99: afterSafety.P99,
            SingleMarketAfterSafetyEdgeMax: afterSafety.Max,
            SingleMarketAfterSafetyEdgeBelowMinus5bp: buckets.BelowMinus5bp,
            SingleMarketAfterSafetyEdgeMinus5bpToMinus2bp: buckets.Minus5bpToMinus2bp,
            SingleMarketAfterSafetyEdgeMinus2bpToMinus1bp: buckets.Minus2bpToMinus1bp,
            SingleMarketAfterSafetyEdgeMinus1bpTo0: buckets.Minus1bpTo0,
            SingleMarketAfterSafetyEdge0To1bp: buckets.ZeroTo1bp,
            SingleMarketAfterSafetyEdge1bpTo5bp: buckets.OnebpTo5bp,
            SingleMarketAfterSafetyEdgeAbove5bp: buckets.Above5bp,
            StrategyCounters: state.StrategyCountersSnapshot());
    }

    private static string PaperDiagnosticsLimitedReason(BotRuntimeState state, TradingBot.Options.TradingBotOptions? options)
    {
        var stats = state.OrderBookServiceStats;
        var reasons = new List<string>();
        if (!state.AllowReducedUniverseDiagnosticsOnly || !state.ReducedUniverseExplicitFlagSatisfied) reasons.Add("NotExplicitlyEnabled");
        if (!string.Equals(state.DiscoverySelectedSource, "ReducedUniverseDiagnosticsOnly", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(state.DiagnosticsUniverse, "Reduced", StringComparison.OrdinalIgnoreCase))
            reasons.Add(state.DiscoverySelectedSource.Equals("Blocked", StringComparison.OrdinalIgnoreCase) || !state.DiscoveryScannerSafeSourceAvailable ? "NoScannerSafeDiscoverySource" : "ReducedUniverseNotActive");
        if (!state.DiscoveryReducedUniverse) reasons.Add("ReducedUniverseNotActive");
        if (state.ReducedUniverseMarkets <= 0 || state.ReducedUniverseOrderbookEligibleMarkets <= 0) reasons.Add("ReducedUniverseEmpty");
        if (state.ScannerPausedByDiscoveryGuard) reasons.Add("ScannerPausedByDiscoveryGuard");
        if (!state.ReducedUniverseOrderbookStable || stats.ReducedUniverseScanPausedByOrderbookHealth) reasons.Add("ReducedUniverseOrderbookUnstable");
        if (!string.Equals(stats.OrderbookCircuitBreakerState, "Closed", StringComparison.OrdinalIgnoreCase)) reasons.Add("OrderbookCircuitBreakerNotClosed");
        if (stats.OrderbookCircuitBreakerActive) reasons.Add("OrderbookCircuitBreakerNotClosed");
        if (stats.ReducedUniverseOrderbookRecoveryMode) reasons.Add("ReducedUniverseOrderbookUnstable");
        if (!ComputeOrderbookStableNow(state)) reasons.Add("OrderbookStableNowFalse");
        if (!SingleMarketScannedThisRun(state)) reasons.Add("NoScannerSafeDiscoverySource");
        if (state.SingleMarketScanPausedByOrderbookHealth > 0) reasons.Add("ReducedUniverseOrderbookUnstable");
        if (stats.TruePostBreakerBadRequests > 0) reasons.Add("TruePostBreakerBadRequests");
        if (stats.InvalidTokenQuarantineActive > 0 || stats.MarketOrderbookQuarantineActive > 0) reasons.Add("ReducedUniverseOrderbookUnstable");
        if (!stats.MarketOrderbookQuarantineLifecycleBalanced || !stats.InvalidTokenQuarantineLifecycleBalanced) reasons.Add("ReducedUniverseOrderbookUnstable");
        if (state.DiagnosticsCounterMismatchCount > 0) reasons.Add("DiagnosticsCounterMismatch");
        if (!MemoryStableForPaper(state)) reasons.Add("MemoryUnstable");
        if (!LogVolumeStableForPaper(state, options)) reasons.Add("LogVolumeUnstable");
        if (state.LiveTradingBlockedCount > 0) reasons.Add("LiveTradingSafety");
        if (TradingBot.Services.LiveTradingGuard.SigningAttempts > 0) reasons.Add("SigningAttemptDetected");
        return reasons.Count == 0 ? "None" : string.Join("|", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static bool ComputeOrderbookStableNow(BotRuntimeState state)
    {
        var stats = state.OrderBookServiceStats;
        return string.Equals(stats.OrderbookCircuitBreakerState, "Closed", StringComparison.OrdinalIgnoreCase)
            && !stats.ReducedUniverseOrderbookRecoveryMode
            && !stats.ReducedUniverseScanPausedByOrderbookHealth
            && stats.TruePostBreakerBadRequests == 0
            && stats.MarketOrderbookQuarantineActive == 0
            && stats.InvalidTokenQuarantineActive == 0;
    }

    private static bool IsPaperDiagnosticsLimitedEligible(BotRuntimeState state, TradingBot.Options.TradingBotOptions? options)
    {
        var stats = state.OrderBookServiceStats;
        return state.AllowReducedUniverseDiagnosticsOnly
            && state.ReducedUniverseExplicitFlagSatisfied
            && string.Equals(state.DiscoverySelectedSource, "ReducedUniverseDiagnosticsOnly", StringComparison.OrdinalIgnoreCase)
            && state.DiscoveryReducedUniverse
            && string.Equals(state.DiagnosticsUniverse, "Reduced", StringComparison.OrdinalIgnoreCase)
            && state.ReducedUniverseMarkets > 0
            && state.ReducedUniverseOrderbookEligibleMarkets > 0
            && !state.ScannerPausedByDiscoveryGuard
            && !stats.OrderbookCircuitBreakerActive
            && ComputeOrderbookStableNow(state)
            && state.ReducedUniverseOrderbookStable
            && SingleMarketScannedThisRun(state)
            && state.SingleMarketScanPausedByOrderbookHealth == 0
            && stats.TruePostBreakerBadRequests == 0
            && state.DiagnosticsCounterMismatchCount == 0
            && MemoryStableForPaper(state)
            && LogVolumeStableForPaper(state, options)
            && state.LiveTradingBlockedCount == 0
            && TradingBot.Services.LiveTradingGuard.SigningAttempts == 0;
    }

    private static bool SingleMarketScannedThisRun(BotRuntimeState state)
        => state.StrategyCountersSnapshot().TryGetValue("SingleMarketBuyBoth", out var single) && single.Scanned > 0;

    private static bool MemoryStableForPaper(BotRuntimeState state)
        => state.MemoryCriticals == 0 && !state.ScannerPausedByMemoryGuard;

    private static bool LogVolumeStableForPaper(BotRuntimeState state, TradingBot.Options.TradingBotOptions? options)
        => options is null || state.Logs().Length <= options.RuntimeState.MaxRecentLogs;
}

public sealed record RuntimeHealthTrend(
    double MinProcessMemoryMbWindow,
    double MaxProcessMemoryMbWindow,
    double MemoryDeltaMbWindow,
    double MemorySlopeMbPerMinute,
    bool IsMemoryStable,
    int Samples,
    long BatchBookBadRequestsDeltaLastHour = 0,
    long BatchBookInvalidTokensDeltaLastHour = 0,
    long SkippedQuarantinedTokensLastHour = 0,
    long PostBreakerBadRequestsDeltaWindow = 0);

public static class RuntimeHealthTrendTracker
{
    private static readonly object Gate = new();
    private static readonly List<(DateTime TimestampUtc, double ProcessMb, long BatchBadRequests, long BatchInvalidTokens, long SkippedQuarantinedTokens, long TruePostBreakerBadRequests)> Samples = new();

    public static RuntimeHealthTrend RecordAndAnalyze(RuntimeHealthSnapshot snapshot, TradingBot.Options.RuntimeHealthOptions options)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            Samples.Add((now, snapshot.ProcessMemoryMb, snapshot.BatchBookBadRequests, snapshot.BatchBookInvalidTokens, snapshot.BatchBookSkippedQuarantinedTokens, snapshot.TruePostBreakerBadRequests));
            Trim(now, options.SoakTrendWindowMinutes);
            return AnalyzeNoLock(options, snapshot);
        }
    }

    public static RuntimeHealthTrend Analyze(IEnumerable<(DateTime TimestampUtc, double ProcessMb)> samples, TradingBot.Options.RuntimeHealthOptions options)
    {
        lock (Gate)
        {
            Samples.Clear();
            Samples.AddRange(samples.OrderBy(x => x.TimestampUtc).Select(x => (x.TimestampUtc, x.ProcessMb, 0L, 0L, 0L, 0L)));
            var now = Samples.Count == 0 ? DateTime.UtcNow : Samples[^1].TimestampUtc;
            Trim(now, options.SoakTrendWindowMinutes);
            return AnalyzeNoLock(options);
        }
    }

    public static RuntimeHealthTrend Current(TradingBot.Options.RuntimeHealthOptions options)
    {
        lock (Gate)
        {
            Trim(DateTime.UtcNow, options.SoakTrendWindowMinutes);
            return AnalyzeNoLock(options);
        }
    }

    public static string ToSoakStatusLogLine(RuntimeHealthSnapshot health, RuntimeHealthTrend trend, TradingBot.Options.TradingBotOptions? options = null, BotRuntimeState? state = null)
    {
        var logVolumeStable = options is null || IsLogVolumeStable(health, options);
        var batchRate = health.BatchBookRequests <= 0 ? 0d : (double)health.BatchBookBadRequests / health.BatchBookRequests;
        var orderbookStable = options is null || (health.OrderbookCircuitBreakerState == "Closed"
            && trend.BatchBookBadRequestsDeltaLastHour <= options.Soak.MaxBatchBookBadRequestsPerHour
            && trend.BatchBookInvalidTokensDeltaLastHour <= options.Soak.MaxBatchBookInvalidTokensPerHour
            && trend.PostBreakerBadRequestsDeltaWindow == 0
            && health.TruePostBreakerBadRequests == 0
            && health.InvalidTokenQuarantineActive == 0
            && health.MarketOrderbookQuarantineActive == 0
            && health.MarketOrderbookQuarantineLifecycleBalanced
            && health.InvalidTokenQuarantineLifecycleBalanced);
        var orderbookStableNow = health.OrderbookCircuitBreakerState == "Closed"
            && !health.ReducedUniverseOrderbookRecoveryMode
            && !health.ReducedUniverseScanPausedByOrderbookHealth
            && trend.BatchBookBadRequestsDeltaLastHour == 0
            && trend.BatchBookInvalidTokensDeltaLastHour == 0
            && trend.PostBreakerBadRequestsDeltaWindow == 0
            && health.InvalidTokenQuarantineActive == 0
            && health.MarketOrderbookQuarantineActive == 0
            && health.MarketOrderbookQuarantineLifecycleBalanced
            && health.InvalidTokenQuarantineLifecycleBalanced;
        var warmupMinutes = options?.RuntimeHealth.WarmupMinutes ?? 0;
        var warmupComplete = warmupMinutes <= 0 || health.Uptime >= TimeSpan.FromMinutes(warmupMinutes);
        var memoryStable = warmupComplete && trend.IsMemoryStable && (state?.MemoryCriticals ?? 0) == 0;
        var singleMarketScanned = health.StrategyCounters.TryGetValue("SingleMarketBuyBoth", out var singleCounter) && singleCounter.Scanned > 0;
        var reducedUniversePrerequisites = health.AllowReducedUniverseDiagnosticsOnly
            && health.ReducedUniverseExplicitFlagSatisfied
            && string.Equals(health.DiscoverySelectedSource, "ReducedUniverseDiagnosticsOnly", StringComparison.OrdinalIgnoreCase)
            && health.DiscoveryReducedUniverse
            && string.Equals(health.DiagnosticsUniverse, "Reduced", StringComparison.OrdinalIgnoreCase)
            && health.ReducedUniverseMarkets > 0
            && health.ReducedUniverseOrderbookEligibleMarkets > 0
            && !health.ScannerPausedByDiscoveryGuard
            && singleMarketScanned;
        var paperDiagnosticsLimitedEligibleNow = reducedUniversePrerequisites
            && orderbookStableNow
            && !health.OrderbookCircuitBreakerActive
            && trend.BatchBookBadRequestsDeltaLastHour == 0
            && trend.BatchBookInvalidTokensDeltaLastHour == 0
            && health.SingleMarketScanPausedByOrderbookHealth == 0
            && health.TruePostBreakerBadRequests == 0
            && health.PaperExecutionsCount == 0
            && health.DiagnosticsCounterMismatchCount == 0
            && memoryStable
            && logVolumeStable
            && health.LiveTradingBlockedCount == 0
            && health.SigningAttempts == 0;
        var paperDiagnosticsLimitedBlockedReasonNow = paperDiagnosticsLimitedEligibleNow ? "None" : string.Join("|", new[]
        {
            !health.AllowReducedUniverseDiagnosticsOnly || !health.ReducedUniverseExplicitFlagSatisfied ? "NotExplicitlyEnabled" : "",
            string.Equals(health.DiscoverySelectedSource, "ReducedUniverseDiagnosticsOnly", StringComparison.OrdinalIgnoreCase) && health.DiscoveryReducedUniverse && string.Equals(health.DiagnosticsUniverse, "Reduced", StringComparison.OrdinalIgnoreCase) ? "" : (health.DiscoverySelectedSource.Equals("Blocked", StringComparison.OrdinalIgnoreCase) || !health.DiscoveryScannerSafeSourceAvailable ? "NoScannerSafeDiscoverySource" : "ReducedUniverseNotActive"),
            health.ReducedUniverseMarkets > 0 && health.ReducedUniverseOrderbookEligibleMarkets > 0 ? "" : "ReducedUniverseEmpty",
            !health.ScannerPausedByDiscoveryGuard ? "" : "ScannerPausedByDiscoveryGuard",
            singleMarketScanned ? "" : "NoScannerSafeDiscoverySource",
            !health.OrderbookCircuitBreakerActive && string.Equals(health.OrderbookCircuitBreakerState, "Closed", StringComparison.OrdinalIgnoreCase) ? "" : "OrderbookCircuitBreakerNotClosed",
            health.ReducedUniverseOrderbookStable && !health.ReducedUniverseScanPausedByOrderbookHealth ? "" : "ReducedUniverseOrderbookUnstable",
            orderbookStableNow ? "" : "OrderbookStableNowFalse",
            trend.BatchBookBadRequestsDeltaLastHour == 0 ? "" : "BatchBookBadRequestsInWindow",
            trend.BatchBookInvalidTokensDeltaLastHour == 0 ? "" : "BatchBookInvalidTokensInWindow",
            health.SingleMarketScanPausedByOrderbookHealth == 0 ? "" : "ReducedUniverseOrderbookUnstable",
            health.TruePostBreakerBadRequests == 0 ? "" : "TruePostBreakerBadRequests",
            health.DiagnosticsCounterMismatchCount == 0 ? "" : "DiagnosticsCounterMismatch",
            memoryStable ? "" : "MemoryUnstable",
            logVolumeStable ? "" : "LogVolumeUnstable",
            health.LiveTradingBlockedCount == 0 ? "" : "LiveTradingSafety",
            health.SigningAttempts == 0 ? "" : "SigningAttemptDetected"
        }.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase));
        var verifiedPricingUnavailableGroups = health.StrategyCounters.TryGetValue("VerifiedMultiOutcome", out var verifiedCounter) ? verifiedCounter.VerifiedPricingBlockedByCircuitBreakerActive + verifiedCounter.VerifiedPricingBlockedByMarketOrderbookQuarantined + verifiedCounter.VerifiedPricingBlockedByTokenQuarantined + verifiedCounter.VerifiedPricingBlockedByOrderbookUnavailable : 0;
        var strategyStatus = string.Join(",", health.StrategyCounters.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => FormatStrategySoakStatus(x.Key, x.Value)));
        return $"[SOAK_STATUS] ProcessRunId={health.ProcessRunId} StartedAtUtc={health.StartedAtUtc:O} ScannerInstanceId={health.ScannerInstanceId} DiagnosticsCounterMismatchCount={health.DiagnosticsCounterMismatchCount} DiagnosticsCounterMismatchLastReason={health.DiagnosticsCounterMismatchLastReason} Uptime={health.Uptime} ProcessMb={health.ProcessMemoryMb} DeltaMb={trend.MemoryDeltaMbWindow:0.##} SlopeMbPerMin={trend.MemorySlopeMbPerMinute:0.##} WarmupMinutes={Math.Max(0, warmupMinutes)} WarmupComplete={warmupComplete.ToString().ToLowerInvariant()} Logs={health.RecentLogsCount} ExecutionAudit={health.ExecutionAuditCount} SignalR={health.SignalREventBufferCount} PaperOpened={health.PaperExecutionsCount} PaperOpenPositions={health.PaperOpenPositions} PaperInFlightOpens={health.PaperInFlightOpens} PaperDuplicateSuppressions={health.PaperDuplicateSuppressions} PaperStaleDedupeEntriesCleared={health.PaperStaleDedupeEntriesCleared} PaperClosed={health.PaperClosedPositions} PaperExposure={health.PaperTotalExposure:0.####} PaperRealizedPnl={health.PaperRealizedPnl:0.####} PaperLocked={health.PaperLocked:0.####} LiveTradingBlocked={health.LiveTradingBlockedCount} QuietSuppressed={health.QuietSuppressedTotal} BatchBookRequests={health.BatchBookRequests} BatchBookBadRequests={health.BatchBookBadRequests} BatchBookTimeouts={health.BatchBookTimeouts} BatchBookRetrySuccesses={health.BatchBookRetrySuccesses} BatchBookInvalidTokens={health.BatchBookInvalidTokens} BatchBookSuppressedErrors={health.BatchBookSuppressedErrors} BatchBookSplitRetriesAttempted={health.BatchBookSplitRetriesAttempted} BatchBookSplitRetrySucceeded={health.BatchBookSplitRetrySucceeded} BatchBookSplitRetryFailed={health.BatchBookSplitRetryFailed} BatchBookSingleTokenFailures={health.BatchBookSingleTokenFailures} BatchBookSingleTokenQuarantined={health.BatchBookSingleTokenQuarantined} BatchBookSkippedQuarantinedTokens={health.BatchBookSkippedQuarantinedTokens} BatchBookSkippedMarketsWithQuarantinedTokens={health.BatchBookSkippedMarketsWithQuarantinedTokens} BatchBookBadRequestRate={batchRate:0.####} BatchBookBadRequestsDeltaLastHour={trend.BatchBookBadRequestsDeltaLastHour} BatchBookInvalidTokensDeltaLastHour={trend.BatchBookInvalidTokensDeltaLastHour} QuarantinedTokens={state?.OrderBookServiceStats.QuarantinedTokens ?? 0} SkippedQuarantinedTokensLastHour={trend.SkippedQuarantinedTokensLastHour} OrderbookUnavailableMarkets={health.OrderbookUnavailableMarkets} InvalidTokenQuarantineActive={health.InvalidTokenQuarantineActive} InvalidTokenQuarantineAdded={health.InvalidTokenQuarantineAdded} InvalidTokenQuarantineExpired={health.InvalidTokenQuarantineExpired} BatchBookRequestsAvoidedByQuarantine={health.BatchBookRequestsAvoidedByQuarantine} MarketsSkippedByInvalidTokenQuarantine={health.MarketsSkippedByInvalidTokenQuarantine} MarketOrderbookQuarantineActive={health.MarketOrderbookQuarantineActive} MarketOrderbookQuarantineAdded={health.MarketOrderbookQuarantineAdded} MarketOrderbookQuarantineExpired={health.MarketOrderbookQuarantineExpired} MarketsSkippedByMarketOrderbookQuarantine={health.MarketsSkippedByMarketOrderbookQuarantine} BatchBookRequestsAvoidedByMarketQuarantine={health.BatchBookRequestsAvoidedByMarketQuarantine} OrderbookCircuitBreakerActive={health.OrderbookCircuitBreakerActive.ToString().ToLowerInvariant()} OrderbookCircuitBreakerState={health.OrderbookCircuitBreakerState} OrderbookCircuitBreakerOpenCount={health.OrderbookCircuitBreakerOpenCount} OrderbookCircuitBreakerCooldownRemainingSeconds={health.OrderbookCircuitBreakerCooldownRemainingSeconds} OrderbookCircuitBreakerHalfOpenAttempts={health.OrderbookCircuitBreakerHalfOpenAttempts} OrderbookCircuitBreakerHalfOpenSucceeded={health.OrderbookCircuitBreakerHalfOpenSucceeded} OrderbookCircuitBreakerHalfOpenFailed={health.OrderbookCircuitBreakerHalfOpenFailed} OrderbookCircuitBreakerHalfOpenStartedUtc={(health.OrderbookCircuitBreakerHalfOpenStartedUtc.HasValue ? health.OrderbookCircuitBreakerHalfOpenStartedUtc.Value.ToString("O") : "")} OrderbookCircuitBreakerHalfOpenAgeSeconds={health.OrderbookCircuitBreakerHalfOpenAgeSeconds} OrderbookCircuitBreakerHalfOpenMaxSeconds={health.OrderbookCircuitBreakerHalfOpenMaxSeconds} OrderbookCircuitBreakerHalfOpenTimedOutCount={health.OrderbookCircuitBreakerHalfOpenTimedOutCount} BatchBookCanaryTimeouts={health.BatchBookCanaryTimeouts} BatchBookCanaryInvalidTokens={health.BatchBookCanaryInvalidTokens} BatchBookCanaryOrderbookUnavailable={health.BatchBookCanaryOrderbookUnavailable} OrderbookCircuitBreakerLastHalfOpenFailureReason={health.OrderbookCircuitBreakerLastHalfOpenFailureReason} OrderbookCircuitBreakerCooldownExtensions={health.OrderbookCircuitBreakerCooldownExtensions} OrderbookCircuitBreakerLastOpenReason={health.OrderbookCircuitBreakerLastOpenReason} OrderbookCircuitBreakerRecoveringSinceUtc={(health.OrderbookCircuitBreakerRecoveringSinceUtc.HasValue ? health.OrderbookCircuitBreakerRecoveringSinceUtc.Value.ToString("O") : "")} OrderbookCircuitBreakerRecoveryRemainingSeconds={health.OrderbookCircuitBreakerRecoveryRemainingSeconds} OrderbookCircuitBreakerReopenedAfterClose={health.OrderbookCircuitBreakerReopenedAfterClose} OrderbookRequestsBlockedByCircuitBreaker={health.OrderbookRequestsBlockedByCircuitBreaker} OrderbookPostCloseBadRequests={health.OrderbookPostCloseBadRequests} OrderbookPostCloseInvalidTokens={health.OrderbookPostCloseInvalidTokens} SingleTokenIsolationBudgetExhausted={health.SingleTokenIsolationBudgetExhausted} BatchBookBadRequestsPreventedEstimate={health.BatchBookBadRequestsPreventedEstimate} BatchBookCanaryRequests={health.BatchBookCanaryRequests} BatchBookCanaryBadRequests={health.BatchBookCanaryBadRequests} BatchBookRecoveryRequests={health.BatchBookRecoveryRequests} BatchBookRecoveryBadRequests={health.BatchBookRecoveryBadRequests} OrderbookRecoveryLimitedRequests={health.OrderbookRecoveryLimitedRequests} OrderbookRecoveryLimitedMarkets={health.OrderbookRecoveryLimitedMarkets} OrderbookRecoveryBadRequests={health.OrderbookRecoveryBadRequests} OrderbookRecoveryInvalidTokens={health.OrderbookRecoveryInvalidTokens} OrderbookRecoverySucceededCount={health.OrderbookRecoverySucceededCount} OrderbookRecoveryFailedCount={health.OrderbookRecoveryFailedCount} BatchBookNormalRequests={health.BatchBookNormalRequests} BatchBookNormalBadRequests={health.BatchBookNormalBadRequests} BatchBookNormalRequestsBeforeBreakerOpen={health.BatchBookNormalRequestsBeforeBreakerOpen} BatchBookNormalBadRequestsBeforeBreakerOpen={health.BatchBookNormalBadRequestsBeforeBreakerOpen} BatchBookNormalRequestsAfterBreakerOpen={health.BatchBookNormalRequestsAfterBreakerOpen} BatchBookNormalBadRequestsAfterBreakerOpen={health.BatchBookNormalBadRequestsAfterBreakerOpen} ReducedUniverseOrderbookStable={health.ReducedUniverseOrderbookStable.ToString().ToLowerInvariant()} ReducedUniverseOrderbookEligibleMarkets={health.ReducedUniverseOrderbookEligibleMarkets} ReducedUniverseScanPausedByOrderbookHealth={health.ReducedUniverseScanPausedByOrderbookHealth.ToString().ToLowerInvariant()} ReducedUniverseOrderbookRecoveryMode={health.ReducedUniverseOrderbookRecoveryMode.ToString().ToLowerInvariant()} ReducedUniverseOrderbookRecoveryCleanWindowSeconds={health.ReducedUniverseOrderbookRecoveryCleanWindowSeconds} ReducedUniversePostRecoveryBadRequests={health.ReducedUniversePostRecoveryBadRequests} MarketOrderbookQuarantineLifecycleBalanced={health.MarketOrderbookQuarantineLifecycleBalanced.ToString().ToLowerInvariant()} InvalidTokenQuarantineLifecycleBalanced={health.InvalidTokenQuarantineLifecycleBalanced.ToString().ToLowerInvariant()} OrderbookQuarantineLifecycleMismatchReason={health.OrderbookQuarantineLifecycleMismatchReason} InFlightBeforeBreakerCompletedAfterOpen={health.InFlightBeforeBreakerCompletedAfterOpen} InFlightBeforeBreakerBadRequestsAfterOpen={health.InFlightBeforeBreakerBadRequestsAfterOpen} TruePostBreakerNormalRequests={health.TruePostBreakerNormalRequests} TruePostBreakerBadRequests={health.TruePostBreakerBadRequests} OrderbookStableNow={orderbookStableNow.ToString().ToLowerInvariant()} OrderbookStableWindowMinutes={health.OrderbookStableWindowMinutes} BatchBookBadRequestsDeltaWindow={trend.BatchBookBadRequestsDeltaLastHour} BatchBookInvalidTokensDeltaWindow={trend.BatchBookInvalidTokensDeltaLastHour} PostBreakerBadRequestsDeltaWindow={trend.PostBreakerBadRequestsDeltaWindow} ReducedUniverseOrderbookStableNow={orderbookStableNow.ToString().ToLowerInvariant()} ReducedUniverseBadHistoryActive={health.ReducedUniverseBadHistoryActive} ReducedUniverseBadHistoryLoaded={health.ReducedUniverseBadHistoryLoaded.ToString().ToLowerInvariant()} ReducedUniverseBadHistoryExpired={health.ReducedUniverseBadHistoryExpired} SingleMarketScanPausedByOrderbookHealth={health.SingleMarketScanPausedByOrderbookHealth} SingleMarketPausedCycles={health.SingleMarketPausedCycles} SingleMarketNormalCycles={health.SingleMarketNormalCycles} SingleMarketFullCyclesCompleted={health.SingleMarketFullCyclesCompleted} PaperDiagnosticsLimitedEnabled={health.PaperDiagnosticsLimitedEnabled.ToString().ToLowerInvariant()} PaperDiagnosticsLimitedEligible={paperDiagnosticsLimitedEligibleNow.ToString().ToLowerInvariant()} PaperDiagnosticsLimitedBlockedReason={paperDiagnosticsLimitedBlockedReasonNow} PaperDiagnosticsLimitedAllowedStrategy={health.PaperDiagnosticsLimitedAllowedStrategy} PaperDiagnosticsLimitedMaxOpenPositions={health.PaperDiagnosticsLimitedMaxOpenPositions} PaperDiagnosticsLimitedMaxPaperNotionalPerTrade={health.PaperDiagnosticsLimitedMaxPaperNotionalPerTrade:0.####} PaperDiagnosticsLimitedMaxPaperTotalExposure={health.PaperDiagnosticsLimitedMaxPaperTotalExposure:0.####} PaperDiagnosticsLimitedOpensLastHour={health.PaperDiagnosticsLimitedOpensLastHour} PaperDiagnosticsLimitedGateLastRejectReason={health.PaperDiagnosticsLimitedGateLastRejectReason} PaperDiagnosticsLimitedPaperOpened={health.PaperDiagnosticsLimitedPaperOpened} QuarantinedMarketsReintroducedBlocked={health.QuarantinedMarketsReintroducedBlocked} QuarantinedTokensReintroducedBlocked={health.QuarantinedTokensReintroducedBlocked} AllowlistHealthy={health.AllowlistHealthy} AllowlistMonitoringOnly={health.AllowlistMonitoringOnly} AllowlistNeedsPricingPrune={health.AllowlistNeedsPricingPrune} AllowlistNeedsRefresh={health.AllowlistNeedsRefresh} AllowlistReviewOnly={health.AllowlistReviewOnly} AllowlistMismatch={health.AllowlistMismatch} AllowlistBrokenConfig={health.AllowlistBrokenConfig} AllowlistDisabled={health.AllowlistDisabled} AllowlistIgnored={health.AllowlistIgnored} AllowlistClassificationTotal={health.AllowlistClassificationTotal} AllowlistClassificationValid={health.AllowlistClassificationValid.ToString().ToLowerInvariant()} AllowlistRefreshPreviewCandidates={health.AllowlistRefreshPreviewCandidates} AllowlistRefreshHighConfidence={health.AllowlistRefreshHighConfidence} AllowlistRefreshFinalNoCandidate={health.AllowlistRefreshFinalNoCandidate} AllowlistRefreshFinalSemanticConflict={health.AllowlistRefreshFinalSemanticConflict} AllowlistRefreshFinalLowConfidence={health.AllowlistRefreshFinalLowConfidence} AllowlistRefreshFinalUnstable={health.AllowlistRefreshFinalUnstable} AllowlistRefreshFinalPreviewOnly={health.AllowlistRefreshFinalPreviewOnly} AllowlistRefreshFinalLockedManualReview={health.AllowlistRefreshFinalLockedManualReview} AllowlistRefreshActionExplainedSuppressed={health.AllowlistRefreshActionExplainedSuppressed} AllowlistRefreshUnstableGroups={health.AllowlistRefreshUnstableGroups} AllowlistRefreshActionFlipFlops={health.AllowlistRefreshActionFlipFlops} AllowlistRefreshAutoApply={health.AllowlistRefreshAutoApply.ToString().ToLowerInvariant()} DiscoveryHealthy={health.DiscoveryHealthy.ToString().ToLowerInvariant()} DiscoveryUsingLastHealthySnapshot={health.DiscoveryUsingLastHealthySnapshot.ToString().ToLowerInvariant()} DiscoveryLastFailureReason={health.DiscoveryLastFailureReason} DiscoveryLastHealthySnapshotAgeSeconds={health.DiscoveryLastHealthySnapshotAgeSeconds} ScannerPausedByDiscoveryGuard={health.ScannerPausedByDiscoveryGuard.ToString().ToLowerInvariant()} DiscoveryGuardSkippedCycles={health.DiscoveryGuardSkippedCycles} DiscoveryGuardUsingLastHealthySnapshot={health.DiscoveryGuardUsingLastHealthySnapshot.ToString().ToLowerInvariant()} DiscoveryGuardBlockedNewMarkets={health.DiscoveryGuardBlockedNewMarkets} LongRunStable={health.LongRunStable.ToString().ToLowerInvariant()} LongRunBlockingReason={health.LongRunBlockingReason} DiscoveryStable={health.DiscoveryStable.ToString().ToLowerInvariant()} OrderbookRecoveredAfterDegradation={health.OrderbookRecoveredAfterDegradation.ToString().ToLowerInvariant()} LastDegradationUtc={(health.LastDegradationUtc.HasValue ? health.LastDegradationUtc.Value.ToString("O") : "")} LastRecoveryUtc={(health.LastRecoveryUtc.HasValue ? health.LastRecoveryUtc.Value.ToString("O") : "")} DiscoveryBootstrapHealthy={health.DiscoveryBootstrapHealthy.ToString().ToLowerInvariant()} DiscoveryBootstrapRetryCount={health.DiscoveryBootstrapRetryCount} DiscoveryBootstrapLastAttemptUtc={(health.DiscoveryBootstrapLastAttemptUtc.HasValue ? health.DiscoveryBootstrapLastAttemptUtc.Value.ToString("O") : "")} DiscoveryBootstrapNextRetryUtc={(health.DiscoveryBootstrapNextRetryUtc.HasValue ? health.DiscoveryBootstrapNextRetryUtc.Value.ToString("O") : "")} DiscoveryBootstrapBackoffSeconds={health.DiscoveryBootstrapBackoffSeconds} DiscoveryBootstrapFailureReason={health.DiscoveryBootstrapFailureReason} DiscoveryRetryBackoffSeconds={health.DiscoveryRetryBackoffSeconds} DiscoveryRetriesSuppressedByBackoff={health.DiscoveryRetriesSuppressedByBackoff} DiscoveryPersistedSnapshotLoaded={health.DiscoveryPersistedSnapshotLoaded.ToString().ToLowerInvariant()} DiscoveryPersistedSnapshotAgeSeconds={health.DiscoveryPersistedSnapshotAgeSeconds} DiscoveryPersistedSnapshotActiveMarkets={health.DiscoveryPersistedSnapshotActiveMarkets} AllowlistEvaluationSkipped={health.AllowlistEvaluationSkipped.ToString().ToLowerInvariant()} AllowlistEvaluationSkippedReason={health.AllowlistEvaluationSkippedReason} AllowlistClassificationBlockedByDiscovery={health.AllowlistClassificationBlockedByDiscovery.ToString().ToLowerInvariant()} DiscoveryBlockedReason={health.DiscoveryBlockedReason} DiscoverySelectedSource={health.DiscoverySelectedSource} AllowReducedUniverseDiagnosticsOnly={health.AllowReducedUniverseDiagnosticsOnly.ToString().ToLowerInvariant()} ReducedUniverseRequireExplicitFlag={health.ReducedUniverseRequireExplicitFlag.ToString().ToLowerInvariant()} ReducedUniverseExplicitFlagSatisfied={health.ReducedUniverseExplicitFlagSatisfied.ToString().ToLowerInvariant()} ReducedUniverseActivationBlockedReason={health.ReducedUniverseActivationBlockedReason} DiscoveryScannerSafeSourceAvailable={health.DiscoveryScannerSafeSourceAvailable.ToString().ToLowerInvariant()} DiscoverySourceAuditOnly={health.DiscoverySourceAuditOnly.ToString().ToLowerInvariant()} DiscoverySourceAuditExportWritten={health.DiscoverySourceAuditExportWritten.ToString().ToLowerInvariant()} DiscoverySourceAuditExportPath={health.DiscoverySourceAuditExportPath} DiscoverySourceAuditSources={health.DiscoverySourceAuditSources} DiscoverySourceAuditScannerSafeSources={health.DiscoverySourceAuditScannerSafeSources} DiscoverySourceAuditRecommendedAction={health.DiscoverySourceAuditRecommendedAction} SoakReadiness={health.SoakReadiness} SoakReadinessReason={health.SoakReadinessReason} VerifiedPricingUnavailableGroups={verifiedPricingUnavailableGroups} InvalidTokenQuarantine={state?.OrderBookServiceStats.QuarantinedTokens ?? 0} OrderbookCache={health.OrderbookCacheCount} QuietLogGateCache={health.LogGateCacheSize} MemoryWarnings={state?.MemoryWarnings ?? 0} MemoryCriticals={state?.MemoryCriticals ?? 0} ScannerPausedByMemoryGuard={(state?.ScannerPausedByMemoryGuard ?? false).ToString().ToLowerInvariant()} MemoryStable={memoryStable.ToString().ToLowerInvariant()} LogVolumeStable={logVolumeStable.ToString().ToLowerInvariant()} OrderbookStable={orderbookStable.ToString().ToLowerInvariant()} Strategies={{{strategyStatus}}}";
    }

    private static string FormatStrategySoakStatus(string key, TradingBot.Services.StrategyRuntimeCounterSnapshot value)
    {
        var standard = $"{key}:{value.Mode}:scan={value.Scanned}:books={value.Books}:cand={value.Candidates}:positive={value.PositiveEdges}:ready={value.ExecutionReady}:shadow={value.ShadowWouldOpen}:paper={value.PaperOpened}:edgeStable={value.EdgeStable}:blockedByMode={value.BlockedByMode}:blockedByPaperDiagnosticsLimitedGate={value.BlockedByPaperDiagnosticsLimitedGate}:blockedByOrderbookHealth={value.BlockedByOrderbookHealth}:blockedByRisk={value.BlockedByRisk}:blockedByFill={value.BlockedByFill}:blockedByDepth={value.BlockedByDepth}:bestAfterSafetyEdge={value.BestEdge?.ToString("0.####") ?? "N/A"}:bestRejectedReason={value.TopSkipReason}:validPriced={value.ValidPriced}:invalidOrUnpriced={value.InvalidOrUnpriced}:dataQualityRejected={value.DataQualityRejected}:unverified={value.Unverified}:reviewOnly={value.ReviewOnly}:missingPricing={value.MissingPricing}:bestCandidateValid={value.BestCandidateValid.ToString().ToLowerInvariant()}:bestCandidatePriced={value.BestCandidatePriced.ToString().ToLowerInvariant()}:bestCandidateExecutableLike={value.BestCandidateExecutableLike.ToString().ToLowerInvariant()}:bestCandidateReason={value.BestCandidateReason}:blocked={value.DiagnosticsOnlyBlocked}:faults={value.Faults}";
        if (!key.Equals("VerifiedMultiOutcome", StringComparison.OrdinalIgnoreCase))
            return $"{standard}:singleMarketCircuitBreakerSkippedMarkets={value.SingleMarketCircuitBreakerSkippedMarkets}:singleMarketCircuitBreakerSkippedCycles={value.SingleMarketCircuitBreakerSkippedCycles}";
        return $"{standard}:pricingMissingNoAsk={value.VerifiedPricingBlockedByMissingNoAsk}:pricingCircuitBreakerActive={value.VerifiedPricingBlockedByCircuitBreakerActive}:pricingMarketOrderbookQuarantined={value.VerifiedPricingBlockedByMarketOrderbookQuarantined}:pricingTokenQuarantined={value.VerifiedPricingBlockedByTokenQuarantined}:pricingEmptyBook={value.VerifiedPricingBlockedByEmptyBook}:pricingOrderbookUnavailable={value.VerifiedPricingBlockedByOrderbookUnavailable}:pricingQuarantinedToken={value.VerifiedPricingBlockedByQuarantinedToken}:activePositive={value.VerifiedActiveConservativePositive}:rawPositiveOnly={value.VerifiedRawPositiveOnly}:alternatePositive={value.VerifiedAlternateProfilePositive}:experimentalCandidates={value.VerifiedExperimentalProfileCandidate}:wouldOpenIfPaperEligible={value.VerifiedWouldOpenIfPaperEligible}:wouldOpenBlockedByStability={value.VerifiedRejectedByStability}:wouldOpenBlockedByRisk={value.VerifiedRejectedByRisk}:wouldOpenBlockedByFill={value.VerifiedWouldOpenBlockedByFill}:wouldOpenBlockedByDepth={value.VerifiedWouldOpenBlockedByDepth}:diagBlocked={value.VerifiedDiagnosticsOnlyBlocked}";
    }

    public static bool IsLogVolumeStable(RuntimeHealthSnapshot health, TradingBot.Options.TradingBotOptions options)
        => health.RecentLogsCount <= options.RuntimeState.MaxRecentLogs
            && health.ExecutionAuditCount <= options.RuntimeState.MaxExecutionAuditEvents
            && health.SignalREventBufferCount <= options.RuntimeState.MaxSignalREventBuffer;

    private static void Trim(DateTime nowUtc, int windowMinutes)
    {
        var cutoff = nowUtc - TimeSpan.FromMinutes(Math.Max(1, windowMinutes));
        Samples.RemoveAll(x => x.TimestampUtc < cutoff);
    }

    private static RuntimeHealthTrend AnalyzeNoLock(TradingBot.Options.RuntimeHealthOptions options, RuntimeHealthSnapshot? currentSnapshot = null)
    {
        if (Samples.Count == 0) return new RuntimeHealthTrend(0, 0, 0, 0, true, 0);
        var min = Samples.Min(x => x.ProcessMb);
        var max = Samples.Max(x => x.ProcessMb);
        var delta = max - min;
        var first = Samples[0];
        var last = Samples[^1];
        var oneHourCutoff = last.TimestampUtc - TimeSpan.FromHours(1);
        var hourBase = Samples.FirstOrDefault(x => x.TimestampUtc >= oneHourCutoff);
        if (hourBase.TimestampUtc == default) hourBase = first;
        var useStartupCounts = currentSnapshot is not null && currentSnapshot.Uptime < TimeSpan.FromHours(1);
        var badDeltaLastHour = useStartupCounts ? currentSnapshot!.BatchBookBadRequests : Math.Max(0, last.BatchBadRequests - hourBase.BatchBadRequests);
        var invalidDeltaLastHour = useStartupCounts ? currentSnapshot!.BatchBookInvalidTokens : Math.Max(0, last.BatchInvalidTokens - hourBase.BatchInvalidTokens);
        var skippedDeltaLastHour = useStartupCounts ? currentSnapshot!.BatchBookSkippedQuarantinedTokens : Math.Max(0, last.SkippedQuarantinedTokens - hourBase.SkippedQuarantinedTokens);
        var postBreakerDeltaWindow = useStartupCounts ? currentSnapshot!.TruePostBreakerBadRequests : Math.Max(0, last.TruePostBreakerBadRequests - hourBase.TruePostBreakerBadRequests);
        var minutes = Math.Max(0.001, (last.TimestampUtc - first.TimestampUtc).TotalMinutes);
        var slope = (last.ProcessMb - first.ProcessMb) / minutes;
        var stable = Math.Abs(slope) <= Math.Max(0, options.StableMemorySlopeMbPerMinute)
            && delta <= Math.Max(0, options.StableMemoryMaxDeltaMb);
        return new RuntimeHealthTrend(Math.Round(min, 2), Math.Round(max, 2), Math.Round(delta, 2), Math.Round(slope, 2), stable, Samples.Count, badDeltaLastHour, invalidDeltaLastHour, skippedDeltaLastHour, postBreakerDeltaWindow);
    }
}
