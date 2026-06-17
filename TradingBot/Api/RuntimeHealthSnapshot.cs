using System.Diagnostics;

namespace TradingBot.Api;

public sealed record RuntimeHealthSnapshot(
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
    long OrderbookCircuitBreakerCooldownExtensions,
    string OrderbookCircuitBreakerLastOpenReason,
    long SingleTokenIsolationBudgetExhausted,
    long BatchBookBadRequestsPreventedEstimate,
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
        return $"[RUNTIME_HEALTH] ProcessMb={ProcessMemoryMb} GcMb={GcTotalMemoryMb} WorkingSetMb={WorkingSetMb} Logs={RecentLogsCount} ScannerHistory={ScannerHistoryCount} CandidateSnapshots={CandidateSnapshotCount} RepairHistory={RepairHistoryCount} ExecutionAudit={ExecutionAuditCount} SignalRBuffer={SignalREventBufferCount} OrderbookCache={OrderbookCacheCount} MarketCache={MarketCacheCount} PatchPreviewItems={PatchPreviewItemsCount} SingleMarketOpportunities={SingleMarketOpportunitiesCount} SingleMarketNearMisses={SingleMarketNearMissesCount} SingleMarketDataQualitySamples={SingleMarketDataQualitySamplesCount} SingleMarketExecutions={SingleMarketExecutionsCount} PaperPhase={PaperPhase} PaperOpenPositions={PaperOpenPositions} PaperClosedPositions={PaperClosedPositions} PaperSettlements={PaperSettlements} PaperRealizedPnl={PaperRealizedPnl} PaperLocked={PaperLocked} PaperCash={PaperCash} PaperEquity={PaperEquity} PaperTotalExposure={PaperTotalExposure} PaperOpenCountLastHour={PaperOpenCountLastHour} PaperPretradeRejects={PaperPretradeRejects} PaperDuplicateSuppressions={PaperDuplicateSuppressions} PaperOpenPositionKeys={string.Join("|", PaperOpenPositionKeys)} PaperOpenMarketIdsCount={PaperOpenMarketIdsCount} PaperOpenMarketIdsSample={string.Join("|", PaperOpenMarketIdsSample)} PaperInFlightOpens={PaperInFlightOpens} PaperDuplicateDedupeEntries={PaperDuplicateDedupeEntries} PaperStaleDedupeEntriesCleared={PaperStaleDedupeEntriesCleared} PaperSettlementRejects={PaperSettlementRejects} PaperDuplicateSettlementSuppressions={PaperDuplicateSettlementSuppressions} PaperLifecycleEvents={PaperLifecycleEvents} PaperOpenEvents={PaperOpenEvents} PaperCloseEvents={PaperCloseEvents} LiveTradingBlocked={LiveTradingBlockedCount} PaperExecutions={PaperExecutionsCount} SigningAttempts={SigningAttempts} DuplicateGroupKeyWarnings={DuplicateGroupKeyWarnings} QuietSuppressed={QuietSuppressedTotal} EmittedLogs={EmittedLogs} LogGateCache={LogGateCacheSize} QuietSuppressedByCategory={{{suppressed}}} EmittedByCategory={{{emitted}}} BatchBookRequests={BatchBookRequests} BatchBookBadRequests={BatchBookBadRequests} BatchBookTimeouts={BatchBookTimeouts} BatchBookRetrySuccesses={BatchBookRetrySuccesses} BatchBookInvalidTokens={BatchBookInvalidTokens} BatchBookSuppressedErrors={BatchBookSuppressedErrors} BatchBookSplitRetriesAttempted={BatchBookSplitRetriesAttempted} BatchBookSplitRetrySucceeded={BatchBookSplitRetrySucceeded} BatchBookSplitRetryFailed={BatchBookSplitRetryFailed} BatchBookSingleTokenFailures={BatchBookSingleTokenFailures} BatchBookSingleTokenQuarantined={BatchBookSingleTokenQuarantined} BatchBookSkippedQuarantinedTokens={BatchBookSkippedQuarantinedTokens} BatchBookSkippedMarketsWithQuarantinedTokens={BatchBookSkippedMarketsWithQuarantinedTokens} OrderbookUnavailableMarkets={OrderbookUnavailableMarkets} InvalidTokenQuarantineActive={InvalidTokenQuarantineActive} InvalidTokenQuarantineAdded={InvalidTokenQuarantineAdded} InvalidTokenQuarantineExpired={InvalidTokenQuarantineExpired} BatchBookRequestsAvoidedByQuarantine={BatchBookRequestsAvoidedByQuarantine} MarketsSkippedByInvalidTokenQuarantine={MarketsSkippedByInvalidTokenQuarantine} MarketOrderbookQuarantineActive={MarketOrderbookQuarantineActive} MarketOrderbookQuarantineAdded={MarketOrderbookQuarantineAdded} MarketOrderbookQuarantineExpired={MarketOrderbookQuarantineExpired} MarketsSkippedByMarketOrderbookQuarantine={MarketsSkippedByMarketOrderbookQuarantine} BatchBookRequestsAvoidedByMarketQuarantine={BatchBookRequestsAvoidedByMarketQuarantine} OrderbookCircuitBreakerActive={OrderbookCircuitBreakerActive.ToString().ToLowerInvariant()} OrderbookCircuitBreakerState={OrderbookCircuitBreakerState} OrderbookCircuitBreakerOpenCount={OrderbookCircuitBreakerOpenCount} OrderbookCircuitBreakerCooldownRemainingSeconds={OrderbookCircuitBreakerCooldownRemainingSeconds} OrderbookCircuitBreakerHalfOpenAttempts={OrderbookCircuitBreakerHalfOpenAttempts} OrderbookCircuitBreakerHalfOpenSucceeded={OrderbookCircuitBreakerHalfOpenSucceeded} OrderbookCircuitBreakerHalfOpenFailed={OrderbookCircuitBreakerHalfOpenFailed} OrderbookCircuitBreakerCooldownExtensions={OrderbookCircuitBreakerCooldownExtensions} OrderbookCircuitBreakerLastOpenReason={OrderbookCircuitBreakerLastOpenReason} SingleTokenIsolationBudgetExhausted={SingleTokenIsolationBudgetExhausted} BatchBookBadRequestsPreventedEstimate={BatchBookBadRequestsPreventedEstimate} AllowlistHealthy={AllowlistHealthy} AllowlistMonitoringOnly={AllowlistMonitoringOnly} AllowlistNeedsPricingPrune={AllowlistNeedsPricingPrune} AllowlistNeedsRefresh={AllowlistNeedsRefresh} AllowlistReviewOnly={AllowlistReviewOnly} AllowlistMismatch={AllowlistMismatch} AllowlistBrokenConfig={AllowlistBrokenConfig} AllowlistDisabled={AllowlistDisabled} AllowlistIgnored={AllowlistIgnored} AllowlistClassificationTotal={AllowlistClassificationTotal} AllowlistClassificationValid={AllowlistClassificationValid.ToString().ToLowerInvariant()} AllowlistRefreshPreviewCandidates={AllowlistRefreshPreviewCandidates} AllowlistRefreshHighConfidence={AllowlistRefreshHighConfidence} AllowlistRefreshFinalNoCandidate={AllowlistRefreshFinalNoCandidate} AllowlistRefreshFinalSemanticConflict={AllowlistRefreshFinalSemanticConflict} AllowlistRefreshFinalLowConfidence={AllowlistRefreshFinalLowConfidence} AllowlistRefreshFinalUnstable={AllowlistRefreshFinalUnstable} AllowlistRefreshFinalPreviewOnly={AllowlistRefreshFinalPreviewOnly} AllowlistRefreshFinalLockedManualReview={AllowlistRefreshFinalLockedManualReview} AllowlistRefreshActionExplainedSuppressed={AllowlistRefreshActionExplainedSuppressed} AllowlistRefreshUnstableGroups={AllowlistRefreshUnstableGroups} AllowlistRefreshActionFlipFlops={AllowlistRefreshActionFlipFlops} AllowlistRefreshAutoApply={AllowlistRefreshAutoApply.ToString().ToLowerInvariant()} Strategies={{{strategies}}} StrategyScanCounts={{{strategyScanCounts}}} StrategyCandidates={{{strategyCandidates}}} StrategyPositiveEdges={{{strategyPositiveEdges}}} StrategyExecutionReady={{{strategyExecutionReady}}} StrategyPaperOpened={{{strategyPaperOpened}}} StrategyRejectedByReason={{{strategyRejectedByReason}}} Uptime={Uptime}";
    }


    private static string FormatStrategyRuntimeHealth(string key, TradingBot.Services.StrategyRuntimeCounterSnapshot value)
    {
        var baseText = $"{key}:{value.Mode}:scan={value.Scanned}:books={value.Books}:cand={value.Candidates}:positive={value.PositiveEdges}:ready={value.ExecutionReady}:execCand={value.ExecutionCandidates}:paper={value.PaperOpened}:diagBlocked={value.DiagnosticsOnlyBlocked}:faults={value.Faults}";
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
        var p = Process.GetCurrentProcess();
        var startedUtc = p.StartTime.Kind == DateTimeKind.Utc ? p.StartTime : p.StartTime.ToUniversalTime();
        return new RuntimeHealthSnapshot(
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
            OrderbookCircuitBreakerCooldownExtensions: state.OrderBookServiceStats.OrderbookCircuitBreakerCooldownExtensions,
            OrderbookCircuitBreakerLastOpenReason: state.OrderBookServiceStats.OrderbookCircuitBreakerLastOpenReason,
            SingleTokenIsolationBudgetExhausted: state.OrderBookServiceStats.SingleTokenIsolationBudgetExhausted,
            BatchBookBadRequestsPreventedEstimate: state.OrderBookServiceStats.BatchBookBadRequestsPreventedEstimate,
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
            StrategyCounters: state.StrategyCountersSnapshot());
    }
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
    long SkippedQuarantinedTokensLastHour = 0);

