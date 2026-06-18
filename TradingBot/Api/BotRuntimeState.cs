using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using TradingBot.Options;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Api;

public class BotRuntimeState
{
    private readonly object _gate = new();
    private readonly RuntimeStateOptions _runtime;
    private long _seq;
    public BotRuntimeState() : this(new RuntimeStateOptions()) { }
    public BotRuntimeState(RuntimeStateOptions runtime) { _runtime = runtime; }
    public BotStatusDto Status { get; private set; } = new("DRY_RUN", false, "DISCONNECTED", 1000, 0, 1000, 0, 0, 0, 0, DateTime.UtcNow, DateTime.UtcNow);
    public ScannerStatsDto ScannerStats { get; private set; } = new(0,0,0,0,0,0,0,0,0,0,0,0,DateTime.UtcNow,DateTime.UtcNow,null,0,0,0,0,0,0,0,DateTime.UtcNow,DateTime.UtcNow,0,0,0,0,0,0,0,0,0,0,0,0,null,0,0,0,0,0,0,0,null,0);
    public TradingBot.Models.OpportunityDiagnosticsSnapshot? OpportunityDiagnostics { get; private set; }
    public MultiOutcomeDiagnosticsDto? MultiOutcomeDiagnostics { get; private set; }
    public object[] MultiOutcomeCandidates { get; private set; } = Array.Empty<object>();
    public object[] MultiOutcomeReviewReport { get; private set; } = Array.Empty<object>();
    public VerifiedBasketScreenerDto? VerifiedBasketScreener { get; private set; }
    public RiskStateDto Risk { get; private set; } = new(100,5,0.003m,0.25m,300,0,5,0,100,new(),true,true,true,true,DateTime.UtcNow,0);
    public BotControlStateDto Controls { get; private set; } = new(false, "RUNNING", DateTime.UtcNow, 0);
    public SingleMarketArbSnapshotDto SingleMarketSnapshot { get; private set; } = new(DateTime.UtcNow, 0, new SingleMarketScanSummaryDto(DateTime.UtcNow, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, "None", 0, new Dictionary<string,int>(), new Dictionary<string,int>()), Array.Empty<SingleMarketArbOpportunityDto>(), Array.Empty<SingleMarketNearMissDto>(), Array.Empty<SingleMarketDataQualityRejectSampleDto>(), Array.Empty<SingleMarketPaperExecutionDto>());
    private readonly ConcurrentQueue<OpportunityDto> _opps = new();
    private readonly ConcurrentQueue<TradeLogEntryDto> _trades = new();
    private readonly ConcurrentQueue<PaperPositionDto> _positions = new();
    private readonly ConcurrentQueue<PaperSettlementRecord> _paperSettlements = new();
    private readonly ConcurrentQueue<TerminalLogEntryDto> _logs = new();
    private readonly Dictionary<string, DateTime> _recentLogDedupe = new(StringComparer.Ordinal);
    private static readonly TimeSpan RecentLogDedupeTtl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentQueue<EquityPointDto> _equity = new();
    private readonly ConcurrentQueue<ScannerStatsDto> _scannerStatsHistory = new();
    private readonly ConcurrentQueue<string> _candidateSnapshots = new();
    private readonly ConcurrentQueue<object> _unresolvedDiagnostics = new();
    private readonly ConcurrentQueue<object> _signalREventBuffer = new();
    private readonly ConcurrentQueue<SingleMarketArbOpportunityDto> _singleMarketOpportunities = new();
    private readonly ConcurrentQueue<SingleMarketPaperExecutionDto> _singleMarketExecutions = new();
    private int _repairHistoryCount;
    private int _dryRunOrderPlansCount;
    private int _fillSimulationsCount;
    private int _executionAuditCount;
    private int _orderbookCacheCount;
    private int _marketCacheCount;
    private int _exportQueueCount;
    private int _patchPreviewItemsCount;
    private int _allowlistHealthy;
    private int _allowlistMonitoringOnly;
    private int _allowlistNeedsPricingPrune;
    private int _allowlistNeedsRefresh;
    private int _allowlistReviewOnly;
    private int _allowlistMismatch;
    private int _allowlistBrokenConfig;
    private int _allowlistDisabled;
    private int _allowlistIgnored;
    private int _allowlistClassificationTotal;
    private int _allowlistClassificationValid;
    private int _allowlistRefreshPreviewCandidates;
    private int _allowlistRefreshHighConfidence;
    private int _allowlistRefreshFinalNoCandidate;
    private int _allowlistRefreshFinalSemanticConflict;
    private int _allowlistRefreshFinalLowConfidence;
    private int _allowlistRefreshFinalUnstable;
    private int _allowlistRefreshFinalPreviewOnly;
    private int _allowlistRefreshFinalLockedManualReview;
    private int _allowlistRefreshActionExplainedSuppressed;
    private int _allowlistRefreshUnstableGroups;
    private int _allowlistRefreshActionFlipFlops;
    private int _discoveryLastHealthySnapshotAgeSeconds;
    private int _discoveryUsingLastHealthySnapshot;
    private int _discoveryPartialAttemptCount;
    private string _discoveryLastFailureReason = string.Empty;
    private int _scannerPausedByDiscoveryGuard;
    private int _discoveryGuardSkippedCycles;
    private int _discoveryGuardUsingLastHealthySnapshot;
    private int _discoveryGuardBlockedNewMarkets;
    private int _discoveryHealthy;
    private int _discoveryStable;
    private int _longRunStable;
    private string _longRunBlockingReason = "DiscoveryInitializing";
    private int _orderbookRecoveredAfterDegradation = 1;
    private DateTime? _lastDegradationUtc;
    private DateTime? _lastRecoveryUtc;
    private int _discoveryBootstrapHealthy;
    private int _discoveryBootstrapRetryCount;
    private DateTime? _discoveryBootstrapLastAttemptUtc;
    private DateTime? _discoveryBootstrapNextRetryUtc;
    private int _discoveryBootstrapBackoffSeconds;
    private string _discoveryBootstrapFailureReason = string.Empty;
    private int _discoveryRetryBackoffSeconds;
    private int _discoveryRetriesSuppressedByBackoff;
    private int _discoveryPersistedSnapshotLoaded;
    private int _discoveryPersistedSnapshotAgeSeconds;
    private int _discoveryPersistedSnapshotActiveMarkets;
    private int _allowlistEvaluationSkipped;
    private string _allowlistEvaluationSkippedReason = string.Empty;
    private int _allowlistClassificationBlockedByDiscovery;
    private string _soakReadiness = "Blocked";
    private string _soakReadinessReason = "DiscoveryInitializing";
    private string _discoveryBlockedReason = "DiscoveryInitializing";
    private string _discoverySelectedSource = "Unknown";
    private int _discoveryScannerSafeSourceAvailable;
    private int _discoverySourceAuditOnly;
    private int _discoverySourceAuditExportWritten;
    private string _discoverySourceAuditExportPath = string.Empty;
    private int _discoverySourceAuditSources;
    private int _discoverySourceAuditScannerSafeSources;
    private string _discoverySourceAuditRecommendedAction = string.Empty;
    private QuietLogGateStats _quietLogGateStats = new(0, 0, new Dictionary<string, long>(), new Dictionary<string, long>(), 0, 0);
    private OrderBookServiceStats _orderBookServiceStats = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    private int _paperPretradeRejects;
    private int _paperDuplicateSuppressions;
    private int _paperInFlightOpens;
    private int _paperDuplicateDedupeEntries;
    private int _paperStaleDedupeEntriesCleared;
    private string[] _paperOpenPositionKeys = Array.Empty<string>();
    private string[] _paperOpenMarketIds = Array.Empty<string>();
    private int _paperExecutionsCount;
    private int _paperOpenEvents;
    private int _paperCloseEvents;
    private int _paperSettlementRejects;
    private int _paperDuplicateSettlementSuppressions;
    private readonly object _paperCountersGate = new();
    private readonly Dictionary<string, int> _paperPretradeRejectsByReason = new(StringComparer.OrdinalIgnoreCase);
    private int _memoryWarnings;
    private int _memoryCriticals;
    private DateTime? _lastMemoryCriticalAt;
    private bool _scannerPausedByMemoryGuard;
    private readonly ConcurrentDictionary<string, StrategyRuntimeCounters> _strategyCounters = new(StringComparer.OrdinalIgnoreCase);

