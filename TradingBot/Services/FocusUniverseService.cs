using System.Text.Json;
using TradingBot.Api;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record FocusUniverseItem(string WatchlistId, DateTime FirstSeenUtc, DateTime LastSeenUtc, string Strategy, string FamilyType, string MarketIdOrGroupKey, string Title, decimal? CurrentRawEdge, decimal? CurrentAfterCostEdge, decimal CurrentAfterSafetyEdge, decimal BestObservedAfterSafetyEdge, decimal WorstObservedAfterSafetyEdge, decimal? PreviousAfterSafetyEdge, decimal? EdgeDelta, string EdgeTrend, int Observations, int ConsecutiveMissing, string LastRejectedReason, bool ValidPriced, bool ExecutionReady, bool ShadowWouldOpen, bool PaperOpened, string RecommendedAction);

public sealed record FocusUniverseSnapshot(bool Enabled, int WatchlistSize, long Admitted, long Evicted, long Refreshed, long SkippedByOrderbookHealth, int Improving, int Worsening, int Stable, string BestStrategy, decimal? BestAfterSafetyEdge, decimal? BestEdgeDelta, int ClosestToBreakEvenCount, int ExecutionReady, int PaperOpened, bool Consistent, IReadOnlyList<FocusUniverseItem> Items);

public sealed class FocusUniverseService
{
    private readonly TradingBotOptions _options;
    private readonly Dictionary<string, FocusUniverseItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private long _admitted, _evicted, _refreshed, _skipped;
    private readonly object _gate = new();
    public FocusUniverseService(TradingBotOptions options) => _options = options;

