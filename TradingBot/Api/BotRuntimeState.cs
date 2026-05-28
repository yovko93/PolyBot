using System.Collections.Concurrent;
using TradingBot.Options;

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
    public TradingBot.Services.DiscoveryHealth DiscoveryHealth { get; private set; } = TradingBot.Services.DiscoveryHealthFactory.FromSummary(new TradingBot.Services.MarketDiscoverySummary(), true);
    public RiskStateDto Risk { get; private set; } = new(100,5,0.003m,0.25m,300,0,5,0,100,new(),true,true,true,true,DateTime.UtcNow,0);
    public BotControlStateDto Controls { get; private set; } = new(false, "RUNNING", DateTime.UtcNow, 0);
    private readonly ConcurrentQueue<OpportunityDto> _opps = new();
    private readonly ConcurrentQueue<TradeLogEntryDto> _trades = new();
    private readonly ConcurrentQueue<PaperPositionDto> _positions = new();
    private readonly ConcurrentQueue<TerminalLogEntryDto> _logs = new();
    private readonly ConcurrentQueue<EquityPointDto> _equity = new();
    private readonly ConcurrentQueue<ScannerStatsDto> _scannerStatsHistory = new();
    private static void Trim<T>(ConcurrentQueue<T> q,int max){ while(q.Count>max) q.TryDequeue(out _); }
    public int ScannerStatsHistoryCount => _scannerStatsHistory.Count;
    public long NextSeq()=>Interlocked.Increment(ref _seq);
    public void SetStatus(BotStatusDto s){lock(_gate) Status=s;}
    public void SetScannerStats(ScannerStatsDto s){lock(_gate) ScannerStats=s; _scannerStatsHistory.Enqueue(s); Trim(_scannerStatsHistory, _runtime.MaxScannerStatsHistory);}    
    public void SetRisk(RiskStateDto r){lock(_gate) Risk=r;}
    public void SetOpportunityDiagnostics(TradingBot.Models.OpportunityDiagnosticsSnapshot? d){lock(_gate) OpportunityDiagnostics=d;}
    public void SetMultiOutcomeDiagnostics(MultiOutcomeDiagnosticsDto? d){lock(_gate) MultiOutcomeDiagnostics=d;}
    public void SetMultiOutcomeCandidates(IEnumerable<object> items){lock(_gate) MultiOutcomeCandidates = items.Take(_runtime.MaxRejectedCandidateSamples).ToArray();}
    public void SetMultiOutcomeReviewReport(IEnumerable<object> items){lock(_gate) MultiOutcomeReviewReport = items.Take(_runtime.MaxRejectedCandidateSamples).ToArray();}
    public void SetVerifiedBasketScreener(VerifiedBasketScreenerDto? d){lock(_gate) VerifiedBasketScreener=d;}
    public void SetDiscoveryHealth(TradingBot.Services.DiscoveryHealth d){lock(_gate) DiscoveryHealth=d;}
    public void SetControls(BotControlStateDto c){lock(_gate) Controls=c;}
    public void AddOpportunity(OpportunityDto o){_opps.Enqueue(o); Trim(_opps,500);}    
    public void ReplaceOpportunities(IEnumerable<OpportunityDto> items){while(_opps.TryDequeue(out _)){} foreach(var i in items) _opps.Enqueue(i); Trim(_opps,500);}    
    public void AddTrade(TradeLogEntryDto t){_trades.Enqueue(t); Trim(_trades,500);}    
    public void ReplaceTrades(IEnumerable<TradeLogEntryDto> items){while(_trades.TryDequeue(out _)){} foreach(var i in items) _trades.Enqueue(i); Trim(_trades,500);}    
    public void AddPosition(PaperPositionDto p){_positions.Enqueue(p); Trim(_positions,200);}    
    public void ReplacePositions(IEnumerable<PaperPositionDto> items){while(_positions.TryDequeue(out _)){} foreach(var i in items) _positions.Enqueue(i); Trim(_positions,200);}    
    public void AddLog(TerminalLogEntryDto l){_logs.Enqueue(l); Trim(_logs,_runtime.MaxRecentLogs);}    
    public void AddEquity(EquityPointDto e){_equity.Enqueue(e); Trim(_equity,1000);}    
    public OpportunityDto[] Opportunities()=>_opps.ToArray();
    public TradeLogEntryDto[] Trades()=>_trades.ToArray();
    public PaperPositionDto[] Positions()=>_positions.ToArray();
    public TerminalLogEntryDto[] Logs()=>_logs.ToArray();
    public EquityPointDto[] Equity()=>_equity.ToArray();
}