    private static void Trim<T>(ConcurrentQueue<T> q,int max){ var capped = Math.Max(0, max); while(q.Count>capped) q.TryDequeue(out _); }
    private void TrimAll()
    {
        Trim(_logs,_runtime.MaxRecentLogs);
        Trim(_scannerStatsHistory, Math.Min(_runtime.MaxScannerStatsHistory, _runtime.MaxScannerHistory));
        Trim(_candidateSnapshots,_runtime.MaxCandidateSnapshots);
        Trim(_unresolvedDiagnostics,_runtime.MaxUnresolvedDiagnostics);
        Trim(_signalREventBuffer,_runtime.MaxSignalREventBuffer);
        Trim(_opps,_runtime.MaxCandidateGroupsInMemory);
        Trim(_positions,_runtime.MaxPaperPositions);
        Trim(_equity,500);
        Trim(_trades,500);
        Trim(_singleMarketOpportunities,_runtime.MaxSingleMarketOpportunities);
        Trim(_singleMarketExecutions,_runtime.MaxSingleMarketExecutions);
    }

    public void RecordStrategyResult(OpportunityStrategyScanResult result)
    {
        _strategyCounters.GetOrAdd(result.StrategyName, _ => new StrategyRuntimeCounters()).Add(result);
    }

    public IReadOnlyDictionary<string, StrategyRuntimeCounterSnapshot> StrategyCountersSnapshot()
        => _strategyCounters.ToDictionary(x => x.Key, x => x.Value.Snapshot(x.Key), StringComparer.OrdinalIgnoreCase);

