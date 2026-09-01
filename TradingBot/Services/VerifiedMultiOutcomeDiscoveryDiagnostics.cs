using System.Text.Json;
using TradingBot.Api;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services.MultiOutcome;

namespace TradingBot.Services;

public sealed record VerifiedDiscoverySnapshot(
    int Discovered, int ValidPriced, int Skipped, IReadOnlyDictionary<string,int> SkippedByReason,
    int Evaluated, int PositiveAfterSafety, int ExecutableLike, string TopBlocker,
    long DiscoveredTotal, long ValidPricedTotal, long EvaluatedTotal, long PositiveAfterSafetyTotal, long ExecutableLikeTotal)
{
    public static VerifiedDiscoverySnapshot Empty { get; } = new(0,0,0,new Dictionary<string,int>(),0,0,0,"VerifiedGroupCacheEmpty",0,0,0,0,0);
    public int Reason(string reason) => SkippedByReason.GetValueOrDefault(reason);
}

public sealed record ShadowGroupCompletionSample(
    string GroupId, string GroupKey, IReadOnlyList<string> MarketIdsPresent, IReadOnlyList<string> MarketIdsMissing,
    bool CompletionAttempted, bool CompletionSucceeded, int AdditionalMarketsRequested, int AdditionalMarketsLoaded,
    int AdditionalOrderbooksRequested, int AdditionalOrderbooksLoaded, string CompletionFailureReason,
    bool ValidPricedAfterCompletion=false, decimal? AfterSafetyEdgeAfterCompletion=null,
    bool ExecutableLikeAfterCompletion=false, string FirstBlockingReasonAfterCompletion="None",
    IReadOnlyList<string>? AllBlockingReasonsAfterCompletion=null,
    IReadOnlyList<string>? TokenIdsRequired=null, IReadOnlyList<string>? TokenIdsWithOrderbook=null,
    IReadOnlyList<string>? TokenIdsMissingOrderbook=null, IReadOnlyList<string>? TokenIdsStaleOrderbook=null,
    IReadOnlyDictionary<string,string>? OrderbookMissingReasonsByToken=null);

public sealed record ShadowCompletionCycle(
    int Attempted, int Completed, int MarketsRequested, int MarketsLoaded, int OrderbooksRequested, int OrderbooksLoaded,
    IReadOnlyList<ShadowGroupCompletionSample> Samples);

public sealed record ShadowOrderbookAvailabilitySnapshot(
    long Requests5m, long Loaded5m, long Missing5m, long Stale5m, long LoadFailed5m,
    decimal SuccessRate5m, string TopMissingReason5m, string TopMissingExchangeOrSource5m,
    double AvgAgeMs5m, double P95AgeMs5m, double MaxAgeMs5m,
    long RequestsTotal, long LoadedTotal, long MissingTotal, long StaleTotal, long LoadFailedTotal,
    long GroupsDeduped5m, long CompletionAttemptsSuppressed5m, long RequestsDeduped5m,
    long RequestsSuppressed5m, long CacheHits5m, long CacheMisses5m)
{
    public static ShadowOrderbookAvailabilitySnapshot Empty { get; } = new(0,0,0,0,0,0,"None","PolymarketClob",0,0,0,0,0,0,0,0,0,0,0,0,0,0);
}

