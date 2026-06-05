using System.Collections.Concurrent;
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
    private readonly ConcurrentQueue<TerminalLogEntryDto> _logs = new();
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
    private QuietLogGateStats _quietLogGateStats = new(0, 0, new Dictionary<string, long>(), new Dictionary<string, long>(), 0, 0);
    private OrderBookServiceStats _orderBookServiceStats = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    private int _paperPretradeRejects;
    private int _paperDuplicateSuppressions;
    private int _paperExecutionsCount;
    private readonly object _paperCountersGate = new();
    private readonly Dictionary<string, int> _paperPretradeRejectsByReason = new(StringComparer.OrdinalIgnoreCase);

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
    public QuietLogGateStats QuietLogGateStats => _quietLogGateStats;
    public OrderBookServiceStats OrderBookServiceStats => _orderBookServiceStats;
    public int SingleMarketOpportunitiesCount => _singleMarketOpportunities.Count;
    public int SingleMarketExecutionsCount => _singleMarketExecutions.Count;
    public int PaperOpenPositions => Status.OpenPositions;
    public decimal PaperTotalExposure => Status.LockedCapital;
    public int PaperOpenCountLastHour { get; private set; }
    public int PaperPretradeRejects => Volatile.Read(ref _paperPretradeRejects);
    public int PaperDuplicateSuppressions => Volatile.Read(ref _paperDuplicateSuppressions);
    public long LiveTradingBlockedCount => TradingBot.Services.LiveTradingGuard.BlockedCount;
    public int PaperExecutionsCount => SingleMarketExecutionsCount + Volatile.Read(ref _paperExecutionsCount);
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
    public void ReplacePositions(IEnumerable<PaperPositionDto> items){while(_positions.TryDequeue(out _)){} foreach(var i in items.Take(_runtime.MaxPaperPositions)) _positions.Enqueue(i); Trim(_positions,_runtime.MaxPaperPositions);}
    public void AddLog(TerminalLogEntryDto l){_logs.Enqueue(l); Trim(_logs,_runtime.MaxRecentLogs);}
    public void AddEquity(EquityPointDto e){_equity.Enqueue(e); Trim(_equity,500);}
    public void AddSingleMarketOpportunity(SingleMarketArbOpportunityDto o){_singleMarketOpportunities.Enqueue(o); Trim(_singleMarketOpportunities,_runtime.MaxSingleMarketOpportunities);}
    public void AddSingleMarketExecution(SingleMarketPaperExecutionDto e){_singleMarketExecutions.Enqueue(e); Trim(_singleMarketExecutions,_runtime.MaxSingleMarketExecutions);}
    public void ReplaceSingleMarketOpportunities(IEnumerable<SingleMarketArbOpportunityDto> items){while(_singleMarketOpportunities.TryDequeue(out _)){} foreach(var i in items.Take(_runtime.MaxSingleMarketOpportunities)) _singleMarketOpportunities.Enqueue(i); Trim(_singleMarketOpportunities,_runtime.MaxSingleMarketOpportunities);}
    public void ReplaceSingleMarketExecutions(IEnumerable<SingleMarketPaperExecutionDto> items){while(_singleMarketExecutions.TryDequeue(out _)){} foreach(var i in items.Take(_runtime.MaxSingleMarketExecutions)) _singleMarketExecutions.Enqueue(i); Trim(_singleMarketExecutions,_runtime.MaxSingleMarketExecutions);}
    public void AddSignalREvent(string eventName){_signalREventBuffer.Enqueue($"{DateTime.UtcNow:O}|{eventName}"); Trim(_signalREventBuffer,_runtime.MaxSignalREventBuffer);}
    public void AddUnresolvedDiagnostics(IEnumerable<object> items){foreach(var item in items.Take(_runtime.MaxUnresolvedDiagnostics)) _unresolvedDiagnostics.Enqueue(item); Trim(_unresolvedDiagnostics,_runtime.MaxUnresolvedDiagnostics);}
    public void SetQuietLogGateStats(QuietLogGateStats stats) => _quietLogGateStats = stats;
    public void SetOrderBookServiceStats(OrderBookServiceStats stats) => _orderBookServiceStats = stats;
    public void RecordPaperPretradeReject(string reason)
    {
        Interlocked.Increment(ref _paperPretradeRejects);
        lock (_paperCountersGate) _paperPretradeRejectsByReason[reason] = _paperPretradeRejectsByReason.TryGetValue(reason, out var c) ? c + 1 : 1;
        if (reason.Equals("DuplicateOpenPosition", StringComparison.OrdinalIgnoreCase)) RecordPaperDuplicateSuppression();
    }
    public void RecordPaperDuplicateSuppression() => Interlocked.Increment(ref _paperDuplicateSuppressions);
    public void SetPaperOpenCountLastHour(int count) => PaperOpenCountLastHour = count;
    public void RecordPaperExecution() => Interlocked.Increment(ref _paperExecutionsCount);

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
        ["paperExecutionsCount"] = PaperExecutionsCount
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
    public OpportunityDto[] Opportunities()=>_opps.ToArray();
    public TradeLogEntryDto[] Trades()=>_trades.ToArray();
    public PaperPositionDto[] Positions()=>_positions.ToArray();
    public TerminalLogEntryDto[] Logs()=>_logs.ToArray();
    public EquityPointDto[] Equity()=>_equity.ToArray();
    public SingleMarketArbOpportunityDto[] SingleMarketOpportunities()=>_singleMarketOpportunities.ToArray();
    public SingleMarketPaperExecutionDto[] SingleMarketExecutions()=>_singleMarketExecutions.ToArray();
}
