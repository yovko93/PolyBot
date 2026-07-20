using System.Text.Json;
using TradingBot.Options;

namespace TradingBot.Api;

public static class RuntimeSoakStatusExporter
{
    private const int MaxAttempts = 4;
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100)
    ];

    public static string Export(BotRuntimeState state, TradingBotOptions options, string contentRootPath)
    {
        var health = RuntimeHealthSnapshot.From(state, options);
        var logs = state.Logs();
        var trend = RuntimeHealthTrendTracker.Current(options.RuntimeHealth);
        var warmupMinutes = Math.Max(0, options.RuntimeHealth.WarmupMinutes);
        var warmupComplete = warmupMinutes <= 0 || health.Uptime >= TimeSpan.FromMinutes(warmupMinutes);
        var verifiedPricingUnavailableGroups = health.StrategyCounters.TryGetValue("VerifiedMultiOutcome", out var verifiedCounter) ? verifiedCounter.VerifiedPricingBlockedByCircuitBreakerActive + verifiedCounter.VerifiedPricingBlockedByMarketOrderbookQuarantined + verifiedCounter.VerifiedPricingBlockedByTokenQuarantined + verifiedCounter.VerifiedPricingBlockedByOrderbookUnavailable : 0;
        var payload = new
        {
            timestamp = DateTime.UtcNow,
            processRunId = health.ProcessRunId,
            startedAtUtc = health.StartedAtUtc,
            scannerInstanceId = health.ScannerInstanceId,
            diagnosticsCounterMismatchCount = health.DiagnosticsCounterMismatchCount,
            diagnosticsCounterMismatchLastReason = health.DiagnosticsCounterMismatchLastReason,
            diagnosticsCounterMismatchBlockingCount = health.DiagnosticsCounterMismatchBlockingCount,
            diagnosticsCounterMismatchNonBlockingCount = health.DiagnosticsCounterMismatchNonBlockingCount,
            diagnosticsCounterMismatchLastBlockingReason = health.DiagnosticsCounterMismatchLastBlockingReason,
            diagnosticsCounterMismatchLastNonBlockingReason = health.DiagnosticsCounterMismatchLastNonBlockingReason,
            diagnosticsCounterMismatchPaperBlocking = health.DiagnosticsCounterMismatchPaperBlocking,
            globalTradingReadiness = health.GlobalTradingReadiness,
            globalTradingReadinessReason = health.GlobalTradingReadinessReason,
            globalSoakReadiness = health.GlobalSoakReadiness,
            globalSoakReadinessReason = health.GlobalSoakReadinessReason,
            localPaperPhase1Readiness = health.LocalPaperPhase1Readiness,
            localPaperPhase1ReadinessReason = health.LocalPaperPhase1ReadinessReason,
            paperPhase1CanaryEnabled = health.PaperPhase1CanaryEnabled,
            paperPhase1CanaryProfileActive = health.PaperPhase1CanaryProfileActive,
            paperPhase1CanaryRequireProfile = health.PaperPhase1CanaryRequireProfile,
            paperPhase1CanaryRunOnce = health.PaperPhase1CanaryRunOnce,
            paperPhase1CanaryAttempted = health.PaperPhase1CanaryAttempted,
            paperPhase1CanaryOpened = health.PaperPhase1CanaryOpened,
            paperPhase1CanarySettled = health.PaperPhase1CanarySettled,
            paperPhase1CanaryRejected = health.PaperPhase1CanaryRejected,
            paperPhase1CanaryRejectedReason = health.PaperPhase1CanaryRejectedReason,
            paperPhase1CanaryPositionId = health.PaperPhase1CanaryPositionId,
            paperPhase1CanaryExpectedProfit = health.PaperPhase1CanaryExpectedProfit,
            paperPhase1CanaryRealizedPnl = health.PaperPhase1CanaryRealizedPnl,
            paperPhase1CanaryDuplicateSuppressions = health.PaperPhase1CanaryDuplicateSuppressions,
            paperPhase1CanaryLastDuplicateReason = health.PaperPhase1CanaryLastDuplicateReason,
            paperPhase1CanaryOpenAttempts = health.PaperPhase1CanaryOpenAttempts,
            paperPhase1CanaryOpenSucceeded = health.PaperPhase1CanaryOpenSucceeded,
            paperPhase1CanaryPaperOpened = health.PaperPhase1CanaryPaperOpened,
            paperPhase1CanaryPaperClosed = health.PaperPhase1CanaryPaperClosed,
            paperPhase1CanaryOpenPositions = health.PaperPhase1CanaryOpenPositions,
            paperPhase1CanaryExposure = health.PaperPhase1CanaryExposure,
            paperPhase1CanaryLadderOpenAttempted = health.PaperPhase1CanaryLadderOpenAttempted,
            paperPhase1CanaryLadderOpened = health.PaperPhase1CanaryLadderOpened,
            paperPhase1CanaryLadderSettled = health.PaperPhase1CanaryLadderSettled,
            paperPhase1CanarySyntheticOnly = health.PaperPhase1CanarySyntheticOnly,
            paperPhase1CanaryRealOrderSent = health.PaperPhase1CanaryRealOrderSent,
            paperPhase1CanarySigningAttempted = health.PaperPhase1CanarySigningAttempted,
            paperPhase1CanaryConsistent = health.PaperPhase1CanaryConsistent,
            paperPhase1CanaryConsistencyReason = health.PaperPhase1CanaryConsistencyReason,
            paperCounterSourceOfTruth = health.PaperCounterSourceOfTruth,
            paperCounterDuplicateSuppressions = health.PaperCounterDuplicateSuppressions,
            paperCounterLastDuplicateExecutionId = health.PaperCounterLastDuplicateExecutionId,
            paperCounterCountedExecutionIds = health.PaperCounterCountedExecutionIds,
            paperCounterSyntheticCanaryCountedExecutions = health.PaperCounterSyntheticCanaryCountedExecutions,
            paperCounterRealScannerCountedExecutions = health.PaperCounterRealScannerCountedExecutions,
            paperCounterAuditEnabled = health.PaperCounterAuditEnabled,
            paperCounterIncrementLogsWritten = health.PaperCounterIncrementLogsWritten,
            paperCounterDuplicateIncrementLogSuppressions = health.PaperCounterDuplicateIncrementLogSuppressions,
            paperCounterDuplicateSuppressionLogsWritten = health.PaperCounterDuplicateSuppressionLogsWritten,
            paperCounterLastIncrementExecutionId = health.PaperCounterLastIncrementExecutionId,
            paperCounterLastSuppressedDuplicateExecutionId = health.PaperCounterLastSuppressedDuplicateExecutionId,
            paperCounterAuditConsistent = health.PaperCounterAuditConsistent,
            paperPhase1ConsistencyReason = health.PaperPhase1ConsistencyReason,
            discoveryInvariantScope = health.DiscoveryInvariantScope,
            uptime = health.Uptime,
            processMemoryMb = health.ProcessMemoryMb,
            gcMemoryMb = health.GcTotalMemoryMb,
            minProcessMemoryMbWindow = trend.MinProcessMemoryMbWindow,
            maxProcessMemoryMbWindow = trend.MaxProcessMemoryMbWindow,
            memoryDeltaMbWindow = trend.MemoryDeltaMbWindow,
            memorySlopeMbPerMinute = trend.MemorySlopeMbPerMinute,
            warmupMinutes,
            warmupComplete,
            isMemoryStable = warmupComplete && trend.IsMemoryStable && state.MemoryCriticals == 0,
            logsCount = health.RecentLogsCount,
            executionAuditCount = health.ExecutionAuditCount,
            signalRBufferCount = health.SignalREventBufferCount,
            orderbookCacheCount = health.OrderbookCacheCount,
            marketCacheCount = health.MarketCacheCount,
            singleMarketOpportunities = health.SingleMarketOpportunitiesCount,
            singleMarketDataQualitySamples = health.SingleMarketDataQualitySamplesCount,
            singleMarketNearMisses = health.SingleMarketNearMissesCount,
            singleMarketRawCandidates = health.SingleMarketRawCandidates,
            singleMarketDataQualityRejected = health.SingleMarketDataQualityRejected,
            singleMarketDataQualityRejectedRawPositive = health.SingleMarketDataQualityRejectedRawPositive,
            singleMarketBelowMinEdge = health.SingleMarketBelowMinEdge,
            singleMarketPositiveBeforeCost = health.SingleMarketPositiveBeforeCost,
            singleMarketPositiveAfterCost = health.SingleMarketPositiveAfterCost,
            singleMarketPositiveAfterSafety = health.SingleMarketPositiveAfterSafety,
            singleMarketValidRawPositive = health.SingleMarketValidRawPositive,
            singleMarketValidAfterCostPositive = health.SingleMarketValidAfterCostPositive,
            singleMarketValidAfterSafetyPositive = health.SingleMarketValidAfterSafetyPositive,
            singleMarketEdgeStable = health.SingleMarketEdgeStable,
            singleMarketExecutionReady = health.SingleMarketExecutionReady,
            singleMarketRejectedByFill = health.SingleMarketRejectedByFill,
            singleMarketRejectedByDepth = health.SingleMarketRejectedByDepth,
            singleMarketRejectedByRisk = health.SingleMarketRejectedByRisk,
            singleMarketRejectedByPaperDiagnosticsLimitedGate = health.SingleMarketRejectedByPaperDiagnosticsLimitedGate,
            singleMarketBestRawEdge = health.SingleMarketBestRawEdge,
            singleMarketBestAfterCostEdge = health.SingleMarketBestAfterCostEdge,
            singleMarketBestAfterSafetyEdge = health.SingleMarketBestAfterSafetyEdge,
            singleMarketBestExecutableEdge = health.SingleMarketBestExecutableEdge,
            singleMarketBestRejectedReason = health.SingleMarketBestRejectedReason,
            singleMarketValidEdgeSamples = health.SingleMarketValidEdgeSamples,
            singleMarketEdgeDistributionSampleMode = health.SingleMarketEdgeDistributionSampleMode,
            singleMarketEdgeDistributionCapacity = health.SingleMarketEdgeDistributionCapacity,
            singleMarketEdgeDistributionDroppedSamples = health.SingleMarketEdgeDistributionDroppedSamples,
            singleMarketRawEdgeMin = health.SingleMarketRawEdgeMin,
            singleMarketRawEdgeP01 = health.SingleMarketRawEdgeP01,
            singleMarketRawEdgeP05 = health.SingleMarketRawEdgeP05,
            singleMarketRawEdgeP10 = health.SingleMarketRawEdgeP10,
            singleMarketRawEdgeP25 = health.SingleMarketRawEdgeP25,
            singleMarketRawEdgeP50 = health.SingleMarketRawEdgeP50,
            singleMarketRawEdgeP75 = health.SingleMarketRawEdgeP75,
            singleMarketRawEdgeP90 = health.SingleMarketRawEdgeP90,
            singleMarketRawEdgeP95 = health.SingleMarketRawEdgeP95,
            singleMarketRawEdgeP99 = health.SingleMarketRawEdgeP99,
            singleMarketRawEdgeMax = health.SingleMarketRawEdgeMax,
            singleMarketAfterCostEdgeMin = health.SingleMarketAfterCostEdgeMin,
            singleMarketAfterCostEdgeP01 = health.SingleMarketAfterCostEdgeP01,
            singleMarketAfterCostEdgeP05 = health.SingleMarketAfterCostEdgeP05,
            singleMarketAfterCostEdgeP10 = health.SingleMarketAfterCostEdgeP10,
            singleMarketAfterCostEdgeP25 = health.SingleMarketAfterCostEdgeP25,
            singleMarketAfterCostEdgeP50 = health.SingleMarketAfterCostEdgeP50,
            singleMarketAfterCostEdgeP75 = health.SingleMarketAfterCostEdgeP75,
            singleMarketAfterCostEdgeP90 = health.SingleMarketAfterCostEdgeP90,
            singleMarketAfterCostEdgeP95 = health.SingleMarketAfterCostEdgeP95,
            singleMarketAfterCostEdgeP99 = health.SingleMarketAfterCostEdgeP99,
            singleMarketAfterCostEdgeMax = health.SingleMarketAfterCostEdgeMax,
            singleMarketAfterSafetyEdgeMin = health.SingleMarketAfterSafetyEdgeMin,
            singleMarketAfterSafetyEdgeP01 = health.SingleMarketAfterSafetyEdgeP01,
            singleMarketAfterSafetyEdgeP05 = health.SingleMarketAfterSafetyEdgeP05,
            singleMarketAfterSafetyEdgeP10 = health.SingleMarketAfterSafetyEdgeP10,
            singleMarketAfterSafetyEdgeP25 = health.SingleMarketAfterSafetyEdgeP25,
            singleMarketAfterSafetyEdgeP50 = health.SingleMarketAfterSafetyEdgeP50,
            singleMarketAfterSafetyEdgeP75 = health.SingleMarketAfterSafetyEdgeP75,
            singleMarketAfterSafetyEdgeP90 = health.SingleMarketAfterSafetyEdgeP90,
            singleMarketAfterSafetyEdgeP95 = health.SingleMarketAfterSafetyEdgeP95,
            singleMarketAfterSafetyEdgeP99 = health.SingleMarketAfterSafetyEdgeP99,
            singleMarketAfterSafetyEdgeMax = health.SingleMarketAfterSafetyEdgeMax,
            singleMarketAfterSafetyEdgeBelowMinus5bp = health.SingleMarketAfterSafetyEdgeBelowMinus5bp,
            singleMarketAfterSafetyEdgeMinus5bpToMinus2bp = health.SingleMarketAfterSafetyEdgeMinus5bpToMinus2bp,
            singleMarketAfterSafetyEdgeMinus2bpToMinus1bp = health.SingleMarketAfterSafetyEdgeMinus2bpToMinus1bp,
            singleMarketAfterSafetyEdgeMinus1bpTo0 = health.SingleMarketAfterSafetyEdgeMinus1bpTo0,
            singleMarketAfterSafetyEdge0To1bp = health.SingleMarketAfterSafetyEdge0To1bp,
            singleMarketAfterSafetyEdge1bpTo5bp = health.SingleMarketAfterSafetyEdge1bpTo5bp,
            singleMarketAfterSafetyEdgeAbove5bp = health.SingleMarketAfterSafetyEdgeAbove5bp,
            paperOpenedCount = state.SingleMarketExecutionsCount,
            paperClosedCount = health.PaperClosedPositions,
            paperExposure = health.PaperTotalExposure,
            paperRealizedPnl = health.PaperRealizedPnl,
            paperLocked = health.PaperLocked,
            quietSuppressed = health.QuietSuppressedTotal,
            emittedLogs = health.EmittedLogs,
            logGateCacheSize = health.LogGateCacheSize,
            quietSuppressedByCategory = health.QuietSuppressedByCategory,
            emittedByCategory = health.EmittedByCategory,
            logVolumeStable = RuntimeHealthTrendTracker.IsLogVolumeStable(health, options),
            batchBookRequests = health.BatchBookRequests,
            batchBookBadRequests = health.BatchBookBadRequests,
            batchBookTimeouts = health.BatchBookTimeouts,
            batchBookRetrySuccesses = health.BatchBookRetrySuccesses,
            batchBookInvalidTokens = health.BatchBookInvalidTokens,
            batchBookSuppressedErrors = health.BatchBookSuppressedErrors,
            batchBookSplitRetriesAttempted = health.BatchBookSplitRetriesAttempted,
            batchBookSplitRetrySucceeded = health.BatchBookSplitRetrySucceeded,
            batchBookSplitRetryFailed = health.BatchBookSplitRetryFailed,
            batchBookSingleTokenFailures = health.BatchBookSingleTokenFailures,
            batchBookSingleTokenQuarantined = health.BatchBookSingleTokenQuarantined,
            batchBookSkippedQuarantinedTokens = health.BatchBookSkippedQuarantinedTokens,
            batchBookSkippedMarketsWithQuarantinedTokens = health.BatchBookSkippedMarketsWithQuarantinedTokens,
            batchBookBadRequestRate = health.BatchBookRequests <= 0 ? 0d : (double)health.BatchBookBadRequests / health.BatchBookRequests,
            batchBookCanaryRequests = health.BatchBookCanaryRequests,
            batchBookCanaryBadRequests = health.BatchBookCanaryBadRequests,
            batchBookCanaryTimeouts = health.BatchBookCanaryTimeouts,
            batchBookCanaryInvalidTokens = health.BatchBookCanaryInvalidTokens,
            batchBookCanaryOrderbookUnavailable = health.BatchBookCanaryOrderbookUnavailable,
            orderbookCircuitBreakerLastHalfOpenFailureReason = health.OrderbookCircuitBreakerLastHalfOpenFailureReason,
            batchBookRecoveryRequests = health.BatchBookRecoveryRequests,
            batchBookRecoveryBadRequests = health.BatchBookRecoveryBadRequests,
            orderbookRecoveryLimitedRequests = health.OrderbookRecoveryLimitedRequests,
            orderbookRecoveryLimitedMarkets = health.OrderbookRecoveryLimitedMarkets,
            orderbookRecoveryBadRequests = health.OrderbookRecoveryBadRequests,
            orderbookRecoveryInvalidTokens = health.OrderbookRecoveryInvalidTokens,
            orderbookRecoverySucceededCount = health.OrderbookRecoverySucceededCount,
            orderbookRecoveryFailedCount = health.OrderbookRecoveryFailedCount,
            batchBookNormalRequests = health.BatchBookNormalRequests,
            batchBookNormalBadRequests = health.BatchBookNormalBadRequests,
            batchBookNormalRequestsBeforeBreakerOpen = health.BatchBookNormalRequestsBeforeBreakerOpen,
            batchBookNormalBadRequestsBeforeBreakerOpen = health.BatchBookNormalBadRequestsBeforeBreakerOpen,
            batchBookNormalRequestsAfterBreakerOpen = health.BatchBookNormalRequestsAfterBreakerOpen,
            batchBookNormalBadRequestsAfterBreakerOpen = health.BatchBookNormalBadRequestsAfterBreakerOpen,
            quarantinedMarketsReintroducedBlocked = health.QuarantinedMarketsReintroducedBlocked,
            quarantinedTokensReintroducedBlocked = health.QuarantinedTokensReintroducedBlocked,
            batchBookBadRequestsDeltaLastHour = trend.BatchBookBadRequestsDeltaLastHour,
            batchBookInvalidTokensDeltaLastHour = trend.BatchBookInvalidTokensDeltaLastHour,
            quarantinedTokens = state.OrderBookServiceStats.QuarantinedTokens,
            skippedQuarantinedTokensLastHour = trend.SkippedQuarantinedTokensLastHour,
            orderbookUnavailableMarkets = health.OrderbookUnavailableMarkets,
            orderbookCircuitBreakerState = health.OrderbookCircuitBreakerState,
            orderbookCircuitBreakerActive = health.OrderbookCircuitBreakerActive,
            orderbookCircuitBreakerRecoveringSinceUtc = health.OrderbookCircuitBreakerRecoveringSinceUtc,
            orderbookCircuitBreakerRecoveryRemainingSeconds = health.OrderbookCircuitBreakerRecoveryRemainingSeconds,
            orderbookCircuitBreakerReopenedAfterClose = health.OrderbookCircuitBreakerReopenedAfterClose,
            orderbookRequestsBlockedByCircuitBreaker = health.OrderbookRequestsBlockedByCircuitBreaker,
            orderbookPostCloseBadRequests = health.OrderbookPostCloseBadRequests,
            orderbookPostCloseInvalidTokens = health.OrderbookPostCloseInvalidTokens,
            allowlistHealthy = health.AllowlistHealthy,
            allowlistMonitoringOnly = health.AllowlistMonitoringOnly,
            allowlistNeedsPricingPrune = health.AllowlistNeedsPricingPrune,
            allowlistNeedsRefresh = health.AllowlistNeedsRefresh,
            allowlistReviewOnly = health.AllowlistReviewOnly,
            allowlistMismatch = health.AllowlistMismatch,
            allowlistBrokenConfig = health.AllowlistBrokenConfig,
            allowlistDisabled = health.AllowlistDisabled,
            allowlistIgnored = health.AllowlistIgnored,
            allowlistClassificationTotal = health.AllowlistClassificationTotal,
            allowlistClassificationValid = health.AllowlistClassificationValid,
            allowlistRefreshFinalLockedManualReview = health.AllowlistRefreshFinalLockedManualReview,
            allowlistRefreshUnstableGroups = health.AllowlistRefreshUnstableGroups,
            allowlistRefreshActionFlipFlops = health.AllowlistRefreshActionFlipFlops,
            allowlistRefreshActionExplainedSuppressed = health.AllowlistRefreshActionExplainedSuppressed,
            allowlistRefreshAutoApply = health.AllowlistRefreshAutoApply,
            discoveryHealthy = health.DiscoveryHealthy,
            discoveryUsingLastHealthySnapshot = health.DiscoveryUsingLastHealthySnapshot,
            discoveryLastFailureReason = health.DiscoveryLastFailureReason,
            discoveryLastHealthySnapshotAgeSeconds = health.DiscoveryLastHealthySnapshotAgeSeconds,
            discoveryPartialAttemptCount = health.DiscoveryPartialAttemptCount,
            scannerPausedByDiscoveryGuard = health.ScannerPausedByDiscoveryGuard,
            discoveryGuardSkippedCycles = health.DiscoveryGuardSkippedCycles,
            discoveryGuardUsingLastHealthySnapshot = health.DiscoveryGuardUsingLastHealthySnapshot,
            discoveryGuardBlockedNewMarkets = health.DiscoveryGuardBlockedNewMarkets,
            longRunStable = health.LongRunStable,
            longRunBlockingReason = health.LongRunBlockingReason,
            discoveryStable = health.DiscoveryStable,
            orderbookRecoveredAfterDegradation = health.OrderbookRecoveredAfterDegradation,
            lastDegradationUtc = health.LastDegradationUtc,
            lastRecoveryUtc = health.LastRecoveryUtc,
            discoveryBootstrapHealthy = health.DiscoveryBootstrapHealthy,
            discoveryBootstrapRetryCount = health.DiscoveryBootstrapRetryCount,
            discoveryBootstrapLastAttemptUtc = health.DiscoveryBootstrapLastAttemptUtc,
            discoveryBootstrapNextRetryUtc = health.DiscoveryBootstrapNextRetryUtc,
            discoveryBootstrapBackoffSeconds = health.DiscoveryBootstrapBackoffSeconds,
            discoveryBootstrapFailureReason = health.DiscoveryBootstrapFailureReason,
            discoveryRetryBackoffSeconds = health.DiscoveryRetryBackoffSeconds,
            discoveryRetriesSuppressedByBackoff = health.DiscoveryRetriesSuppressedByBackoff,
            discoveryPersistedSnapshotLoaded = health.DiscoveryPersistedSnapshotLoaded,
            discoveryPersistedSnapshotAgeSeconds = health.DiscoveryPersistedSnapshotAgeSeconds,
            discoveryPersistedSnapshotActiveMarkets = health.DiscoveryPersistedSnapshotActiveMarkets,
            allowlistEvaluationSkipped = health.AllowlistEvaluationSkipped,
            allowlistEvaluationSkippedReason = health.AllowlistEvaluationSkippedReason,
            allowlistClassificationBlockedByDiscovery = health.AllowlistClassificationBlockedByDiscovery,
            discoveryBlockedReason = health.DiscoveryBlockedReason,
            discoverySelectedSource = health.DiscoverySelectedSource,
            discoveryScannerSafeSourceAvailable = health.DiscoveryScannerSafeSourceAvailable,
            discoverySourceAuditOnly = health.DiscoverySourceAuditOnly,
            discoverySourceAuditExportWritten = health.DiscoverySourceAuditExportWritten,
            discoverySourceAuditExportPath = health.DiscoverySourceAuditExportPath,
            discoverySourceAuditSources = health.DiscoverySourceAuditSources,
            discoverySourceAuditScannerSafeSources = health.DiscoverySourceAuditScannerSafeSources,
            discoverySourceAuditRecommendedAction = health.DiscoverySourceAuditRecommendedAction,
            soakReadiness = health.SoakReadiness,
            soakReadinessReason = health.SoakReadinessReason,
            strategies = health.StrategyCounters,
            focusUniverseEnabled = health.FocusUniverseEnabled,
            focusUniverseWatchlistSize = health.FocusUniverseWatchlistSize,
            focusUniverseAdmitted = health.FocusUniverseAdmitted,
            focusUniverseEvicted = health.FocusUniverseEvicted,
            focusUniverseRefreshed = health.FocusUniverseRefreshed,
            focusUniverseSkippedByOrderbookHealth = health.FocusUniverseSkippedByOrderbookHealth,
            focusUniverseImproving = health.FocusUniverseImproving,
            focusUniverseWorsening = health.FocusUniverseWorsening,
            focusUniverseStable = health.FocusUniverseStable,
            focusUniverseBestStrategy = health.FocusUniverseBestStrategy,
            focusUniverseBestAfterSafetyEdge = health.FocusUniverseBestAfterSafetyEdge,
            focusUniverseBestEdgeDelta = health.FocusUniverseBestEdgeDelta,
            focusUniverseClosestToBreakEvenCount = health.FocusUniverseClosestToBreakEvenCount,
            focusUniverseExecutionReady = health.FocusUniverseExecutionReady,
            focusUniversePaperOpened = health.FocusUniversePaperOpened,
            focusUniverseConsistent = health.FocusUniverseConsistent,
            edgeCompressionEnabled = health.EdgeCompressionEnabled,
            edgeCompressionItems = health.EdgeCompressionItems,
            edgeCompressionNearBreakEven = health.EdgeCompressionNearBreakEven,
            edgeCompressionCompressing = health.EdgeCompressionCompressing,
            edgeCompressionExpanding = health.EdgeCompressionExpanding,
            edgeCompressionFlat = health.EdgeCompressionFlat,
            edgeCompressionRawPositive = health.EdgeCompressionRawPositive,
            edgeCompressionAfterCostPositive = health.EdgeCompressionAfterCostPositive,
            edgeCompressionAfterSafetyPositive = health.EdgeCompressionAfterSafetyPositive,
            edgeCompressionBlockedByMarketSpread = health.EdgeCompressionBlockedByMarketSpread,
            edgeCompressionBlockedByCost = health.EdgeCompressionBlockedByCost,
            edgeCompressionBlockedBySafety = health.EdgeCompressionBlockedBySafety,
            edgeCompressionBestStrategy = health.EdgeCompressionBestStrategy,
            edgeCompressionBestRawEdge = health.EdgeCompressionBestRawEdge,
            edgeCompressionBestAfterCostEdge = health.EdgeCompressionBestAfterCostEdge,
            edgeCompressionBestAfterSafetyEdge = health.EdgeCompressionBestAfterSafetyEdge,
            edgeCompressionBestDistanceToBreakEven = health.EdgeCompressionBestDistanceToBreakEven,
            edgeCompressionMedianDistanceToBreakEven = health.EdgeCompressionMedianDistanceToBreakEven,
            edgeCompressionP95AfterSafetyEdge = health.EdgeCompressionP95AfterSafetyEdge,
            edgeCompressionDominantDragComponent = health.EdgeCompressionDominantDragComponent,
            edgeCompressionConsistent = health.EdgeCompressionConsistent,
            strategyCompact = string.Join(",", health.StrategyCounters.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value.Mode}:scan={x.Value.Scanned}:books={x.Value.Books}:paper={x.Value.PaperOpened}:faults={x.Value.Faults}")),
            verifiedPricingUnavailableGroups,
            spreadMicrostructureEnabled = health.SpreadMicrostructureEnabled,
            spreadMicrostructureItems = health.SpreadMicrostructureItems,
            spreadMicrostructureSkippedByOrderbookHealth = health.SpreadMicrostructureSkippedByOrderbookHealth,
            spreadMicrostructureWideAskSpread = health.SpreadMicrostructureWideAskSpread,
            spreadMicrostructureThinTopBook = health.SpreadMicrostructureThinTopBook,
            spreadMicrostructureBothWideAndThin = health.SpreadMicrostructureBothWideAndThin,
            spreadMicrostructureAlreadyNearExecutable = health.SpreadMicrostructureAlreadyNearExecutable,
            spreadMicrostructureDepthSufficient = health.SpreadMicrostructureDepthSufficient,
            spreadMicrostructureBestStrategy = health.SpreadMicrostructureBestStrategy,
            spreadMicrostructureBestAfterSafetyEdge = health.SpreadMicrostructureBestAfterSafetyEdge,
            spreadMicrostructureBestMoveNeededToBreakEven = health.SpreadMicrostructureBestMoveNeededToBreakEven,
            spreadMicrostructureMedianMoveNeededToBreakEven = health.SpreadMicrostructureMedianMoveNeededToBreakEven,
            spreadMicrostructureP95MoveNeededToBreakEven = health.SpreadMicrostructureP95MoveNeededToBreakEven,
            spreadMicrostructureMinTicksToBreakEven = health.SpreadMicrostructureMinTicksToBreakEven,
            spreadMicrostructureMedianTicksToBreakEven = health.SpreadMicrostructureMedianTicksToBreakEven,
            spreadMicrostructureDominantCause = health.SpreadMicrostructureDominantCause,
            spreadMicrostructureConsistent = health.SpreadMicrostructureConsistent,
            orderbookStable = health.OrderbookCircuitBreakerState == "Closed"
                && (health.BatchBookRequests <= 0 ? 0d : (double)health.BatchBookBadRequests / health.BatchBookRequests) <= options.Soak.MaxBatchBookBadRequestRate
                && trend.BatchBookBadRequestsDeltaLastHour <= options.Soak.MaxBatchBookBadRequestsPerHour
                && trend.BatchBookInvalidTokensDeltaLastHour <= options.Soak.MaxBatchBookInvalidTokensPerHour
                && health.BatchBookRepeatedInvalidTokenAfterQuarantine <= options.OrderBook.MaxRepeatedInvalidTokenAfterQuarantine
                && health.BatchBookSplitRetryFailed <= options.OrderBook.MaxBatchSplitRetriesPerCycle
                && health.OrderbookUnavailableMarkets <= options.Soak.MaxOrderbookUnavailableMarkets,
            invalidTokenQuarantine = state.OrderBookServiceStats.QuarantinedTokens,
            memoryWarnings = Math.Max(state.MemoryWarnings, logs.Count(x => x.Message.Contains("[MEMORY_WARNING]", StringComparison.OrdinalIgnoreCase))),
            memoryCriticals = Math.Max(state.MemoryCriticals, logs.Count(x => x.Message.Contains("[MEMORY_CRITICAL]", StringComparison.OrdinalIgnoreCase))),
            lastMemoryCriticalAt = state.LastMemoryCriticalAt,
            scannerPausedByMemoryGuard = state.ScannerPausedByMemoryGuard,
            liveTradingEnabled = options.EnableLiveExecution,
            paperOnly = options.PaperOnly,
            signalRPayloadTrimmedTotal = state.SignalRPayloadTrimmedTotal,
            signalRPayloadTrimmedLogged = state.SignalRPayloadTrimmedLogged,
            signalRPayloadTrimmedSuppressed = state.SignalRPayloadTrimmedSuppressed,
            signalRPayloadTrimmedLastEvent = state.SignalRPayloadTrimmedLastEvent,
            signalRPayloadTrimmedLastItemsBefore = state.SignalRPayloadTrimmedLastItemsBefore,
            signalRPayloadTrimmedLastItemsAfter = state.SignalRPayloadTrimmedLastItemsAfter,
            signalRPayloadTrimmedSummaryIntervalSeconds = options.SignalRLogNoiseControl.PayloadTrimmedSummaryIntervalSeconds,
            signalRLogNoiseControlEnabled = options.SignalRLogNoiseControl.Enabled,
            signalRLogNoiseControlConsistent = state.SignalRLogNoiseControlConsistent,
            runtimeStatusExportWriteFailures = state.RuntimeStatusExportWriteFailures,
            runtimeStatusExportReadFailures = state.RuntimeStatusExportReadFailures,
            runtimeStatusExportLastFailureReason = state.RuntimeStatusExportLastFailureReason,
            runtimeStatusExportRecoveredCount = state.RuntimeStatusExportRecoveredCount,
            runtimeStatusExportStable = state.RuntimeStatusExportStable,
            soakReady = options.Diagnostics.OperationalQuietMode
                && options.RuntimeHealth.Enabled
                && options.SignalR.MaxPayloadItems > 0
                && options.SignalR.MaxPayloadBytes > 0
                && options.PaperOnly
                && !options.EnableLiveExecution
        };
        var path = Path.Combine(contentRootPath, "exports/runtime-soak-status-latest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        WriteAtomicWithRetry(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }), state);
        return path;
    }

    private static void WriteAtomicWithRetry(string path, string contents, BotRuntimeState state)
    {
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var tempPath = $"{path}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream))
                    writer.Write(contents);

                if (File.Exists(path))
                    File.Replace(tempPath, path, null);
                else
                    File.Move(tempPath, path);

                state.RecordRuntimeStatusExportSuccess();
                return;
            }
            catch (IOException ex) when (attempt < MaxAttempts)
            {
                TryDelete(tempPath);
                Thread.Sleep(RetryDelays[Math.Min(attempt - 1, RetryDelays.Length - 1)]);
                state.RecordRuntimeStatusExportFailure(write: true, CompactReason(ex));
            }
            catch (IOException ex)
            {
                TryDelete(tempPath);
                state.RecordRuntimeStatusExportFailure(write: true, CompactReason(ex));
                return;
            }
            catch
            {
                TryDelete(tempPath);
                throw;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static string CompactReason(IOException ex)
    {
        var message = ex.Message.Replace(Environment.NewLine, " ");
        return message.Length <= 180 ? message : message[..180];
    }
}