    public FocusUniverseSnapshot Update(OpportunityFamilyRankingSnapshot? ranking, RuntimeHealthSnapshot health, string contentRootPath)
    {
        var o = _options.FocusUniverse;
        lock (_gate)
        {
            if (!o.Enabled)
            {
                _items.Clear();
                var disabled = Snapshot(false, health);
                if (o.ExportEnabled) TryExport(disabled, health, contentRootPath);
                return disabled;
            }
            var orderbookClean = (!o.RequireOrderbookStableNow || health.OrderbookStableNow) && (!o.RequireReducedUniverseOrderbookStableNow || health.ReducedUniverseOrderbookStableNow) && !health.OrderbookCircuitBreakerActive;
            if (!orderbookClean) _skipped += Math.Min(_items.Count, Math.Max(0, o.MaxRefreshPerCycle));
            var candidates = (ranking?.PricedFamilies ?? Array.Empty<OpportunityFamilySummary>())
                .Where(x => IsAdmissible(x, o))
                .OrderByDescending(x => x.BestAfterSafetyEdge!.Value)
                .Take(Math.Max(0, o.MaxWatchlistItems));
            var refreshBudget = Math.Max(0, o.MaxRefreshPerCycle);
            foreach (var c in candidates) Upsert(c, orderbookClean && refreshBudget-- > 0);
            foreach (var key in _items.Keys.ToArray())
            {
                if ((DateTime.UtcNow - _items[key].LastSeenUtc) > TimeSpan.FromHours(6)) { _items.Remove(key); _evicted++; }
            }
            EvictToCap(o.MaxWatchlistItems);
            var s = Snapshot(true, health);
            if (!s.Consistent) Console.WriteLine("[FOCUS_UNIVERSE_WARNING] Reason=BestPositiveWhileTotalPositiveZero");
            if (!_options.Diagnostics.OperationalQuietMode) Console.WriteLine($"[FOCUS_UNIVERSE_SUMMARY] Enabled={s.Enabled.ToString().ToLowerInvariant()} WatchlistSize={s.WatchlistSize} Admitted={s.Admitted} Evicted={s.Evicted} Refreshed={s.Refreshed} SkippedByOrderbookHealth={s.SkippedByOrderbookHealth} Improving={s.Improving} Worsening={s.Worsening} Stable={s.Stable} BestStrategy={s.BestStrategy} BestAfterSafetyEdge={s.BestAfterSafetyEdge?.ToString("0.####") ?? "N/A"} BestEdgeDelta={s.BestEdgeDelta?.ToString("0.####") ?? "N/A"} ClosestToBreakEven={s.ClosestToBreakEvenCount} ExecutionReady={s.ExecutionReady} PaperOpened={s.PaperOpened} Consistent={s.Consistent.ToString().ToLowerInvariant()}");
            if (o.ExportEnabled) TryExport(s, health, contentRootPath);
            return s;
        }
    }

    private static bool IsAdmissible(OpportunityFamilySummary x, FocusUniverseOptions o)
    {
        if (o.RequireValidPriced && x.ValidPriced <= 0) return false;
        if (!x.BestAfterSafetyEdge.HasValue || x.BestAfterSafetyEdge.Value < o.MinCandidateAfterSafetyEdge) return false;
        var r = x.TopRejectedReason ?? string.Empty;
        string[] blocked = ["MissingYesAsk","MissingNoAsk","SuspiciousYesNoAskSum","TokenOutcomeMappingUnverified","AutoCandidateUnverified","ReviewOnly","DiagnosticsOnly","DifferentEvent","DataQualityRejected","MissingPricing"];
        if (blocked.Any(b => r.Contains(b, StringComparison.OrdinalIgnoreCase) || x.FamilyType.Contains(b, StringComparison.OrdinalIgnoreCase))) return false;
        if (x.Strategy.Equals("AutoCandidateMultiOutcome", StringComparison.OrdinalIgnoreCase) && x.VerificationHigh + x.VerificationMedium <= 0) return false;
        return x.Strategy.Equals("SingleMarketBuyBoth", StringComparison.OrdinalIgnoreCase) || x.Strategy.Contains("AutoCandidate", StringComparison.OrdinalIgnoreCase);
    }

    private void Upsert(OpportunityFamilySummary c, bool refresh)
    {
        var now = DateTime.UtcNow; var id = $"{c.Strategy}|{c.BestMarketOrGroupKey}"; var edge = c.BestAfterSafetyEdge!.Value;
        _items.TryGetValue(id, out var prev); var previous = prev?.CurrentAfterSafetyEdge; var delta = previous.HasValue ? edge - previous.Value : null;
        var trend = !previous.HasValue ? "New" : delta > 0.0001m ? "Improving" : delta < -0.0001m ? "Worsening" : "Stable";
        if (prev is null) _admitted++; else if (refresh) _refreshed++;
        _items[id] = new FocusUniverseItem(id, prev?.FirstSeenUtc ?? now, now, c.Strategy, c.FamilyType, c.BestMarketOrGroupKey, c.BestTitle, c.BestRawEdge, c.BestAfterCostEdge, edge, Math.Max(prev?.BestObservedAfterSafetyEdge ?? edge, edge), Math.Min(prev?.WorstObservedAfterSafetyEdge ?? edge, edge), previous, delta, trend, (prev?.Observations ?? 0) + 1, 0, c.TopRejectedReason, true, c.ExecutionReady > 0, c.ShadowWouldOpen > 0, c.PaperOpened > 0, c.RecommendedAction);
    }
    private void EvictToCap(int cap){ cap=Math.Max(0,cap); foreach(var k in _items.Values.OrderBy(x=>x.CurrentAfterSafetyEdge).ThenBy(x=>x.LastSeenUtc).Take(Math.Max(0,_items.Count-cap)).Select(x=>x.WatchlistId).ToArray()){_items.Remove(k);_evicted++;}}
    private FocusUniverseSnapshot Snapshot(bool enabled, RuntimeHealthSnapshot h){ var items=_items.Values.OrderByDescending(x=>x.CurrentAfterSafetyEdge).ToArray(); var exec=Math.Min(items.Count(x=>x.ExecutionReady), h.StrategyCounters.Values.Sum(x=>x.ExecutionReady)); var paper=Math.Min(items.Count(x=>x.PaperOpened), h.PaperExecutionsCount); var best=items.FirstOrDefault(); var positive=h.StrategyCounters.Values.Sum(x=>x.PositiveEdges); var consistent=exec<=h.StrategyCounters.Values.Sum(x=>x.ExecutionReady)&&paper<=h.PaperExecutionsCount&&!(best?.CurrentAfterSafetyEdge>0&&positive==0); return new(enabled,items.Length,_admitted,_evicted,_refreshed,_skipped,items.Count(x=>x.EdgeTrend=="Improving"),items.Count(x=>x.EdgeTrend=="Worsening"),items.Count(x=>x.EdgeTrend=="Stable"),best?.Strategy??"N/A",best?.CurrentAfterSafetyEdge,best?.EdgeDelta,items.Count(x=>x.CurrentAfterSafetyEdge>=_options.FocusUniverse.MinCandidateAfterSafetyEdge),exec,paper,consistent,items);}
    private void TryExport(FocusUniverseSnapshot s, RuntimeHealthSnapshot h, string root){ try{ var path=Path.Combine(root,"exports/focus-universe-watchlist-latest.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!); var payload=new{generatedAtUtc=DateTime.UtcNow,h.ProcessRunId,h.Uptime,enabled=s.Enabled,maxWatchlistItems=_options.FocusUniverse.MaxWatchlistItems,maxRefreshPerCycle=_options.FocusUniverse.MaxRefreshPerCycle,minCandidateAfterSafetyEdge=_options.FocusUniverse.MinCandidateAfterSafetyEdge,h.PaperDiagnosticsLimitedEligible,h.OrderbookStableNow,h.ReducedUniverseOrderbookStableNow,watchlistSize=s.WatchlistSize,admitted=s.Admitted,evicted=s.Evicted,refreshed=s.Refreshed,skippedByOrderbookHealth=s.SkippedByOrderbookHealth,improving=s.Improving,worsening=s.Worsening,stable=s.Stable,bestAfterSafetyEdge=s.BestAfterSafetyEdge,closestToBreakEvenCount=s.ClosestToBreakEvenCount,executionReady=s.ExecutionReady,paperOpened=s.PaperOpened,items=s.Items.Select((x,i)=>new{rank=i+1,x.WatchlistId,x.Strategy,x.FamilyType,marketIdGroupKey=x.MarketIdOrGroupKey,x.Title,x.CurrentAfterSafetyEdge,x.BestObservedAfterSafetyEdge,x.PreviousAfterSafetyEdge,x.EdgeDelta,x.EdgeTrend,x.Observations,x.LastRejectedReason,x.ExecutionReady,x.ShadowWouldOpen,x.PaperOpened,x.RecommendedAction})}; var json=JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}); var tmp=path+".tmp"; for(var i=0;i<3;i++){try{File.WriteAllText(tmp,json);File.Move(tmp,path,true);break;}catch when(i<2){Thread.Sleep(50);}}}catch(Exception ex){Console.WriteLine($"[FOCUS_UNIVERSE_EXPORT_WARNING] Error={ex.Message}");}}
}
