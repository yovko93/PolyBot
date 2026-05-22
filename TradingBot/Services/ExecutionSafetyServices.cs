using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public interface IRiskManager
{
    bool CanOpenPosition(ExecutionOpportunity opportunity, out RejectionReason reason);
    void RegisterPlannedExecution(OrderPlan plan);
    void RegisterExecutionResult(ExecutionResult result);
    RiskSnapshot GetRiskSnapshot();
    void SetKillSwitch(bool enabled);
}

public sealed class RiskManager(IOptions<ExecutionOptions> options) : IRiskManager
{
    private readonly ExecutionOptions _o = options.Value;
    private readonly object _gate = new();
    private decimal _dailyNotional;
    private DateOnly _day = DateOnly.FromDateTime(DateTime.UtcNow);
    private int _openPositions;
    private int _consecutiveFailures;
    private readonly Dictionary<string, decimal> _byMarket = new();
    private readonly Dictionary<string, decimal> _byStrategy = new();
    private readonly Dictionary<string, int> _rejected = new();
    private bool _killSwitch = options.Value.KillSwitchEnabled;

    public bool CanOpenPosition(ExecutionOpportunity o, out RejectionReason reason)
    {
        lock (_gate)
        {
            ResetIfNewDay();
            if (_killSwitch) { reason = RejectionReason.KillSwitchEnabled; return false; }
            if (_openPositions >= _o.MaxOpenPositions) { reason = RejectionReason.MaxOpenPositions; return false; }
            if (_dailyNotional + o.Quantity * o.Price > _o.MaxDailyNotional) { reason = RejectionReason.MaxDailyNotional; return false; }
            var exp = _byMarket.TryGetValue(o.MarketId, out var v) ? v : 0m;
            if (exp + o.Quantity * o.Price > _o.MaxExposurePerMarket) { reason = RejectionReason.MaxExposurePerMarket; return false; }
            reason = RejectionReason.None; return true;
        }
    }
    public void RegisterPlannedExecution(OrderPlan p){ lock(_gate){ ResetIfNewDay(); _dailyNotional += p.TotalNotional; _openPositions++; _byMarket[p.Legs[0].MarketId]=(_byMarket.TryGetValue(p.Legs[0].MarketId,out var v)?v:0)+p.TotalNotional; _byStrategy[p.Strategy]=(_byStrategy.TryGetValue(p.Strategy,out var s)?s:0)+p.TotalNotional; }}
    public void RegisterExecutionResult(ExecutionResult r){ lock(_gate){ if(r.Status==ExecutionResultStatus.Failed) _consecutiveFailures++; else _consecutiveFailures=0; if(r.RejectionReason!=RejectionReason.None) _rejected[r.RejectionReason.ToString()]=(_rejected.TryGetValue(r.RejectionReason.ToString(),out var c)?c:0)+1; }}
    public RiskSnapshot GetRiskSnapshot(){ lock(_gate){ ResetIfNewDay(); return new(_openPositions,_byMarket.Values.Sum(),new Dictionary<string,decimal>(_byMarket),new Dictionary<string,decimal>(_byStrategy),_dailyNotional,new Dictionary<string,int>(_rejected),_consecutiveFailures,_killSwitch,DateTime.UtcNow);} }
    public void SetKillSwitch(bool enabled){ lock(_gate){ _killSwitch=enabled; } }
    private void ResetIfNewDay(){ var d=DateOnly.FromDateTime(DateTime.UtcNow); if(d!=_day){ _day=d; _dailyNotional=0; }}
}

public sealed class ExecutionAuditLog
{
    private readonly ConcurrentQueue<ExecutionAuditEntry> _entries = new();
    public void Add(string eventType, string opportunityId, string message){ _entries.Enqueue(new ExecutionAuditEntry(DateTime.UtcNow,eventType,opportunityId,message)); while(_entries.Count>500) _entries.TryDequeue(out _); }
    public ExecutionAuditEntry[] List() => _entries.ToArray();
}

public sealed class DuplicateExecutionGuard
{
    private readonly ConcurrentDictionary<string, DateTime> _cache = new();
    public bool TryMark(string opportunityId, TimeSpan ttl)
    {
        var now = DateTime.UtcNow;
        foreach (var kv in _cache.Where(x => x.Value < now)) _cache.TryRemove(kv.Key, out _);
        return _cache.TryAdd(opportunityId, now.Add(ttl));
    }
}