public sealed record ShadowCompletionSnapshot(
    bool ConfigPresent, bool Enabled, bool RequireVerified, int MaxGroups, int MaxAdditionalMarkets, bool PaperOpenAllowed,
    long Attempted5m, long Completed5m, long MarketsRequested5m, long MarketsLoaded5m, long OrderbooksRequested5m,
    long OrderbooksLoaded5m, long Failed5m, string TopFailure5m, int SkippedAfterCompletion5m,
    int ValidPricedAfterCompletion5m, int EvaluatedAfterCompletion5m, int PositiveAfterSafetyAfterCompletion5m,
    int ExecutableLikeAfterCompletion5m, long AttemptedTotal, long CompletedTotal, long MarketsRequestedTotal,
    long MarketsLoadedTotal, long OrderbooksRequestedTotal, long OrderbooksLoadedTotal, long FailedTotal,
    long EvaluatedAfterCompletionTotal, long ValidPricedAfterCompletionTotal, long PositiveAfterSafetyAfterCompletionTotal,
    long ExecutableLikeAfterCompletionTotal)
{
    public static ShadowCompletionSnapshot Empty { get; } = new(
        ConfigPresent: false,
        Enabled: false,
        RequireVerified: true,
        MaxGroups: 0,
        MaxAdditionalMarkets: 0,
        PaperOpenAllowed: false,
        Attempted5m: 0,
        Completed5m: 0,
        MarketsRequested5m: 0,
        MarketsLoaded5m: 0,
        OrderbooksRequested5m: 0,
        OrderbooksLoaded5m: 0,
        Failed5m: 0,
        TopFailure5m: "ShadowGroupCompletionNotWired",
        SkippedAfterCompletion5m: 0,
        ValidPricedAfterCompletion5m: 0,
        EvaluatedAfterCompletion5m: 0,
        PositiveAfterSafetyAfterCompletion5m: 0,
        ExecutableLikeAfterCompletion5m: 0,
        AttemptedTotal: 0,
        CompletedTotal: 0,
        MarketsRequestedTotal: 0,
        MarketsLoadedTotal: 0,
        OrderbooksRequestedTotal: 0,
        OrderbooksLoadedTotal: 0,
        FailedTotal: 0,
        EvaluatedAfterCompletionTotal: 0,
        ValidPricedAfterCompletionTotal: 0,
        PositiveAfterSafetyAfterCompletionTotal: 0,
        ExecutableLikeAfterCompletionTotal: 0);
}

/// <summary>Shadow-only observability for verified-group discovery; never feeds eligibility or execution.</summary>
public static class VerifiedMultiOutcomeDiscoveryDiagnostics
{
    public static readonly string[] ShadowSiblingOrderbookMissingReasons =
    [
        "ShadowSiblingOrderbookNotRequested", "ShadowSiblingOrderbookRequestFailed", "ShadowSiblingOrderbookNotFound",
        "ShadowSiblingOrderbookEmpty", "ShadowSiblingOrderbookNoBids", "ShadowSiblingOrderbookNoAsks",
        "ShadowSiblingOrderbookStale", "ShadowSiblingOrderbookTokenMapMissing", "ShadowSiblingOrderbookMarketInactive",
        "ShadowSiblingOrderbookRateLimited", "ShadowSiblingOrderbookProviderError", "ShadowSiblingOrderbookTimeout",
        "ShadowSiblingOrderbookLoadQueueDropped", "ShadowSiblingOrderbookCompletionWindowExpired"
    ];
    private static readonly object Sync = new();
    private static DateTime _windowStart = DateTime.UtcNow;
    private static IReadOnlyList<ResolvedVerifiedGroup> _groups = [];
    private static IReadOnlyList<VerifiedGroupDiagnosticDto> _diagnostics = [];
    private static int _marketPoolCount, _reducedUniverseMarkets;
    private static string _discoveryMode = "Unknown";
    private static long _discoveredTotal, _validTotal, _evaluatedTotal, _positiveTotal, _executableTotal;
    private static PaperPhase1Options? _completionConfig;
    private static readonly List<ShadowGroupCompletionSample> CompletionSamples = [];
    private static long _attempted5m,_completed5m,_marketsRequested5m,_marketsLoaded5m,_booksRequested5m,_booksLoaded5m;
    private static long _attemptedTotal,_completedTotal,_marketsRequestedTotal,_marketsLoadedTotal,_booksRequestedTotal,_booksLoadedTotal;
    private static long _evaluatedAfterTotal,_validAfterTotal,_positiveAfterTotal,_executableAfterTotal;
    private static readonly HashSet<string> AttemptedGroups = new(StringComparer.OrdinalIgnoreCase);
    private static long _groupsDeduped5m,_attemptsSuppressed5m,_requestsDeduped5m,_requestsSuppressed5m,_cacheHits5m,_cacheMisses5m;
    public static VerifiedDiscoverySnapshot Current { get; private set; } = VerifiedDiscoverySnapshot.Empty;
    public static ShadowCompletionSnapshot CompletionCurrent { get; private set; } = ShadowCompletionSnapshot.Empty;
    public static ShadowOrderbookAvailabilitySnapshot OrderbookCurrent { get; private set; } = ShadowOrderbookAvailabilitySnapshot.Empty;
    public static bool ShadowPrefetchEnabled { get { lock(Sync) return _completionConfig?.ShadowSiblingOrderbookPrefetchEnabled??false; } }

