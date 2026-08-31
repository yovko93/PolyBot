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
    IReadOnlyList<string>? AllBlockingReasonsAfterCompletion=null);

public sealed record ShadowCompletionCycle(
    int Attempted, int Completed, int MarketsRequested, int MarketsLoaded, int OrderbooksRequested, int OrderbooksLoaded,
    IReadOnlyList<ShadowGroupCompletionSample> Samples);

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
    public static ShadowCompletionSnapshot Empty { get; } = new(false,false,true,0,0,false,0,0,0,0,0,0,0,"ShadowGroupCompletionNotWired",0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0);
}

/// <summary>Shadow-only observability for verified-group discovery; never feeds eligibility or execution.</summary>
public static class VerifiedMultiOutcomeDiscoveryDiagnostics
{
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
    public static VerifiedDiscoverySnapshot Current { get; private set; } = VerifiedDiscoverySnapshot.Empty;
    public static ShadowCompletionSnapshot CompletionCurrent { get; private set; } = ShadowCompletionSnapshot.Empty;

    public static void Configure(PaperPhase1Options options) { lock(Sync) _completionConfig=options; }

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
            var completionPayload=new { TimeUtc=now,WindowStartUtc=_windowStart,WindowEndUtc=now,ConfigPresent=CompletionCurrent.ConfigPresent,Enabled=CompletionCurrent.Enabled,RequireVerified=CompletionCurrent.RequireVerified,MaxGroups=CompletionCurrent.MaxGroups,MaxAdditionalMarkets=CompletionCurrent.MaxAdditionalMarkets,PaperOpenAllowed=false,Counters=CompletionCurrent,Groups=completedSamples.Select(Enrich) };
            var payload = new { basePayload.TimeUtc,basePayload.WindowStartUtc,basePayload.WindowEndUtc,basePayload.ReducedUniverseMarkets,basePayload.DiscoveryMode,basePayload.MarketPoolCount,basePayload.CandidateGroupCount,basePayload.VerifiedGroupCount,basePayload.EvaluatedGroupCount,basePayload.ValidPricedGroupCount,basePayload.PositiveAfterSafetyGroupCount,basePayload.ExecutableLikeGroupCount,basePayload.SkippedByReason,basePayload.TopBlocker,basePayload.RecommendedAction,basePayload.TopSkippedOrBlockedGroups,ShadowGroupCompletionConfigPresent=CompletionCurrent.ConfigPresent,ShadowGroupCompletionEnabled=CompletionCurrent.Enabled,ShadowGroupCompletionRequireVerified=CompletionCurrent.RequireVerified,ShadowGroupCompletionMaxGroups=CompletionCurrent.MaxGroups,ShadowGroupCompletionMaxAdditionalMarkets=CompletionCurrent.MaxAdditionalMarkets,ShadowGroupCompletionPaperOpenAllowed=false,ShadowGroupCompletion=completionPayload };
            Write(Path.Combine(root,"exports/phase1-verified-multioutcome-discovery-latest.json"),payload);
            Directory.CreateDirectory(Path.Combine(root,"exports")); File.AppendAllText(Path.Combine(root,"exports/phase1-verified-multioutcome-discovery-history.jsonl"),JsonSerializer.Serialize(payload)+Environment.NewLine);
            Write(Path.Combine(root,"exports/phase1-verified-multioutcome-completion-latest.json"),completionPayload);
            File.AppendAllText(Path.Combine(root,"exports/phase1-verified-multioutcome-completion-history.jsonl"),JsonSerializer.Serialize(completionPayload)+Environment.NewLine);
            _attempted5m=_completed5m=_marketsRequested5m=_marketsLoaded5m=_booksRequested5m=_booksLoaded5m=0; CompletionSamples.Clear();
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
                    "MissingNoAsk" or "OrderbookFetchFailed" or "MissingNoAsk_OrderbookUnavailable" or "MissingNoAsk_EmptyBook" => "VerifiedGroupSiblingOrderbookMissing",
                    _ => "VerifiedGroupCompletedButNoValidPricing"
                }
            : x.CompletionFailureReason;
        return new { x.GroupId,x.GroupKey,x.MarketIdsPresent,x.MarketIdsMissing,MissingSiblingCount=x.MarketIdsMissing.Count,x.CompletionAttempted,x.CompletionSucceeded,x.AdditionalMarketsRequested,x.AdditionalMarketsLoaded,x.AdditionalOrderbooksRequested,x.AdditionalOrderbooksLoaded,x.CompletionFailureReason,CompletedDiagnosticsOnly=true,PaperOpenAllowed=false,ValidPricedAfterCompletion=valid,AfterSafetyEdgeAfterCompletion=edge,ExecutableLikeAfterCompletion=valid&&edge>0&&(d!.EvaluationStatus.Contains("Executable",StringComparison.OrdinalIgnoreCase)||d.SkipReason=="None"),FirstBlockingReasonAfterCompletion=first,AllBlockingReasonsAfterCompletion=new[]{first} };
    }
    private static void Write(string path,object value){Directory.CreateDirectory(Path.GetDirectoryName(path)!);var tmp=path+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));File.Move(tmp,path,true);}
}