public sealed class PreTradeValidator(IOptions<ExecutionOptions> options, IRiskManager riskManager, DuplicateExecutionGuard dup)
{
    private readonly ExecutionOptions _o = options.Value;
    public PreTradeValidationResult Validate(ExecutionOpportunity o, HashSet<string> cycleSet)
    {
        if (_o.KillSwitchEnabled) return Reject(RejectionReason.KillSwitchEnabled,o,0);
        if (_o.Mode == ExecutionMode.Disabled) return Reject(RejectionReason.ModeDisabled,o,0);
        if (_o.Mode == ExecutionMode.Live && !_o.EnableLiveTrading) return Reject(RejectionReason.LiveTradingDisabled,o,0);
        if (o.EdgePerShare < 0) return Reject(RejectionReason.NegativeEdge,o,0);
        if (o.EdgePerShare == 0) return Reject(RejectionReason.EdgeAfterCostsTooLow,o,0);
        if (o.ExpectedProfit <= 0) return Reject(RejectionReason.NonPositiveExpectedProfit,o,0);
        if (!o.MarketOpen) return Reject(RejectionReason.MarketNotOpen,o,0);
        if (!o.StrategyEnabled) return Reject(RejectionReason.StrategyDisabled,o,0);
        if ((DateTime.UtcNow - o.OrderbookTimestampUtc).TotalMilliseconds > _o.MaxOrderbookAgeMs) return Reject(RejectionReason.StaleOrderbook,o,0);
        var effectiveEdge = o.EdgePerShare - o.FeesPerShare - o.SlippagePerShare;
        if (effectiveEdge < _o.MinEdgeAfterFeesAndSlippage) return Reject(RejectionReason.EdgeAfterCostsTooLow,o,effectiveEdge);
        var qty = o.Quantity;
        if (qty <= 0) return Reject(RejectionReason.NonExecutableQuantity,o,effectiveEdge);
        var notional = qty * o.Price;
        if (notional > _o.MaxNotionalPerTrade) return Reject(RejectionReason.MaxNotionalPerTrade,o,effectiveEdge,_o.MaxNotionalPerTrade / o.Price);
        if (cycleSet.Contains(o.OpportunityId) || !dup.TryMark(o.OpportunityId, TimeSpan.FromMinutes(2))) return Reject(RejectionReason.DuplicateOpportunity,o,effectiveEdge);
        if (!riskManager.CanOpenPosition(o, out var reason)) return Reject(reason,o,effectiveEdge);
        return new(true, PreTradeDecision.Approved, RejectionReason.None, [], effectiveEdge, qty, qty, DateTime.UtcNow);
    }
    private static PreTradeValidationResult Reject(RejectionReason reason, ExecutionOpportunity o, decimal edge, decimal? adjustedQty=null) => new(false,PreTradeDecision.Rejected,reason,[],edge,adjustedQty ?? o.Quantity,adjustedQty ?? 0,DateTime.UtcNow);
}

public sealed class OrderPlanBuilder(IOptions<ExecutionOptions> options)
{
    private readonly ExecutionOptions _o = options.Value;
    public OrderPlan Build(ExecutionOpportunity o, PreTradeValidationResult v)
    {
        var planId = DeterministicId($"{o.OpportunityId}:{o.Strategy}:{o.MarketId}:{o.Price}:{v.AdjustedQuantity}");
        var leg = new OrderPlanLeg("Polymarket", o.MarketId, o.MarketId, "BUY", "YES", o.Price, v.AdjustedQuantity, o.Price + _o.MaxSlippagePerLeg, o.Price * v.AdjustedQuantity, "LIMIT", "IOC", true);
        return new(planId, o.OpportunityId, o.Strategy, _o.Mode.ToString(), DateTime.UtcNow, [leg], leg.EstimatedCost, leg.Quantity, o.ExpectedProfit, o.EdgePerShare, o.FeesPerShare*v.AdjustedQuantity, o.SlippagePerShare*v.AdjustedQuantity, _o.MaxSlippagePerLeg, v, v.IsValid?"Planned":"Rejected");
    }
    public static string DeterministicId(string s){ var h=SHA256.HashData(Encoding.UTF8.GetBytes(s)); return Convert.ToHexString(h)[..24].ToLowerInvariant(); }
}

public sealed class DryRunLiveExecutor(ExecutionAuditLog audit)
{
    public ExecutionResult Execute(OrderPlan plan)
    {
        audit.Add("dry-run-order-simulated", plan.OpportunityId, $"Simulated {plan.Legs.Count} order legs.");
        var legResults = plan.Legs.Select(l => new ExecutionLegResult(l.Exchange, l.MarketId, FillStatus.Simulated, l.Quantity, 0, l.Price)).ToArray();
        return new("DryRunLive", ExecutionResultStatus.Simulated, plan.OrderPlanId, plan.OpportunityId, plan.Legs, RejectionReason.None, legResults, false, DateTime.UtcNow);
    }
}
