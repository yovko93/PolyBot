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
    int OrderbookUnavailableMarkets)
{
    public string ToLogLine()
    {
        var suppressed = string.Join(",", QuietSuppressedByCategory.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value}"));
        var emitted = string.Join(",", EmittedByCategory.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value}"));
        return $"[RUNTIME_HEALTH] ProcessMb={ProcessMemoryMb} GcMb={GcTotalMemoryMb} WorkingSetMb={WorkingSetMb} Logs={RecentLogsCount} ScannerHistory={ScannerHistoryCount} CandidateSnapshots={CandidateSnapshotCount} RepairHistory={RepairHistoryCount} ExecutionAudit={ExecutionAuditCount} SignalRBuffer={SignalREventBufferCount} OrderbookCache={OrderbookCacheCount} MarketCache={MarketCacheCount} PatchPreviewItems={PatchPreviewItemsCount} SingleMarketOpportunities={SingleMarketOpportunitiesCount} SingleMarketNearMisses={SingleMarketNearMissesCount} SingleMarketDataQualitySamples={SingleMarketDataQualitySamplesCount} SingleMarketExecutions={SingleMarketExecutionsCount} PaperPhase={PaperPhase} PaperOpenPositions={PaperOpenPositions} PaperClosedPositions={PaperClosedPositions} PaperSettlements={PaperSettlements} PaperRealizedPnl={PaperRealizedPnl} PaperLocked={PaperLocked} PaperCash={PaperCash} PaperEquity={PaperEquity} PaperTotalExposure={PaperTotalExposure} PaperOpenCountLastHour={PaperOpenCountLastHour} PaperPretradeRejects={PaperPretradeRejects} PaperDuplicateSuppressions={PaperDuplicateSuppressions} PaperOpenPositionKeys={string.Join("|", PaperOpenPositionKeys)} PaperOpenMarketIdsCount={PaperOpenMarketIdsCount} PaperOpenMarketIdsSample={string.Join("|", PaperOpenMarketIdsSample)} PaperInFlightOpens={PaperInFlightOpens} PaperDuplicateDedupeEntries={PaperDuplicateDedupeEntries} PaperStaleDedupeEntriesCleared={PaperStaleDedupeEntriesCleared} PaperSettlementRejects={PaperSettlementRejects} PaperDuplicateSettlementSuppressions={PaperDuplicateSettlementSuppressions} PaperLifecycleEvents={PaperLifecycleEvents} PaperOpenEvents={PaperOpenEvents} PaperCloseEvents={PaperCloseEvents} LiveTradingBlocked={LiveTradingBlockedCount} PaperExecutions={PaperExecutionsCount} SigningAttempts={SigningAttempts} DuplicateGroupKeyWarnings={DuplicateGroupKeyWarnings} QuietSuppressed={QuietSuppressedTotal} EmittedLogs={EmittedLogs} LogGateCache={LogGateCacheSize} QuietSuppressedByCategory={{{suppressed}}} EmittedByCategory={{{emitted}}} BatchBookRequests={BatchBookRequests} BatchBookBadRequests={BatchBookBadRequests} BatchBookTimeouts={BatchBookTimeouts} BatchBookRetrySuccesses={BatchBookRetrySuccesses} BatchBookInvalidTokens={BatchBookInvalidTokens} BatchBookSuppressedErrors={BatchBookSuppressedErrors} BatchBookSplitRetriesAttempted={BatchBookSplitRetriesAttempted} BatchBookSplitRetrySucceeded={BatchBookSplitRetrySucceeded} BatchBookSplitRetryFailed={BatchBookSplitRetryFailed} BatchBookSingleTokenFailures={BatchBookSingleTokenFailures} BatchBookSingleTokenQuarantined={BatchBookSingleTokenQuarantined} BatchBookSkippedQuarantinedTokens={BatchBookSkippedQuarantinedTokens} BatchBookSkippedMarketsWithQuarantinedTokens={BatchBookSkippedMarketsWithQuarantinedTokens} OrderbookUnavailableMarkets={OrderbookUnavailableMarkets} Uptime={Uptime}";
    }

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
            OrderbookUnavailableMarkets: state.OrderBookServiceStats.OrderbookUnavailableMarkets);
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
            return AnalyzeNoLock(options);
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
        var orderbookStable = options is null || (batchRate <= options.OrderBook.MaxBadRequestRateForStable && trend.BatchBookBadRequestsDeltaLastHour <= options.OrderBook.MaxBadRequestsPerHourForStable && health.BatchBookRepeatedInvalidTokenAfterQuarantine <= options.OrderBook.MaxRepeatedInvalidTokenAfterQuarantine);
        var warmupMinutes = options?.RuntimeHealth.WarmupMinutes ?? 0;
        var warmupComplete = warmupMinutes <= 0 || health.Uptime >= TimeSpan.FromMinutes(warmupMinutes);
        var memoryStable = warmupComplete && trend.IsMemoryStable && (state?.MemoryCriticals ?? 0) == 0;
        return $"[SOAK_STATUS] Uptime={health.Uptime} ProcessMb={health.ProcessMemoryMb} DeltaMb={trend.MemoryDeltaMbWindow:0.##} SlopeMbPerMin={trend.MemorySlopeMbPerMinute:0.##} WarmupMinutes={Math.Max(0, warmupMinutes)} WarmupComplete={warmupComplete.ToString().ToLowerInvariant()} Logs={health.RecentLogsCount} ExecutionAudit={health.ExecutionAuditCount} SignalR={health.SignalREventBufferCount} PaperOpened={health.PaperExecutionsCount} PaperOpenPositions={health.PaperOpenPositions} PaperInFlightOpens={health.PaperInFlightOpens} PaperDuplicateSuppressions={health.PaperDuplicateSuppressions} PaperStaleDedupeEntriesCleared={health.PaperStaleDedupeEntriesCleared} PaperClosed={health.PaperClosedPositions} PaperExposure={health.PaperTotalExposure:0.####} PaperRealizedPnl={health.PaperRealizedPnl:0.####} PaperLocked={health.PaperLocked:0.####} LiveTradingBlocked={health.LiveTradingBlockedCount} QuietSuppressed={health.QuietSuppressedTotal} BatchBookRequests={health.BatchBookRequests} BatchBookBadRequests={health.BatchBookBadRequests} BatchBookTimeouts={health.BatchBookTimeouts} BatchBookRetrySuccesses={health.BatchBookRetrySuccesses} BatchBookInvalidTokens={health.BatchBookInvalidTokens} BatchBookSuppressedErrors={health.BatchBookSuppressedErrors} BatchBookSplitRetriesAttempted={health.BatchBookSplitRetriesAttempted} BatchBookSplitRetrySucceeded={health.BatchBookSplitRetrySucceeded} BatchBookSplitRetryFailed={health.BatchBookSplitRetryFailed} BatchBookSingleTokenFailures={health.BatchBookSingleTokenFailures} BatchBookSingleTokenQuarantined={health.BatchBookSingleTokenQuarantined} BatchBookSkippedQuarantinedTokens={health.BatchBookSkippedQuarantinedTokens} BatchBookSkippedMarketsWithQuarantinedTokens={health.BatchBookSkippedMarketsWithQuarantinedTokens} BatchBookBadRequestRate={batchRate:0.####} BatchBookBadRequestsDeltaLastHour={trend.BatchBookBadRequestsDeltaLastHour} BatchBookInvalidTokensDeltaLastHour={trend.BatchBookInvalidTokensDeltaLastHour} QuarantinedTokens={state?.OrderBookServiceStats.QuarantinedTokens ?? 0} SkippedQuarantinedTokensLastHour={trend.SkippedQuarantinedTokensLastHour} OrderbookUnavailableMarkets={health.OrderbookUnavailableMarkets} VerifiedPricingUnavailableGroups=0 InvalidTokenQuarantine={state?.OrderBookServiceStats.QuarantinedTokens ?? 0} OrderbookCache={health.OrderbookCacheCount} QuietLogGateCache={health.LogGateCacheSize} MemoryWarnings={state?.MemoryWarnings ?? 0} MemoryCriticals={state?.MemoryCriticals ?? 0} ScannerPausedByMemoryGuard={(state?.ScannerPausedByMemoryGuard ?? false).ToString().ToLowerInvariant()} MemoryStable={memoryStable.ToString().ToLowerInvariant()} LogVolumeStable={logVolumeStable.ToString().ToLowerInvariant()} OrderbookStable={orderbookStable.ToString().ToLowerInvariant()}";
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

    private static RuntimeHealthTrend AnalyzeNoLock(TradingBot.Options.RuntimeHealthOptions options)
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
        var badDeltaLastHour = Math.Max(0, last.BatchBadRequests - hourBase.BatchBadRequests);
        var invalidDeltaLastHour = Math.Max(0, last.BatchInvalidTokens - hourBase.BatchInvalidTokens);
        var skippedDeltaLastHour = Math.Max(0, last.SkippedQuarantinedTokens - hourBase.SkippedQuarantinedTokens);
        var minutes = Math.Max(0.001, (last.TimestampUtc - first.TimestampUtc).TotalMinutes);
        var slope = (last.ProcessMb - first.ProcessMb) / minutes;
        var stable = Math.Abs(slope) <= Math.Max(0, options.StableMemorySlopeMbPerMinute)
            && delta <= Math.Max(0, options.StableMemoryMaxDeltaMb);
        return new RuntimeHealthTrend(Math.Round(min, 2), Math.Round(max, 2), Math.Round(delta, 2), Math.Round(slope, 2), stable, Samples.Count, badDeltaLastHour, invalidDeltaLastHour, skippedDeltaLastHour);
    }
}