    public static void Configure(PaperPhase1Options options) { lock(Sync) _completionConfig=options; }

    public static bool TryBeginCompletion(string groupId)
    {
        lock(Sync)
        {
            if (AttemptedGroups.Add(groupId)) { _cacheMisses5m++; return true; }
            _groupsDeduped5m++; _attemptsSuppressed5m++; _cacheHits5m++; return false;
        }
    }

    public static void ObserveSuppressedOrderbooks(int count)
    { lock(Sync) { _requestsDeduped5m+=count; _requestsSuppressed5m+=count; } }

    public static void ObserveCompletion(ShadowCompletionCycle cycle)
    {
        lock(Sync)
        {
            _attempted5m+=cycle.Attempted; _completed5m+=cycle.Completed; _marketsRequested5m+=cycle.MarketsRequested;
            _marketsLoaded5m+=cycle.MarketsLoaded; _booksRequested5m+=cycle.OrderbooksRequested; _booksLoaded5m+=cycle.OrderbooksLoaded;
            _attemptedTotal+=cycle.Attempted; _completedTotal+=cycle.Completed; _marketsRequestedTotal+=cycle.MarketsRequested;
            _marketsLoadedTotal+=cycle.MarketsLoaded; _booksRequestedTotal+=cycle.OrderbooksRequested; _booksLoadedTotal+=cycle.OrderbooksLoaded;
            CompletionSamples.AddRange(cycle.Samples);
            if(CompletionSamples.Count>500) CompletionSamples.RemoveRange(0,CompletionSamples.Count-500);
        }
    }

    public static void Observe(IReadOnlyList<ResolvedVerifiedGroup> groups, IReadOnlyList<VerifiedGroupDiagnosticDto> diagnostics,
        int marketPoolCount, int reducedUniverseMarkets, string discoveryMode)
    {
        lock (Sync) { _groups=groups.ToArray(); _diagnostics=diagnostics.ToArray(); _marketPoolCount=marketPoolCount; _reducedUniverseMarkets=reducedUniverseMarkets; _discoveryMode=discoveryMode; }
    }