    public int ScannerStatsHistoryCount => _scannerStatsHistory.Count;
    public int CandidateSnapshotCount => _candidateSnapshots.Count;
    public int RepairHistoryCount => Volatile.Read(ref _repairHistoryCount);
    public int UnresolvedDiagnosticsCount => _unresolvedDiagnostics.Count;
    public int DryRunOrderPlansCount => Volatile.Read(ref _dryRunOrderPlansCount);
    public int FillSimulationsCount => Volatile.Read(ref _fillSimulationsCount);
    public int ExecutionAuditCount => Volatile.Read(ref _executionAuditCount);
    public int SignalREventBufferCount => _signalREventBuffer.Count;
    public int OrderbookCacheCount => Volatile.Read(ref _orderbookCacheCount);
    public int MarketCacheCount => Volatile.Read(ref _marketCacheCount);
    public int ExportQueueCount => Volatile.Read(ref _exportQueueCount);
    public int PatchPreviewItemsCount => Volatile.Read(ref _patchPreviewItemsCount);
    public int AllowlistHealthy => Volatile.Read(ref _allowlistHealthy);
    public int AllowlistMonitoringOnly => Volatile.Read(ref _allowlistMonitoringOnly);
    public int AllowlistNeedsPricingPrune => Volatile.Read(ref _allowlistNeedsPricingPrune);
    public int AllowlistNeedsRefresh => Volatile.Read(ref _allowlistNeedsRefresh);
    public int AllowlistReviewOnly => Volatile.Read(ref _allowlistReviewOnly);
    public int AllowlistMismatch => Volatile.Read(ref _allowlistMismatch);
    public int AllowlistBrokenConfig => Volatile.Read(ref _allowlistBrokenConfig);
    public int AllowlistDisabled => Volatile.Read(ref _allowlistDisabled);
    public int AllowlistIgnored => Volatile.Read(ref _allowlistIgnored);
    public int AllowlistClassificationTotal => Volatile.Read(ref _allowlistClassificationTotal);
    public bool AllowlistClassificationValid => Volatile.Read(ref _allowlistClassificationValid) == 1;
    public int AllowlistRefreshPreviewCandidates => Volatile.Read(ref _allowlistRefreshPreviewCandidates);
    public int AllowlistRefreshHighConfidence => Volatile.Read(ref _allowlistRefreshHighConfidence);
    public int AllowlistRefreshFinalNoCandidate => Volatile.Read(ref _allowlistRefreshFinalNoCandidate);
    public int AllowlistRefreshFinalSemanticConflict => Volatile.Read(ref _allowlistRefreshFinalSemanticConflict);
    public int AllowlistRefreshFinalLowConfidence => Volatile.Read(ref _allowlistRefreshFinalLowConfidence);
    public int AllowlistRefreshFinalUnstable => Volatile.Read(ref _allowlistRefreshFinalUnstable);
    public int AllowlistRefreshFinalPreviewOnly => Volatile.Read(ref _allowlistRefreshFinalPreviewOnly);
    public int AllowlistRefreshFinalLockedManualReview => Volatile.Read(ref _allowlistRefreshFinalLockedManualReview);
    public int AllowlistRefreshActionExplainedSuppressed => Volatile.Read(ref _allowlistRefreshActionExplainedSuppressed);
    public int AllowlistRefreshUnstableGroups => Volatile.Read(ref _allowlistRefreshUnstableGroups);
    public int AllowlistRefreshActionFlipFlops => Volatile.Read(ref _allowlistRefreshActionFlipFlops);
    public int DiscoveryLastHealthySnapshotAgeSeconds => Volatile.Read(ref _discoveryLastHealthySnapshotAgeSeconds);
    public bool DiscoveryUsingLastHealthySnapshot => Volatile.Read(ref _discoveryUsingLastHealthySnapshot) == 1;
    public int DiscoveryPartialAttemptCount => Volatile.Read(ref _discoveryPartialAttemptCount);
    public string DiscoveryLastFailureReason => _discoveryLastFailureReason;
    public bool ScannerPausedByDiscoveryGuard => Volatile.Read(ref _scannerPausedByDiscoveryGuard) == 1;
    public int DiscoveryGuardSkippedCycles => Volatile.Read(ref _discoveryGuardSkippedCycles);
    public bool DiscoveryGuardUsingLastHealthySnapshot => Volatile.Read(ref _discoveryGuardUsingLastHealthySnapshot) == 1;
    public int DiscoveryGuardBlockedNewMarkets => Volatile.Read(ref _discoveryGuardBlockedNewMarkets);
    public bool DiscoveryHealthy => Volatile.Read(ref _discoveryHealthy) == 1;
    public bool DiscoveryStable => Volatile.Read(ref _discoveryStable) == 1;
    public bool LongRunStable => Volatile.Read(ref _longRunStable) == 1;
    public string LongRunBlockingReason => _longRunBlockingReason;
    public bool OrderbookRecoveredAfterDegradation => Volatile.Read(ref _orderbookRecoveredAfterDegradation) == 1;
    public DateTime? LastDegradationUtc => _lastDegradationUtc;
    public DateTime? LastRecoveryUtc => _lastRecoveryUtc;
    public bool DiscoveryBootstrapHealthy => Volatile.Read(ref _discoveryBootstrapHealthy) == 1;
    public int DiscoveryBootstrapRetryCount => Volatile.Read(ref _discoveryBootstrapRetryCount);
    public DateTime? DiscoveryBootstrapLastAttemptUtc => _discoveryBootstrapLastAttemptUtc;
    public DateTime? DiscoveryBootstrapNextRetryUtc => _discoveryBootstrapNextRetryUtc;
    public int DiscoveryBootstrapBackoffSeconds => Volatile.Read(ref _discoveryBootstrapBackoffSeconds);
    public string DiscoveryBootstrapFailureReason => _discoveryBootstrapFailureReason;
    public int DiscoveryRetryBackoffSeconds => Volatile.Read(ref _discoveryRetryBackoffSeconds);
    public int DiscoveryRetriesSuppressedByBackoff => Volatile.Read(ref _discoveryRetriesSuppressedByBackoff);
    public bool DiscoveryPersistedSnapshotLoaded => Volatile.Read(ref _discoveryPersistedSnapshotLoaded) == 1;
    public int DiscoveryPersistedSnapshotAgeSeconds => Volatile.Read(ref _discoveryPersistedSnapshotAgeSeconds);
    public int DiscoveryPersistedSnapshotActiveMarkets => Volatile.Read(ref _discoveryPersistedSnapshotActiveMarkets);
    public bool AllowlistEvaluationSkipped => Volatile.Read(ref _allowlistEvaluationSkipped) == 1;
    public string AllowlistEvaluationSkippedReason => _allowlistEvaluationSkippedReason;
    public bool AllowlistClassificationBlockedByDiscovery => Volatile.Read(ref _allowlistClassificationBlockedByDiscovery) == 1;
    public string SoakReadiness => _soakReadiness;
    public string SoakReadinessReason => _soakReadinessReason;
    public string DiscoveryBlockedReason => _discoveryBlockedReason;
    public string DiscoverySelectedSource => _discoverySelectedSource;
    public bool DiscoveryScannerSafeSourceAvailable => Volatile.Read(ref _discoveryScannerSafeSourceAvailable) == 1;
    public bool DiscoverySourceAuditOnly => Volatile.Read(ref _discoverySourceAuditOnly) == 1;
    public bool DiscoverySourceAuditExportWritten => Volatile.Read(ref _discoverySourceAuditExportWritten) == 1;
    public string DiscoverySourceAuditExportPath => _discoverySourceAuditExportPath;
    public int DiscoverySourceAuditSources => Volatile.Read(ref _discoverySourceAuditSources);
    public int DiscoverySourceAuditScannerSafeSources => Volatile.Read(ref _discoverySourceAuditScannerSafeSources);
    public string DiscoverySourceAuditRecommendedAction => _discoverySourceAuditRecommendedAction;
    public QuietLogGateStats QuietLogGateStats => _quietLogGateStats;
    public OrderBookServiceStats OrderBookServiceStats => _orderBookServiceStats;
    public string ProcessRunId => ProcessRunContext.ProcessRunId;
    public string ScannerInstanceId => ProcessRunContext.ScannerInstanceId;
    public DateTime StartedAtUtc => ProcessRunContext.StartedAtUtc;
    public long DiagnosticsCounterMismatchCount => ProcessRunContext.DiagnosticsCounterMismatchCount;
    public string DiagnosticsCounterMismatchLastReason => ProcessRunContext.DiagnosticsCounterMismatchLastReason;
    public int SingleMarketOpportunitiesCount => _singleMarketOpportunities.Count;
    public int SingleMarketExecutionsCount => _singleMarketExecutions.Count;
    public int PaperOpenPositions => Status.OpenPositions;
    public int PaperClosedPositions => Positions().Count(p => string.Equals(p.Status, "CLOSED", StringComparison.OrdinalIgnoreCase));
    public int PaperSettlements => _paperSettlements.Count;
    public decimal PaperRealizedPnl => Status.RealizedPnl;
    public decimal PaperLocked => Status.LockedCapital;
    public decimal PaperCash => Status.Cash;
    public decimal PaperEquity => Status.Equity;
    public decimal PaperTotalExposure => Status.LockedCapital;
    public int PaperOpenCountLastHour { get; private set; }
    public int PaperPretradeRejects => Volatile.Read(ref _paperPretradeRejects);
    public int PaperDuplicateSuppressions => Volatile.Read(ref _paperDuplicateSuppressions);
    public int PaperInFlightOpens => Volatile.Read(ref _paperInFlightOpens);
    public int PaperDuplicateDedupeEntries => Volatile.Read(ref _paperDuplicateDedupeEntries);
    public int PaperStaleDedupeEntriesCleared => Volatile.Read(ref _paperStaleDedupeEntriesCleared);
    public IReadOnlyList<string> PaperOpenPositionKeys => _paperOpenPositionKeys;
    public IReadOnlyList<string> PaperOpenMarketIds => _paperOpenMarketIds;
    public int PaperSettlementRejects => Volatile.Read(ref _paperSettlementRejects);
    public int PaperDuplicateSettlementSuppressions => Volatile.Read(ref _paperDuplicateSettlementSuppressions);
    public int PaperOpenEvents => Volatile.Read(ref _paperOpenEvents);
    public int PaperCloseEvents => Volatile.Read(ref _paperCloseEvents);
    public int PaperLifecycleEvents => PaperOpenEvents + PaperCloseEvents;
    public long LiveTradingBlockedCount => TradingBot.Services.LiveTradingGuard.BlockedCount;
    public int PaperExecutionsCount => Math.Max(PaperOpenEvents, SingleMarketExecutionsCount) + Volatile.Read(ref _paperExecutionsCount);
    public int MemoryWarnings => Volatile.Read(ref _memoryWarnings);
    public int MemoryCriticals => Volatile.Read(ref _memoryCriticals);
    public DateTime? LastMemoryCriticalAt => _lastMemoryCriticalAt;
    public bool ScannerPausedByMemoryGuard => _scannerPausedByMemoryGuard;
    public IReadOnlyDictionary<string, int> PaperPretradeRejectsByReason { get { lock (_paperCountersGate) return new Dictionary<string, int>(_paperPretradeRejectsByReason, StringComparer.OrdinalIgnoreCase); } }
    public long NextSeq()=>Interlocked.Increment(ref _seq);
    public void SetStatus(BotStatusDto s){lock(_gate) Status=s;}
    public void SetScannerStats(ScannerStatsDto s){lock(_gate) ScannerStats=s; _scannerStatsHistory.Enqueue(s); Trim(_scannerStatsHistory, Math.Min(_runtime.MaxScannerStatsHistory, _runtime.MaxScannerHistory));}
    public void SetRisk(RiskStateDto r){lock(_gate) Risk=r;}
    public void SetOpportunityDiagnostics(TradingBot.Models.OpportunityDiagnosticsSnapshot? d){lock(_gate) OpportunityDiagnostics=d;}
    public void SetMultiOutcomeDiagnostics(MultiOutcomeDiagnosticsDto? d){lock(_gate) MultiOutcomeDiagnostics=d;}
    public void SetMultiOutcomeCandidates(IEnumerable<object> items){lock(_gate) { MultiOutcomeCandidates = items.Take(_runtime.MaxCandidateGroupsInMemory).ToArray(); _candidateSnapshots.Enqueue(DateTime.UtcNow.ToString("O")); Trim(_candidateSnapshots,_runtime.MaxCandidateSnapshots); }}
    public void SetMultiOutcomeReviewReport(IEnumerable<object> items){lock(_gate) MultiOutcomeReviewReport = items.Take(_runtime.MaxRejectedCandidateSamples).ToArray();}
    public void SetVerifiedBasketScreener(VerifiedBasketScreenerDto? d){lock(_gate) VerifiedBasketScreener=d;}
    public void SetControls(BotControlStateDto c){lock(_gate) Controls=c;}
    public void SetSingleMarketSnapshot(SingleMarketArbSnapshotDto snapshot)
    {
        lock(_gate)
        {
            SingleMarketSnapshot = snapshot with
            {
                PositiveCandidates = snapshot.PositiveCandidates.Take(_runtime.MaxSingleMarketOpportunities).ToArray(),
                TopNearMisses = snapshot.TopNearMisses.Take(_runtime.MaxSingleMarketNearMisses).ToArray(),
                DataQualityRejectSamples = snapshot.DataQualityRejectSamples.Take(_runtime.MaxSingleMarketDataQualitySamples).ToArray(),
                PaperExecutions = snapshot.PaperExecutions.Take(_runtime.MaxSingleMarketExecutions).ToArray()
            };
        }
    }
    public void AddOpportunity(OpportunityDto o){_opps.Enqueue(o); Trim(_opps,_runtime.MaxCandidateGroupsInMemory);}
    public void ReplaceOpportunities(IEnumerable<OpportunityDto> items){while(_opps.TryDequeue(out _)){} foreach(var i in items.Take(_runtime.MaxCandidateGroupsInMemory)) _opps.Enqueue(i); Trim(_opps,_runtime.MaxCandidateGroupsInMemory);}
    public void AddTrade(TradeLogEntryDto t){_trades.Enqueue(t); Trim(_trades,500);}
    public void ReplaceTrades(IEnumerable<TradeLogEntryDto> items){while(_trades.TryDequeue(out _)){} foreach(var i in items.Take(500)) _trades.Enqueue(i); Trim(_trades,500);}
    public void AddPosition(PaperPositionDto p){_positions.Enqueue(p); Trim(_positions,_runtime.MaxPaperPositions);}
    public void ReplacePositions(IEnumerable<PaperPositionDto> items)
    {
        while(_positions.TryDequeue(out _)){}
        var materialized = items.Take(_runtime.MaxPaperPositions).ToArray();
        foreach(var i in materialized) _positions.Enqueue(i);
        Trim(_positions,_runtime.MaxPaperPositions);
        var openEvents = materialized.Count(p => p.Status.Equals("OPEN", StringComparison.OrdinalIgnoreCase) || p.Status.Equals("CLOSED", StringComparison.OrdinalIgnoreCase) || p.Status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase));
        var closeEvents = materialized.Count(p => p.Status.Equals("CLOSED", StringComparison.OrdinalIgnoreCase));
        Interlocked.Exchange(ref _paperOpenEvents, openEvents);
        Interlocked.Exchange(ref _paperCloseEvents, closeEvents);
    }
    public void ReplacePaperSettlements(IEnumerable<PaperSettlementRecord> items){while(_paperSettlements.TryDequeue(out _)){} foreach(var i in items.Take(500)) _paperSettlements.Enqueue(i); Trim(_paperSettlements,500);}
    public void ClearTransientLogBuffers()
    {
        while (_logs.TryDequeue(out _)) { }
        while (_signalREventBuffer.TryDequeue(out _)) { }
        lock (_recentLogDedupe) _recentLogDedupe.Clear();
    }

    public bool AddLog(TerminalLogEntryDto l)
    {
        if (!IsCriticalLog(l) && IsDuplicateRecentLog(l)) return false;
        _logs.Enqueue(l);
        Trim(_logs,_runtime.MaxRecentLogs);
        return true;
    }
    public void AddEquity(EquityPointDto e){_equity.Enqueue(e); Trim(_equity,500);}
    public void AddSingleMarketOpportunity(SingleMarketArbOpportunityDto o){_singleMarketOpportunities.Enqueue(o); Trim(_singleMarketOpportunities,_runtime.MaxSingleMarketOpportunities);}
    public void AddSingleMarketExecution(SingleMarketPaperExecutionDto e){_singleMarketExecutions.Enqueue(e); Trim(_singleMarketExecutions,_runtime.MaxSingleMarketExecutions);}
    public void ReplaceSingleMarketOpportunities(IEnumerable<SingleMarketArbOpportunityDto> items){while(_singleMarketOpportunities.TryDequeue(out _)){} foreach(var i in items.Take(_runtime.MaxSingleMarketOpportunities)) _singleMarketOpportunities.Enqueue(i); Trim(_singleMarketOpportunities,_runtime.MaxSingleMarketOpportunities);}
    public void ReplaceSingleMarketExecutions(IEnumerable<SingleMarketPaperExecutionDto> items){while(_singleMarketExecutions.TryDequeue(out _)){} foreach(var i in items.Take(_runtime.MaxSingleMarketExecutions)) _singleMarketExecutions.Enqueue(i); Trim(_singleMarketExecutions,_runtime.MaxSingleMarketExecutions);}
    public void AddSignalREvent(string eventName){_signalREventBuffer.Enqueue($"{DateTime.UtcNow:O}|{eventName}"); Trim(_signalREventBuffer,_runtime.MaxSignalREventBuffer);}
    public void AddUnresolvedDiagnostics(IEnumerable<object> items){foreach(var item in items.Take(_runtime.MaxUnresolvedDiagnostics)) _unresolvedDiagnostics.Enqueue(item); Trim(_unresolvedDiagnostics,_runtime.MaxUnresolvedDiagnostics);}
    public void SetQuietLogGateStats(QuietLogGateStats stats) => _quietLogGateStats = stats;
    public void SetOrderBookServiceStats(OrderBookServiceStats stats) => _orderBookServiceStats = stats;
    public void RecordMemoryWarning() => Interlocked.Increment(ref _memoryWarnings);
    public void RecordMemoryCritical(DateTime whenUtc, bool scannerPaused)
    {
        Interlocked.Increment(ref _memoryCriticals);
        _lastMemoryCriticalAt = whenUtc;
        _scannerPausedByMemoryGuard = scannerPaused;
    }
    public void SetScannerPausedByMemoryGuard(bool paused) => _scannerPausedByMemoryGuard = paused;
    public void RecordPaperPretradeReject(string reason)
    {
        Interlocked.Increment(ref _paperPretradeRejects);
        lock (_paperCountersGate) _paperPretradeRejectsByReason[reason] = _paperPretradeRejectsByReason.TryGetValue(reason, out var c) ? c + 1 : 1;
        if (reason.Equals("DuplicateOpenPosition", StringComparison.OrdinalIgnoreCase)) RecordPaperDuplicateSuppression();
    }
    public void RecordPaperDuplicateSuppression() => Interlocked.Increment(ref _paperDuplicateSuppressions);
    public void SetPaperInFlightOpens(int count) => Interlocked.Exchange(ref _paperInFlightOpens, count);
    public void SetPaperDuplicateDedupeEntries(int count) => Interlocked.Exchange(ref _paperDuplicateDedupeEntries, count);
    public void RecordPaperStaleDedupeEntryCleared() => Interlocked.Increment(ref _paperStaleDedupeEntriesCleared);
    public void SetPaperOpenPositionKeys(IEnumerable<string> keys) => _paperOpenPositionKeys = keys.Take(20).ToArray();
    public void SetPaperOpenMarketIds(IEnumerable<string> marketIds) => _paperOpenMarketIds = marketIds.Take(20).ToArray();
    public void SetPaperOpenCountLastHour(int count) => PaperOpenCountLastHour = count;
    public void RecordPaperExecution() => Interlocked.Increment(ref _paperExecutionsCount);
    public void SetPaperSettlementCounters(int rejects, int duplicateSuppressions) { Interlocked.Exchange(ref _paperSettlementRejects, rejects); Interlocked.Exchange(ref _paperDuplicateSettlementSuppressions, duplicateSuppressions); }

    public void SetRuntimeCounts(int? repairHistoryCount = null, int? dryRunOrderPlansCount = null, int? fillSimulationsCount = null, int? executionAuditCount = null, int? orderbookCacheCount = null, int? marketCacheCount = null, int? exportQueueCount = null, int? patchPreviewItemsCount = null)
    {
        if (repairHistoryCount is int rh) Interlocked.Exchange(ref _repairHistoryCount, rh);
        if (dryRunOrderPlansCount is int drp) Interlocked.Exchange(ref _dryRunOrderPlansCount, drp);
        if (fillSimulationsCount is int fs) Interlocked.Exchange(ref _fillSimulationsCount, fs);
        if (executionAuditCount is int ea) Interlocked.Exchange(ref _executionAuditCount, ea);
        if (orderbookCacheCount is int ob) Interlocked.Exchange(ref _orderbookCacheCount, ob);
        if (marketCacheCount is int mc) Interlocked.Exchange(ref _marketCacheCount, mc);
        if (exportQueueCount is int eq) Interlocked.Exchange(ref _exportQueueCount, eq);
        if (patchPreviewItemsCount is int pp) Interlocked.Exchange(ref _patchPreviewItemsCount, pp);
    }
    public void ApplyReadinessInvariantCorrections(bool sourceAuditOnly)
    {
        var reasons = new List<string>();
        var scannerSafe = DiscoveryScannerSafeSourceAvailable;
        if (sourceAuditOnly && !scannerSafe)
        {
            if (!SoakReadiness.Equals("Blocked", StringComparison.OrdinalIgnoreCase) || !SoakReadinessReason.Equals("SourceAuditOnly", StringComparison.OrdinalIgnoreCase))
                reasons.Add("SourceAuditOnlyRequiresBlockedReadiness");
            _soakReadiness = "Blocked";
            _soakReadinessReason = "SourceAuditOnly";
            _discoveryBlockedReason = "SourceAuditOnly";
            _discoverySelectedSource = "Blocked";
            Interlocked.Exchange(ref _discoverySourceAuditOnly, 1);
            Interlocked.Exchange(ref _scannerPausedByDiscoveryGuard, 1);
            Interlocked.Exchange(ref _allowlistEvaluationSkipped, 1);
            _allowlistEvaluationSkippedReason = "SourceAuditOnly";
            Interlocked.Exchange(ref _allowlistClassificationBlockedByDiscovery, 1);
        }
        if (!DiscoveryHealthy)
        {
            if (DiscoveryStable || LongRunStable) reasons.Add("UnhealthyDiscoveryRequiresUnstableRuntime");
            Interlocked.Exchange(ref _discoveryStable, 0);
            Interlocked.Exchange(ref _longRunStable, 0);
            if (string.IsNullOrWhiteSpace(_longRunBlockingReason) || _longRunBlockingReason == "None") _longRunBlockingReason = "DiscoveryUnavailable";
        }
        if (DiscoverySelectedSource.Equals("Blocked", StringComparison.OrdinalIgnoreCase) && SoakReadiness.Equals("Ready", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("BlockedSourceCannotBeReady");
            _soakReadiness = "Blocked";
            if (string.IsNullOrWhiteSpace(_soakReadinessReason) || _soakReadinessReason == "None") _soakReadinessReason = _discoveryBlockedReason == "None" ? "NoScannerSafeDiscoverySource" : _discoveryBlockedReason;
        }
        if (reasons.Count > 0) ProcessRunContext.RecordReadinessInvariantCorrection(string.Join("|", reasons.Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    public void SetDiscoveryGuardState(bool discoveryHealthy, bool discoveryStable, bool usingLastHealthySnapshot, int lastHealthySnapshotAgeSeconds, int partialAttemptCount, string? lastFailureReason, bool scannerPausedByDiscoveryGuard, int discoveryGuardSkippedCycles, bool discoveryGuardUsingLastHealthySnapshot, int discoveryGuardBlockedNewMarkets, bool longRunStable, string? longRunBlockingReason, bool orderbookRecoveredAfterDegradation, DateTime? lastDegradationUtc, DateTime? lastRecoveryUtc, bool discoveryBootstrapHealthy = false, int discoveryBootstrapRetryCount = 0, DateTime? discoveryBootstrapLastAttemptUtc = null, DateTime? discoveryBootstrapNextRetryUtc = null, int discoveryBootstrapBackoffSeconds = 0, string? discoveryBootstrapFailureReason = null, int discoveryRetryBackoffSeconds = 0, int discoveryRetriesSuppressedByBackoff = 0, bool discoveryPersistedSnapshotLoaded = false, int discoveryPersistedSnapshotAgeSeconds = 0, int discoveryPersistedSnapshotActiveMarkets = 0, bool allowlistEvaluationSkipped = false, string? allowlistEvaluationSkippedReason = null, bool allowlistClassificationBlockedByDiscovery = false, string? soakReadiness = null, string? soakReadinessReason = null, string? discoveryBlockedReason = null, string? discoverySelectedSource = null, bool discoveryScannerSafeSourceAvailable = false, bool discoverySourceAuditOnly = false, bool discoverySourceAuditExportWritten = false, string? discoverySourceAuditExportPath = null, int discoverySourceAuditSources = 0, int discoverySourceAuditScannerSafeSources = 0, string? discoverySourceAuditRecommendedAction = null)
    {
        Interlocked.Exchange(ref _discoveryHealthy, discoveryHealthy ? 1 : 0);
        Interlocked.Exchange(ref _discoveryStable, discoveryStable ? 1 : 0);
        Interlocked.Exchange(ref _discoveryUsingLastHealthySnapshot, usingLastHealthySnapshot ? 1 : 0);
        Interlocked.Exchange(ref _discoveryLastHealthySnapshotAgeSeconds, Math.Max(0, lastHealthySnapshotAgeSeconds));
        Interlocked.Exchange(ref _discoveryPartialAttemptCount, Math.Max(0, partialAttemptCount));
        _discoveryLastFailureReason = lastFailureReason ?? string.Empty;
        Interlocked.Exchange(ref _scannerPausedByDiscoveryGuard, scannerPausedByDiscoveryGuard ? 1 : 0);
        Interlocked.Exchange(ref _discoveryGuardSkippedCycles, Math.Max(0, discoveryGuardSkippedCycles));
        Interlocked.Exchange(ref _discoveryGuardUsingLastHealthySnapshot, discoveryGuardUsingLastHealthySnapshot ? 1 : 0);
        Interlocked.Exchange(ref _discoveryGuardBlockedNewMarkets, Math.Max(0, discoveryGuardBlockedNewMarkets));
        Interlocked.Exchange(ref _longRunStable, longRunStable ? 1 : 0);
        _longRunBlockingReason = longRunBlockingReason ?? string.Empty;
        Interlocked.Exchange(ref _orderbookRecoveredAfterDegradation, orderbookRecoveredAfterDegradation ? 1 : 0);
        _lastDegradationUtc = lastDegradationUtc;
        _lastRecoveryUtc = lastRecoveryUtc;
        Interlocked.Exchange(ref _discoveryBootstrapHealthy, discoveryBootstrapHealthy ? 1 : 0);
        Interlocked.Exchange(ref _discoveryBootstrapRetryCount, Math.Max(0, discoveryBootstrapRetryCount));
        _discoveryBootstrapLastAttemptUtc = discoveryBootstrapLastAttemptUtc;
        _discoveryBootstrapNextRetryUtc = discoveryBootstrapNextRetryUtc;
        Interlocked.Exchange(ref _discoveryBootstrapBackoffSeconds, Math.Max(0, discoveryBootstrapBackoffSeconds));
        _discoveryBootstrapFailureReason = discoveryBootstrapFailureReason ?? string.Empty;
        Interlocked.Exchange(ref _discoveryRetryBackoffSeconds, Math.Max(0, discoveryRetryBackoffSeconds));
        Interlocked.Exchange(ref _discoveryRetriesSuppressedByBackoff, Math.Max(0, discoveryRetriesSuppressedByBackoff));
        Interlocked.Exchange(ref _discoveryPersistedSnapshotLoaded, discoveryPersistedSnapshotLoaded ? 1 : 0);
        Interlocked.Exchange(ref _discoveryPersistedSnapshotAgeSeconds, Math.Max(0, discoveryPersistedSnapshotAgeSeconds));
        Interlocked.Exchange(ref _discoveryPersistedSnapshotActiveMarkets, Math.Max(0, discoveryPersistedSnapshotActiveMarkets));
        Interlocked.Exchange(ref _allowlistEvaluationSkipped, allowlistEvaluationSkipped ? 1 : 0);
        _allowlistEvaluationSkippedReason = allowlistEvaluationSkippedReason ?? string.Empty;
        Interlocked.Exchange(ref _allowlistClassificationBlockedByDiscovery, allowlistClassificationBlockedByDiscovery ? 1 : 0);
        _soakReadiness = soakReadiness ?? "Ready";
        _soakReadinessReason = soakReadinessReason ?? "None";
        _discoveryBlockedReason = discoveryBlockedReason ?? "None";
        _discoverySelectedSource = discoverySelectedSource ?? "Unknown";
        Interlocked.Exchange(ref _discoveryScannerSafeSourceAvailable, discoveryScannerSafeSourceAvailable ? 1 : 0);
        Interlocked.Exchange(ref _discoverySourceAuditOnly, discoverySourceAuditOnly ? 1 : 0);
        Interlocked.Exchange(ref _discoverySourceAuditExportWritten, discoverySourceAuditExportWritten ? 1 : 0);
        _discoverySourceAuditExportPath = discoverySourceAuditExportPath ?? string.Empty;
        Interlocked.Exchange(ref _discoverySourceAuditSources, Math.Max(0, discoverySourceAuditSources));
        Interlocked.Exchange(ref _discoverySourceAuditScannerSafeSources, Math.Max(0, discoverySourceAuditScannerSafeSources));
        _discoverySourceAuditRecommendedAction = discoverySourceAuditRecommendedAction ?? string.Empty;
    }

    public void SetAllowlistRefreshCounters(int needsRefresh, int reviewOnly, int mismatch, int refreshPreviewCandidates, int highConfidence, int finalNoCandidate = 0, int finalSemanticConflict = 0, int finalLowConfidence = 0, int finalUnstable = 0, int finalPreviewOnly = 0, int finalLockedManualReview = 0, int actionExplainedSuppressed = 0, int unstableGroups = 0, int actionFlipFlops = 0, int healthy = 0, int monitoringOnly = 0, int needsPricingPrune = 0, int brokenConfig = 0, int disabled = 0, int ignored = 0, int classificationTotal = 0, bool classificationValid = true)
    {
        Interlocked.Exchange(ref _allowlistHealthy, healthy);
        Interlocked.Exchange(ref _allowlistMonitoringOnly, monitoringOnly);
        Interlocked.Exchange(ref _allowlistNeedsPricingPrune, needsPricingPrune);
        Interlocked.Exchange(ref _allowlistNeedsRefresh, needsRefresh);
        Interlocked.Exchange(ref _allowlistReviewOnly, reviewOnly);
        Interlocked.Exchange(ref _allowlistMismatch, mismatch);
        Interlocked.Exchange(ref _allowlistRefreshPreviewCandidates, refreshPreviewCandidates);
        Interlocked.Exchange(ref _allowlistRefreshHighConfidence, highConfidence);
        Interlocked.Exchange(ref _allowlistRefreshFinalNoCandidate, finalNoCandidate);
        Interlocked.Exchange(ref _allowlistRefreshFinalSemanticConflict, finalSemanticConflict);
        Interlocked.Exchange(ref _allowlistRefreshFinalLowConfidence, finalLowConfidence);
        Interlocked.Exchange(ref _allowlistRefreshFinalUnstable, finalUnstable);
        Interlocked.Exchange(ref _allowlistRefreshFinalPreviewOnly, finalPreviewOnly);
        SetMax(ref _allowlistRefreshFinalLockedManualReview, finalLockedManualReview);
        Interlocked.Exchange(ref _allowlistRefreshActionExplainedSuppressed, actionExplainedSuppressed);
        SetMax(ref _allowlistRefreshUnstableGroups, unstableGroups);
        SetMax(ref _allowlistRefreshActionFlipFlops, actionFlipFlops);
        Interlocked.Exchange(ref _allowlistBrokenConfig, brokenConfig);
        Interlocked.Exchange(ref _allowlistDisabled, disabled);
        Interlocked.Exchange(ref _allowlistIgnored, ignored);
        Interlocked.Exchange(ref _allowlistClassificationTotal, classificationTotal);
        Interlocked.Exchange(ref _allowlistClassificationValid, classificationValid ? 1 : 0);
    }

    private static void SetMax(ref int location, int value)
    {
        var current = Volatile.Read(ref location);
        while (value > current)
        {
            var observed = Interlocked.CompareExchange(ref location, value, current);
            if (observed == current) return;
            current = observed;
        }
    }

    public IReadOnlyDictionary<string,int> GetRuntimeCollectionCounts() => new Dictionary<string,int>
    {
        ["recentLogs"] = Logs().Length,
        ["scannerHistory"] = ScannerStatsHistoryCount,
        ["candidateSnapshots"] = CandidateSnapshotCount,
        ["repairHistory"] = RepairHistoryCount,
        ["unresolvedDiagnostics"] = UnresolvedDiagnosticsCount,
        ["dryRunOrderPlans"] = DryRunOrderPlansCount,
        ["fillSimulations"] = FillSimulationsCount,
        ["executionAudit"] = ExecutionAuditCount,
        ["signalREventBuffer"] = SignalREventBufferCount,
        ["orderbookCache"] = OrderbookCacheCount,
        ["marketCache"] = MarketCacheCount,
        ["exports"] = ExportQueueCount,
        ["patchPreviewItems"] = PatchPreviewItemsCount,
        ["singleMarketOpportunities"] = SingleMarketOpportunitiesCount,
        ["singleMarketNearMisses"] = SingleMarketSnapshot.TopNearMisses.Count,
        ["singleMarketDataQualitySamples"] = SingleMarketSnapshot.DataQualityRejectSamples.Count,
        ["singleMarketExecutions"] = SingleMarketExecutionsCount,
        ["paperOpenPositions"] = PaperOpenPositions,
        ["paperOpenCountLastHour"] = PaperOpenCountLastHour,
        ["paperPretradeRejects"] = PaperPretradeRejects,
        ["paperDuplicateSuppressions"] = PaperDuplicateSuppressions,
        ["paperInFlightOpens"] = PaperInFlightOpens,
        ["paperDuplicateDedupeEntries"] = PaperDuplicateDedupeEntries,
        ["paperStaleDedupeEntriesCleared"] = PaperStaleDedupeEntriesCleared,
        ["paperExecutionsCount"] = PaperExecutionsCount,
        ["paperLifecycleEvents"] = PaperLifecycleEvents,
        ["paperOpenEvents"] = PaperOpenEvents,
        ["paperCloseEvents"] = PaperCloseEvents,
        ["paperClosedPositions"] = PaperClosedPositions,
        ["paperSettlements"] = PaperSettlements,
        ["paperSettlementRejects"] = PaperSettlementRejects,
        ["paperDuplicateSettlementSuppressions"] = PaperDuplicateSettlementSuppressions,
        ["memoryWarnings"] = MemoryWarnings,
        ["memoryCriticals"] = MemoryCriticals,
        ["scannerPausedByMemoryGuard"] = ScannerPausedByMemoryGuard ? 1 : 0
    };
    public void ClearNonEssentialRuntimeState()
    {
        while(_unresolvedDiagnostics.TryDequeue(out _)){}
        while(_candidateSnapshots.TryDequeue(out _)){}
        while(_scannerStatsHistory.Count > Math.Min(50, _runtime.MaxScannerHistory) && _scannerStatsHistory.TryDequeue(out _)){}
        while(_logs.Count > Math.Min(100, _runtime.MaxRecentLogs) && _logs.TryDequeue(out _)){}
        while(_signalREventBuffer.TryDequeue(out _)){}
        while(_singleMarketOpportunities.TryDequeue(out _)){}
        while(_singleMarketExecutions.TryDequeue(out _)){}
        SetSingleMarketSnapshot(SingleMarketSnapshot with { PositiveCandidates = Array.Empty<SingleMarketArbOpportunityDto>(), TopNearMisses = Array.Empty<SingleMarketNearMissDto>(), DataQualityRejectSamples = Array.Empty<SingleMarketDataQualityRejectSampleDto>(), PaperExecutions = Array.Empty<SingleMarketPaperExecutionDto>() });
        TrimAll();
    }

    private bool IsDuplicateRecentLog(TerminalLogEntryDto log)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - RecentLogDedupeTtl;
        var bucketTicks = log.Timestamp.ToUniversalTime().Ticks / RecentLogDedupeTtl.Ticks;
        var key = $"{bucketTicks}|{NormalizeLogCategory(log)}|{StableHash(log.Message)}";
        lock (_gate)
        {
            foreach (var stale in _recentLogDedupe.Where(x => x.Value < cutoff).Select(x => x.Key).ToArray())
                _recentLogDedupe.Remove(stale);
            if (_recentLogDedupe.ContainsKey(key)) return true;
            _recentLogDedupe[key] = now;
            return false;
        }
    }

    private static string NormalizeLogCategory(TerminalLogEntryDto log)
    {
        var message = log.Message ?? string.Empty;
        if (message.StartsWith("[CONFIG]", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("[DIAGNOSTICS]", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("[SOAK_READINESS]", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("[COST_PROFILE", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("[PAPER_MODE", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("[PAPER_EFFECTIVE_RISK]", StringComparison.OrdinalIgnoreCase)
            || message.Contains("Bot API listening", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ExecutionMode=", StringComparison.OrdinalIgnoreCase))
            return "startup-config";
        return log.Source ?? string.Empty;
    }

    private static bool IsCriticalLog(TerminalLogEntryDto log)
        => log.Level.Equals("error", StringComparison.OrdinalIgnoreCase)
            || log.Message.Contains("[MEMORY_CRITICAL]", StringComparison.OrdinalIgnoreCase)
            || log.Message.Contains("[PAPER_CONFIG_ERROR]", StringComparison.OrdinalIgnoreCase);

    private static string StableHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();

    public OpportunityDto[] Opportunities()=>_opps.ToArray();
    public TradeLogEntryDto[] Trades()=>_trades.ToArray();
    public PaperPositionDto[] Positions()=>_positions.ToArray();
    public PaperSettlementRecord[] PaperSettlementsRecords()=>_paperSettlements.ToArray();
    public TerminalLogEntryDto[] Logs()=>_logs.ToArray();
    public EquityPointDto[] Equity()=>_equity.ToArray();
    public SingleMarketArbOpportunityDto[] SingleMarketOpportunities()=>_singleMarketOpportunities.ToArray();
    public SingleMarketPaperExecutionDto[] SingleMarketExecutions()=>_singleMarketExecutions.ToArray();
}