public static class RuntimeHealthTrendTracker
{
    private static readonly object Gate = new();
    private static readonly List<(DateTime TimestampUtc, double ProcessMb, long BatchBadRequests, long BatchInvalidTokens, long SkippedQuarantinedTokens)> Samples = new();

    public static RuntimeHealthTrend RecordAndAnalyze(RuntimeHealthSnapshot snapshot, TradingBot.Options.RuntimeHealthOptions options)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            Samples.Add((now, snapshot.ProcessMemoryMb, snapshot.BatchBookBadRequests, snapshot.BatchBookInvalidTokens, snapshot.BatchBookSkippedQuarantinedTokens));
            Trim(now, options.SoakTrendWindowMinutes);
            return AnalyzeNoLock(options, snapshot);
        }
    }

    public static RuntimeHealthTrend Analyze(IEnumerable<(DateTime TimestampUtc, double ProcessMb)> samples, TradingBot.Options.RuntimeHealthOptions options)
    {
        lock (Gate)
        {
            Samples.Clear();
            Samples.AddRange(samples.OrderBy(x => x.TimestampUtc).Select(x => (x.TimestampUtc, x.ProcessMb, 0L, 0L, 0L)));
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
            && batchRate <= options.Soak.MaxBatchBookBadRequestRate
            && trend.BatchBookBadRequestsDeltaLastHour <= options.Soak.MaxBatchBookBadRequestsPerHour
            && trend.BatchBookInvalidTokensDeltaLastHour <= options.Soak.MaxBatchBookInvalidTokensPerHour
            && health.BatchBookRepeatedInvalidTokenAfterQuarantine <= options.OrderBook.MaxRepeatedInvalidTokenAfterQuarantine
            && health.BatchBookSplitRetryFailed <= options.OrderBook.MaxBatchSplitRetriesPerCycle
            && health.OrderbookUnavailableMarkets <= options.Soak.MaxOrderbookUnavailableMarkets);
        var warmupMinutes = options?.RuntimeHealth.WarmupMinutes ?? 0;
        var warmupComplete = warmupMinutes <= 0 || health.Uptime >= TimeSpan.FromMinutes(warmupMinutes);
        var memoryStable = warmupComplete && trend.IsMemoryStable && (state?.MemoryCriticals ?? 0) == 0;
        var verifiedPricingUnavailableGroups = health.StrategyCounters.TryGetValue("VerifiedMultiOutcome", out var verifiedCounter) ? verifiedCounter.VerifiedPricingBlockedByCircuitBreakerActive + verifiedCounter.VerifiedPricingBlockedByMarketOrderbookQuarantined + verifiedCounter.VerifiedPricingBlockedByTokenQuarantined + verifiedCounter.VerifiedPricingBlockedByOrderbookUnavailable : 0;
        var strategyStatus = string.Join(",", health.StrategyCounters.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => FormatStrategySoakStatus(x.Key, x.Value)));
        return $"[SOAK_STATUS] Uptime={health.Uptime} ProcessMb={health.ProcessMemoryMb} DeltaMb={trend.MemoryDeltaMbWindow:0.##} SlopeMbPerMin={trend.MemorySlopeMbPerMinute:0.##} WarmupMinutes={Math.Max(0, warmupMinutes)} WarmupComplete={warmupComplete.ToString().ToLowerInvariant()} Logs={health.RecentLogsCount} ExecutionAudit={health.ExecutionAuditCount} SignalR={health.SignalREventBufferCount} PaperOpened={health.PaperExecutionsCount} PaperOpenPositions={health.PaperOpenPositions} PaperInFlightOpens={health.PaperInFlightOpens} PaperDuplicateSuppressions={health.PaperDuplicateSuppressions} PaperStaleDedupeEntriesCleared={health.PaperStaleDedupeEntriesCleared} PaperClosed={health.PaperClosedPositions} PaperExposure={health.PaperTotalExposure:0.####} PaperRealizedPnl={health.PaperRealizedPnl:0.####} PaperLocked={health.PaperLocked:0.####} LiveTradingBlocked={health.LiveTradingBlockedCount} QuietSuppressed={health.QuietSuppressedTotal} BatchBookRequests={health.BatchBookRequests} BatchBookBadRequests={health.BatchBookBadRequests} BatchBookTimeouts={health.BatchBookTimeouts} BatchBookRetrySuccesses={health.BatchBookRetrySuccesses} BatchBookInvalidTokens={health.BatchBookInvalidTokens} BatchBookSuppressedErrors={health.BatchBookSuppressedErrors} BatchBookSplitRetriesAttempted={health.BatchBookSplitRetriesAttempted} BatchBookSplitRetrySucceeded={health.BatchBookSplitRetrySucceeded} BatchBookSplitRetryFailed={health.BatchBookSplitRetryFailed} BatchBookSingleTokenFailures={health.BatchBookSingleTokenFailures} BatchBookSingleTokenQuarantined={health.BatchBookSingleTokenQuarantined} BatchBookSkippedQuarantinedTokens={health.BatchBookSkippedQuarantinedTokens} BatchBookSkippedMarketsWithQuarantinedTokens={health.BatchBookSkippedMarketsWithQuarantinedTokens} BatchBookBadRequestRate={batchRate:0.####} BatchBookBadRequestsDeltaLastHour={trend.BatchBookBadRequestsDeltaLastHour} BatchBookInvalidTokensDeltaLastHour={trend.BatchBookInvalidTokensDeltaLastHour} QuarantinedTokens={state?.OrderBookServiceStats.QuarantinedTokens ?? 0} SkippedQuarantinedTokensLastHour={trend.SkippedQuarantinedTokensLastHour} OrderbookUnavailableMarkets={health.OrderbookUnavailableMarkets} InvalidTokenQuarantineActive={health.InvalidTokenQuarantineActive} InvalidTokenQuarantineAdded={health.InvalidTokenQuarantineAdded} InvalidTokenQuarantineExpired={health.InvalidTokenQuarantineExpired} BatchBookRequestsAvoidedByQuarantine={health.BatchBookRequestsAvoidedByQuarantine} MarketsSkippedByInvalidTokenQuarantine={health.MarketsSkippedByInvalidTokenQuarantine} MarketOrderbookQuarantineActive={health.MarketOrderbookQuarantineActive} MarketOrderbookQuarantineAdded={health.MarketOrderbookQuarantineAdded} MarketOrderbookQuarantineExpired={health.MarketOrderbookQuarantineExpired} MarketsSkippedByMarketOrderbookQuarantine={health.MarketsSkippedByMarketOrderbookQuarantine} BatchBookRequestsAvoidedByMarketQuarantine={health.BatchBookRequestsAvoidedByMarketQuarantine} OrderbookCircuitBreakerActive={health.OrderbookCircuitBreakerActive.ToString().ToLowerInvariant()} OrderbookCircuitBreakerState={health.OrderbookCircuitBreakerState} OrderbookCircuitBreakerOpenCount={health.OrderbookCircuitBreakerOpenCount} OrderbookCircuitBreakerCooldownRemainingSeconds={health.OrderbookCircuitBreakerCooldownRemainingSeconds} OrderbookCircuitBreakerHalfOpenAttempts={health.OrderbookCircuitBreakerHalfOpenAttempts} OrderbookCircuitBreakerHalfOpenSucceeded={health.OrderbookCircuitBreakerHalfOpenSucceeded} OrderbookCircuitBreakerHalfOpenFailed={health.OrderbookCircuitBreakerHalfOpenFailed} OrderbookCircuitBreakerCooldownExtensions={health.OrderbookCircuitBreakerCooldownExtensions} OrderbookCircuitBreakerLastOpenReason={health.OrderbookCircuitBreakerLastOpenReason} SingleTokenIsolationBudgetExhausted={health.SingleTokenIsolationBudgetExhausted} BatchBookBadRequestsPreventedEstimate={health.BatchBookBadRequestsPreventedEstimate} AllowlistHealthy={health.AllowlistHealthy} AllowlistMonitoringOnly={health.AllowlistMonitoringOnly} AllowlistNeedsPricingPrune={health.AllowlistNeedsPricingPrune} AllowlistNeedsRefresh={health.AllowlistNeedsRefresh} AllowlistReviewOnly={health.AllowlistReviewOnly} AllowlistMismatch={health.AllowlistMismatch} AllowlistBrokenConfig={health.AllowlistBrokenConfig} AllowlistDisabled={health.AllowlistDisabled} AllowlistIgnored={health.AllowlistIgnored} AllowlistClassificationTotal={health.AllowlistClassificationTotal} AllowlistClassificationValid={health.AllowlistClassificationValid.ToString().ToLowerInvariant()} AllowlistRefreshPreviewCandidates={health.AllowlistRefreshPreviewCandidates} AllowlistRefreshHighConfidence={health.AllowlistRefreshHighConfidence} AllowlistRefreshFinalNoCandidate={health.AllowlistRefreshFinalNoCandidate} AllowlistRefreshFinalSemanticConflict={health.AllowlistRefreshFinalSemanticConflict} AllowlistRefreshFinalLowConfidence={health.AllowlistRefreshFinalLowConfidence} AllowlistRefreshFinalUnstable={health.AllowlistRefreshFinalUnstable} AllowlistRefreshFinalPreviewOnly={health.AllowlistRefreshFinalPreviewOnly} AllowlistRefreshFinalLockedManualReview={health.AllowlistRefreshFinalLockedManualReview} AllowlistRefreshActionExplainedSuppressed={health.AllowlistRefreshActionExplainedSuppressed} AllowlistRefreshUnstableGroups={health.AllowlistRefreshUnstableGroups} AllowlistRefreshActionFlipFlops={health.AllowlistRefreshActionFlipFlops} AllowlistRefreshAutoApply={health.AllowlistRefreshAutoApply.ToString().ToLowerInvariant()} VerifiedPricingUnavailableGroups={verifiedPricingUnavailableGroups} InvalidTokenQuarantine={state?.OrderBookServiceStats.QuarantinedTokens ?? 0} OrderbookCache={health.OrderbookCacheCount} QuietLogGateCache={health.LogGateCacheSize} MemoryWarnings={state?.MemoryWarnings ?? 0} MemoryCriticals={state?.MemoryCriticals ?? 0} ScannerPausedByMemoryGuard={(state?.ScannerPausedByMemoryGuard ?? false).ToString().ToLowerInvariant()} MemoryStable={memoryStable.ToString().ToLowerInvariant()} LogVolumeStable={logVolumeStable.ToString().ToLowerInvariant()} OrderbookStable={orderbookStable.ToString().ToLowerInvariant()} Strategies={{{strategyStatus}}}";
    }

    private static string FormatStrategySoakStatus(string key, TradingBot.Services.StrategyRuntimeCounterSnapshot value)
    {
        if (key.Equals("VerifiedMultiOutcome", StringComparison.OrdinalIgnoreCase))
            return $"{key}:{value.Mode}:pricingMissingNoAsk={value.VerifiedPricingBlockedByMissingNoAsk}:pricingCircuitBreakerActive={value.VerifiedPricingBlockedByCircuitBreakerActive}:pricingMarketOrderbookQuarantined={value.VerifiedPricingBlockedByMarketOrderbookQuarantined}:pricingTokenQuarantined={value.VerifiedPricingBlockedByTokenQuarantined}:pricingEmptyBook={value.VerifiedPricingBlockedByEmptyBook}:pricingOrderbookUnavailable={value.VerifiedPricingBlockedByOrderbookUnavailable}:pricingQuarantinedToken={value.VerifiedPricingBlockedByQuarantinedToken}:activePositive={value.VerifiedActiveConservativePositive}:rawPositiveOnly={value.VerifiedRawPositiveOnly}:experimentalCandidates={value.VerifiedExperimentalProfileCandidate}:wouldOpenIfPaperEligible={value.VerifiedWouldOpenIfPaperEligible}:wouldOpenBlockedByStability={value.VerifiedRejectedByStability}:wouldOpenBlockedByRisk={value.VerifiedRejectedByRisk}:wouldOpenBlockedByFill={value.VerifiedWouldOpenBlockedByFill}:wouldOpenBlockedByDepth={value.VerifiedWouldOpenBlockedByDepth}:diagBlocked={value.VerifiedDiagnosticsOnlyBlocked}";
        return $"{key}:{value.Mode}:scan={value.Scanned}:books={value.Books}:cand={value.Candidates}:positive={value.PositiveEdges}:paper={value.PaperOpened}:singleMarketCircuitBreakerSkippedMarkets={value.SingleMarketCircuitBreakerSkippedMarkets}:singleMarketCircuitBreakerSkippedCycles={value.SingleMarketCircuitBreakerSkippedCycles}:blocked={value.DiagnosticsOnlyBlocked}:faults={value.Faults}";
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
        var minutes = Math.Max(0.001, (last.TimestampUtc - first.TimestampUtc).TotalMinutes);
        var slope = (last.ProcessMb - first.ProcessMb) / minutes;
        var stable = Math.Abs(slope) <= Math.Max(0, options.StableMemorySlopeMbPerMinute)
            && delta <= Math.Max(0, options.StableMemoryMaxDeltaMb);
        return new RuntimeHealthTrend(Math.Round(min, 2), Math.Round(max, 2), Math.Round(delta, 2), Math.Round(slope, 2), stable, Samples.Count, badDeltaLastHour, invalidDeltaLastHour, skippedDeltaLastHour);
    }
}