    public static VerifiedDiscoverySnapshot Publish(string root, DateTime now)
    {
        lock (Sync)
        {
            var reasons = _groups.Where(g=>g.ValidationStatus!="VerifiedGroupResolved").Select(g=>Specific(g.RejectionReason,g))
                .Concat(_diagnostics.Where(d=>d.SkipReason!="None" && d.EvaluationStatus!="Executable").Select(d=>Specific(d.SkipReason,null)))
                .GroupBy(x=>x,StringComparer.OrdinalIgnoreCase).ToDictionary(x=>x.Key,x=>x.Count(),StringComparer.OrdinalIgnoreCase);
            var discovered=_groups.Count;
            var evaluated=_groups.Count(g=>g.ValidationStatus=="VerifiedGroupResolved");
            var valid=_diagnostics.Count(d=>d.BestEdge.HasValue);
            var positive=_diagnostics.Count(d=>d.BestEdge>0);
            var executable=_diagnostics.Count(d=>d.BestEdge>0 && (d.EvaluationStatus.Contains("Executable",StringComparison.OrdinalIgnoreCase)||d.SkipReason=="None"));
            var skipped=Math.Max(0,discovered-valid);
            var top=reasons.OrderByDescending(x=>x.Value).ThenBy(x=>x.Key,StringComparer.OrdinalIgnoreCase).FirstOrDefault().Key ?? (discovered==0?"VerifiedGroupCacheEmpty":valid==0?"VerifiedGroupNoValidPricing":positive==0?"BelowMinEdge":"None");
            _discoveredTotal+=discovered; _validTotal+=valid; _evaluatedTotal+=evaluated; _positiveTotal+=positive; _executableTotal+=executable;
            Current=new(discovered,valid,skipped,reasons,evaluated,positive,executable,top,_discoveredTotal,_validTotal,_evaluatedTotal,_positiveTotal,_executableTotal);
            var samples=_groups.Where(g=>g.ValidationStatus!="VerifiedGroupResolved" || _diagnostics.Any(d=>d.GroupKey==g.GroupKey&&d.SkipReason!="None")).Take(50).Select(g=>{
                var d=_diagnostics.LastOrDefault(x=>x.GroupKey==g.GroupKey); var first=Specific(d?.SkipReason??g.RejectionReason,g);
                return new { GroupId=g.GroupKey,GroupKey=g.GroupKey,Strategy="VerifiedMultiOutcome",MarketIds=g.MarketIds,QuestionTitles=g.ResolvedMarkets.Select(m=>m.question).Where(q=>!string.IsNullOrWhiteSpace(q)).ToArray(),OutcomeCount=g.ResolvedMarkets.Count,ExpectedOutcomeSet=g.MarketIds,ActualOutcomeSet=g.ResolvedMarkets.Select(m=>m.id).ToArray(),HasAllOrderbooks=d is not null&&d.MissingNoAskCount==0,HasAllTokens=g.ResolvedMarkets.All(m=>m.clobTokenIds.Count>=2),IsMutuallyExclusive=(bool?)null,IsCollectivelyExhaustive=(bool?)null,VerificationStatus=g.ValidationStatus,VerificationReason=g.RejectionReason,FirstBlockingReason=first,AllBlockingReasons=new[]{first},LastSeenUtc=now};
            }).ToArray();
            var basePayload=new { TimeUtc=now,WindowStartUtc=_windowStart,WindowEndUtc=now,ReducedUniverseMarkets=_reducedUniverseMarkets,DiscoveryMode=_discoveryMode,MarketPoolCount=_marketPoolCount,CandidateGroupCount=discovered,VerifiedGroupCount=_groups.Count(g=>g.ValidationStatus=="VerifiedGroupResolved"),EvaluatedGroupCount=evaluated,ValidPricedGroupCount=valid,PositiveAfterSafetyGroupCount=positive,ExecutableLikeGroupCount=executable,SkippedByReason=reasons,TopBlocker=top,RecommendedAction=Recommend(top),TopSkippedOrBlockedGroups=samples };
            var c=_completionConfig;
            var completedSamples=CompletionSamples.GroupBy(x=>x.GroupKey,StringComparer.OrdinalIgnoreCase).Select(x=>x.Last()).TakeLast(50).ToArray();
            var completedDiagnostics=completedSamples.Where(x=>x.CompletionSucceeded).Select(x=>_diagnostics.LastOrDefault(d=>d.GroupKey==x.GroupKey)).Where(x=>x is not null).ToArray();
            var validAfter=completedDiagnostics.Count(x=>x!.BestEdge.HasValue);
            var positiveAfter=completedDiagnostics.Count(x=>x!.BestEdge>0);
            var executableAfter=completedDiagnostics.Count(x=>x!.BestEdge>0&&(x.EvaluationStatus.Contains("Executable",StringComparison.OrdinalIgnoreCase)||x.SkipReason=="None"));
            var failures=completedSamples.Where(x=>x.CompletionAttempted&&!x.CompletionSucceeded).GroupBy(x=>x.CompletionFailureReason,StringComparer.OrdinalIgnoreCase).OrderByDescending(x=>x.Count()).ThenBy(x=>x.Key).ToArray();
            var failed5m=Math.Max(0,_attempted5m-_completed5m);
            var topFailure=failures.FirstOrDefault()?.Key??(_attempted5m==0?(c is null?"ShadowGroupCompletionConfigMissing":c.ShadowGroupCompletionEnabled?"None":"ShadowGroupCompletionDisabled"):"None");
            var evaluatedAfter=completedSamples.Count(x=>x.CompletionSucceeded);
            _evaluatedAfterTotal+=evaluatedAfter; _validAfterTotal+=validAfter; _positiveAfterTotal+=positiveAfter; _executableAfterTotal+=executableAfter;
            CompletionCurrent=new(c is not null,c?.ShadowGroupCompletionEnabled??false,c?.ShadowGroupCompletionRequireVerified??true,c?.ShadowGroupCompletionMaxGroups??0,c?.ShadowGroupCompletionMaxAdditionalMarkets??0,c?.ShadowGroupCompletionPaperOpenAllowed??false,_attempted5m,_completed5m,_marketsRequested5m,_marketsLoaded5m,_booksRequested5m,_booksLoaded5m,failed5m,topFailure,Math.Max(0,discovered-validAfter),validAfter,evaluatedAfter,positiveAfter,executableAfter,_attemptedTotal,_completedTotal,_marketsRequestedTotal,_marketsLoadedTotal,_booksRequestedTotal,_booksLoadedTotal,Math.Max(0,_attemptedTotal-_completedTotal),_evaluatedAfterTotal,_validAfterTotal,_positiveAfterTotal,_executableAfterTotal);
            var missing=Math.Max(0,_booksRequested5m-_booksLoaded5m);
            var missingTotal=Math.Max(0,_booksRequestedTotal-_booksLoadedTotal);
            var missingReasons=completedSamples.SelectMany(x=>x.OrderbookMissingReasonsByToken?.Values??Enumerable.Empty<string>()).GroupBy(x=>x,StringComparer.OrdinalIgnoreCase).OrderByDescending(x=>x.Count()).ThenBy(x=>x.Key).ToArray();
            var stale=missingReasons.Where(x=>x.Key=="ShadowSiblingOrderbookStale").Sum(x=>x.Count());
            var staleTotal=0L;
            var ages=completedSamples.SelectMany(x=>x.TokenIdsWithOrderbook??Enumerable.Empty<string>()).Select(_=>0d).OrderBy(x=>x).ToArray();
            OrderbookCurrent=new(_booksRequested5m,_booksLoaded5m,missing,stale,missing,_booksRequested5m==0?0:decimal.Round((decimal)_booksLoaded5m/_booksRequested5m,4),missingReasons.FirstOrDefault()?.Key??"None","PolymarketClob",ages.Length==0?0:ages.Average(),ages.Length==0?0:ages[(int)Math.Clamp(Math.Ceiling(.95*ages.Length)-1,0,ages.Length-1)],ages.Length==0?0:ages[^1],_booksRequestedTotal,_booksLoadedTotal,missingTotal,staleTotal,missingTotal,_groupsDeduped5m,_attemptsSuppressed5m,_requestsDeduped5m,_requestsSuppressed5m,_cacheHits5m,_cacheMisses5m);
            var completionPayload=new { TimeUtc=now,WindowStartUtc=_windowStart,WindowEndUtc=now,ConfigPresent=CompletionCurrent.ConfigPresent,Enabled=CompletionCurrent.Enabled,RequireVerified=CompletionCurrent.RequireVerified,MaxGroups=CompletionCurrent.MaxGroups,MaxAdditionalMarkets=CompletionCurrent.MaxAdditionalMarkets,PaperOpenAllowed=false,Counters=CompletionCurrent,Groups=completedSamples.Select(Enrich) };
            var payload = new { basePayload.TimeUtc,basePayload.WindowStartUtc,basePayload.WindowEndUtc,basePayload.ReducedUniverseMarkets,basePayload.DiscoveryMode,basePayload.MarketPoolCount,basePayload.CandidateGroupCount,basePayload.VerifiedGroupCount,basePayload.EvaluatedGroupCount,basePayload.ValidPricedGroupCount,basePayload.PositiveAfterSafetyGroupCount,basePayload.ExecutableLikeGroupCount,basePayload.SkippedByReason,basePayload.TopBlocker,basePayload.RecommendedAction,basePayload.TopSkippedOrBlockedGroups,ShadowGroupCompletionConfigPresent=CompletionCurrent.ConfigPresent,ShadowGroupCompletionEnabled=CompletionCurrent.Enabled,ShadowGroupCompletionRequireVerified=CompletionCurrent.RequireVerified,ShadowGroupCompletionMaxGroups=CompletionCurrent.MaxGroups,ShadowGroupCompletionMaxAdditionalMarkets=CompletionCurrent.MaxAdditionalMarkets,ShadowGroupCompletionPaperOpenAllowed=false,ShadowGroupCompletion=completionPayload };
            Write(Path.Combine(root,"exports/phase1-verified-multioutcome-discovery-latest.json"),payload);
            Directory.CreateDirectory(Path.Combine(root,"exports")); File.AppendAllText(Path.Combine(root,"exports/phase1-verified-multioutcome-discovery-history.jsonl"),JsonSerializer.Serialize(payload)+Environment.NewLine);
            Write(Path.Combine(root,"exports/phase1-verified-multioutcome-completion-latest.json"),completionPayload);
            File.AppendAllText(Path.Combine(root,"exports/phase1-verified-multioutcome-completion-history.jsonl"),JsonSerializer.Serialize(completionPayload)+Environment.NewLine);
            var availabilityPayload=new { TimeUtc=now,WindowStartUtc=_windowStart,WindowEndUtc=now,ShadowSiblingOrderbookPrefetchEnabled=c?.ShadowSiblingOrderbookPrefetchEnabled??false,RequestedTokenCount=OrderbookCurrent.Requests5m,LoadedTokenCount=OrderbookCurrent.Loaded5m,MissingTokenCount=OrderbookCurrent.Missing5m,StaleTokenCount=OrderbookCurrent.Stale5m,FailedTokenCount=OrderbookCurrent.LoadFailed5m,SuccessRate=OrderbookCurrent.SuccessRate5m,MissingReasons=missingReasons.ToDictionary(x=>x.Key,x=>x.Count(),StringComparer.OrdinalIgnoreCase),RateLimitEvents=missingReasons.Where(x=>x.Key=="ShadowSiblingOrderbookRateLimited").Sum(x=>x.Count()),ProviderErrors=missingReasons.Where(x=>x.Key=="ShadowSiblingOrderbookProviderError").Sum(x=>x.Count()),Timeouts=missingReasons.Where(x=>x.Key=="ShadowSiblingOrderbookTimeout").Sum(x=>x.Count()),QueueDrops=missingReasons.Where(x=>x.Key=="ShadowSiblingOrderbookLoadQueueDropped").Sum(x=>x.Count()),RecommendedAction=RecommendOrderbooks(OrderbookCurrent)};
            Write(Path.Combine(root,"exports/phase1-shadow-orderbook-availability-latest.json"),availabilityPayload);
            File.AppendAllText(Path.Combine(root,"exports/phase1-shadow-orderbook-availability-history.jsonl"),JsonSerializer.Serialize(availabilityPayload)+Environment.NewLine);
            _attempted5m=_completed5m=_marketsRequested5m=_marketsLoaded5m=_booksRequested5m=_booksLoaded5m=0; CompletionSamples.Clear();
            _groupsDeduped5m=_attemptsSuppressed5m=_requestsDeduped5m=_requestsSuppressed5m=_cacheHits5m=_cacheMisses5m=0; AttemptedGroups.Clear();
            _windowStart=now; return Current;
        }
    }
    private static string Specific(string? reason, ResolvedVerifiedGroup? g) => reason switch {
        null or "" => "VerifiedGroupEvaluatorNotScheduled",
        "VerifiedGroupNotFoundInDiscoveredPool" when g is not null && g.MarketIds.Count==0 => "VerifiedGroupMissingMarketIds",
        "VerifiedGroupNotFoundInDiscoveredPool" => "VerifiedGroupOutsideReducedUniverse",
        "VerifiedGroupMissingSiblingMarkets" when _completionConfig is null => "ShadowGroupCompletionConfigMissing",
        "VerifiedGroupMissingSiblingMarkets" when !_completionConfig.ShadowGroupCompletionEnabled => "ShadowGroupCompletionDisabled",
        "VerifiedGroupOutcomeCountMismatch" or "InsufficientResolvedMarkets" => "VerifiedGroupInvalidOutcomeSet",
        "InvalidToken" => "VerifiedGroupMissingTokenMap",
        "OrderbookFetchFailed" or "MissingNoAsk_OrderbookUnavailable" or "MissingNoAsk_EmptyBook" or "MissingNoAsk" => "VerifiedGroupMissingOrderbooks",
        "StaleOrderbook" => "VerifiedGroupStaleOrderbook",
        "ReducedUniverseOrderbookUnstable" => "VerifiedGroupMissingOrderbooks",
        var r => r.StartsWith("VerifiedGroup",StringComparison.Ordinal)?r:"VerifiedGroupNoValidPricing"
    };
    private static string Recommend(string blocker)=>blocker switch {"VerifiedGroupCacheEmpty"=>"RefreshVerifiedGroupCache","VerifiedGroupOutsideReducedUniverse"=>"EnableShadowMultiOutcomeDiscoveryOrExpandDiscoveryCoverage","VerifiedGroupMissingTokenMap"=>"RepairTokenMap","VerifiedGroupMissingOrderbooks"=>"ImproveVerifiedGroupOrderbookCoverage","VerifiedGroupStaleOrderbook"=>"RefreshVerifiedGroupOrderbooks","VerifiedGroupInvalidOutcomeSet"=>"ReviewVerifiedOutcomeSet",_=>"InspectTopSkippedGroups"};
    private static string RecommendOrderbooks(ShadowOrderbookAvailabilitySnapshot s)=>s.TopMissingReason5m switch {"ShadowSiblingOrderbookRateLimited"=>"ReducePrefetchRateOrIncreaseBackoff","ShadowSiblingOrderbookProviderError"=>"InspectOrderbookProvider","ShadowSiblingOrderbookTokenMapMissing"=>"RepairTokenMap","ShadowSiblingOrderbookStale"=>"RefreshSiblingOrderbooks",_ when s.SuccessRate5m<.5m=>"InspectShadowOrderbookCoverage",_=>"KeepMonitoring"};
    private static object Enrich(ShadowGroupCompletionSample x)
    {
        var d=_diagnostics.LastOrDefault(v=>v.GroupKey==x.GroupKey); var valid=d?.BestEdge.HasValue==true; var edge=d?.BestEdge;
        var first=x.CompletionSucceeded
            ? valid
                ? edge>0 ? d!.SkipReason : "VerifiedGroupCompletedBelowMinEdge"
                : d?.SkipReason switch
                {
                    "StaleOrderbook" => "VerifiedGroupSiblingOrderbookStale",
                    "InvalidToken" => "VerifiedGroupSiblingTokenMapMissing",
                    "MissingNoAsk" => "ShadowSiblingOrderbookNoAsks",
                    "MissingNoAsk_EmptyBook" => "ShadowSiblingOrderbookEmpty",
                    "OrderbookFetchFailed" or "MissingNoAsk_OrderbookUnavailable" => "ShadowSiblingOrderbookNotFound",
                    _ => "VerifiedGroupCompletedButNoValidPricing"
                }
            : x.CompletionFailureReason;
        return new { x.GroupId,x.GroupKey,x.MarketIdsPresent,x.MarketIdsMissing,TokenIdsRequired=x.TokenIdsRequired??[],TokenIdsWithOrderbook=x.TokenIdsWithOrderbook??Enumerable.Empty<string>(),TokenIdsMissingOrderbook=x.TokenIdsMissingOrderbook??[],TokenIdsStaleOrderbook=x.TokenIdsStaleOrderbook??[],x.CompletionAttempted,x.CompletionSucceeded,GroupCompletionFailureReason=x.CompletionFailureReason,OrderbookRequests=x.AdditionalOrderbooksRequested,OrderbooksLoaded=x.AdditionalOrderbooksLoaded,OrderbooksMissing=Math.Max(0,x.AdditionalOrderbooksRequested-x.AdditionalOrderbooksLoaded),OrderbooksStale=(x.TokenIdsStaleOrderbook?.Count??0),OrderbookMissingReasonsByToken=x.OrderbookMissingReasonsByToken??new Dictionary<string,string>(),DiagnosticsOnly=true,PaperOpenAllowed=false,ValidPricedAfterCompletion=valid,AfterSafetyEdgeAfterCompletion=edge,ExecutableLikeAfterCompletion=valid&&edge>0&&(d!.EvaluationStatus.Contains("Executable",StringComparison.OrdinalIgnoreCase)||d.SkipReason=="None"),FirstBlockingReasonAfterCompletion=first,AllBlockingReasonsAfterCompletion=new[]{first} };
    }
    private static void Write(string path,object value){Directory.CreateDirectory(Path.GetDirectoryName(path)!);var tmp=path+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));File.Move(tmp,path,true);}
}
