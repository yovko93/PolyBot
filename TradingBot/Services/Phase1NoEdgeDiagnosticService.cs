using System.Text.Json;
using TradingBot.Api;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record Phase1NoEdgeCompact(int NearBreakEven, int ShadowPositiveAtMinEdge0, int ShadowPositiveAtMinEdge005, decimal? BestRawEdge, decimal? P99AfterSafetyEdge, string Diagnosis)
{ public static Phase1NoEdgeCompact Empty { get; } = new(0,0,0,null,null,"InsufficientValidPricing"); }

/// <summary>Export-only analysis with no dependency on paper execution, signing, or order placement.</summary>
public static class Phase1NoEdgeDiagnosticService
{
    private static readonly object Sync=new(); private static DateTime _windowStart=DateTime.UtcNow,_lastHistory=DateTime.MinValue;
    public static Phase1NoEdgeCompact Current { get; private set; }=Phase1NoEdgeCompact.Empty;
    public static void Update(OpportunityFamilyRankingSnapshot ranking,FocusUniverseSnapshot focus,RuntimeHealthSnapshot h,TradingBotOptions options,string root)
    {
        if(!string.Equals(options.RuntimeProfile,RuntimeProfileService.ReducedDiagnosticsPaperPhase1,StringComparison.OrdinalIgnoreCase))return;
        lock(Sync){var now=DateTime.UtcNow;var min=options.PaperDiagnosticsLimited.MinEdgeOverride;
        var rows=focus.Items.Where(x=>x.ValidPriced&&x.CurrentRawEdge.HasValue&&x.CurrentAfterCostEdge.HasValue).OrderByDescending(x=>x.CurrentAfterSafetyEdge).ToArray();
        var edges=rows.Select(x=>x.CurrentAfterSafetyEdge).OrderBy(x=>x).ToArray();var cap=PaperPhase1PositiveCaptureService.Current;
        var reasons=rows.SelectMany(x=>Split(x.LastRejectedReason)).ToArray();int RC(string s)=>reasons.Count(x=>x.Contains(s,StringComparison.OrdinalIgnoreCase));
        var top=reasons.GroupBy(x=>x,StringComparer.OrdinalIgnoreCase).OrderByDescending(x=>x.Count()).FirstOrDefault();
        decimal? raw=rows.Select(x=>x.CurrentRawEdge).Max(),cost=rows.Select(x=>x.CurrentAfterCostEdge).Max(),safe=rows.Select(x=>(decimal?)x.CurrentAfterSafetyEdge).Max();
        var diagnosis=Diagnose(raw,cost,safe,min,rows.Length,cap.InvalidArtifactsTotal);int Shadow(decimal threshold)=>rows.Count(x=>x.CurrentAfterSafetyEdge>=threshold);
        var coverage=h.ReducedUniverseMaxMarkets<=0?0m:Math.Min(1m,(decimal)h.ReducedUniverseMarkets/h.ReducedUniverseMaxMarkets);
        var payload=new{generatedAtUtc=now,windowMinutes=30,windowStartUtc=_windowStart,diagnosticsOnly=true,paperOpenAllowed=false,
          gateCandidatesSeen=h.PaperPhase1LadderSeen,ladderValidPriced=h.PaperPhase1LadderValidPriced,ladderPositiveBeforeCost=h.PaperPhase1LadderPositiveBeforeCost,
          ladderPositiveAfterCost=h.PaperPhase1LadderPositiveAfterCost,ladderPositiveAfterSafety=h.PaperPhase1LadderPositiveAfterSafety,nearBreakEvenCount=rows.Count(x=>x.CurrentAfterSafetyEdge>=-.003m),
          bestRawEdge=raw,bestAfterCostEdge=cost,bestAfterSafetyEdge=safe,distanceToMinEdge=safe.HasValue?min-safe:(decimal?)null,medianAfterSafetyEdge=P(edges,.5),p95AfterSafetyEdge=P(edges,.95),p99AfterSafetyEdge=P(edges,.99),
          topRejectReason=top?.Key??h.PaperPhase1LadderTopBlockingReason,topRejectReasonCount=top?.Count()??h.PaperPhase1LadderTopBlockingReasonCount,
          missingYesAskCount=Count(cap,"MissingYesAsk"),missingNoAskCount=Count(cap,"MissingNoAsk"),depthInsufficientCount=Count(cap,"DepthInsufficient"),fillFailedCount=Count(cap,"FillFailed"),riskFailedCount=Count(cap,"RiskFailed"),staleOrderbookCount=Count(cap,"StaleOrderbook"),suspiciousYesNoAskSumCount=Count(cap,"SuspiciousYesNoAskSum"),
          validButBelowMinEdgeCount=rows.Count(x=>x.CurrentAfterSafetyEdge<min),wouldPassIfNoFeesCount=rows.Count(x=>x.CurrentRawEdge>=min&&x.CurrentAfterCostEdge<min),wouldPassIfNoSafetyHaircutCount=rows.Count(x=>x.CurrentAfterCostEdge>=min&&x.CurrentAfterSafetyEdge<min),
          wouldPassIfMinEdgeZeroCount=Shadow(0),wouldPassIfMinEdgeHalfPercentCount=Shadow(.005m),wouldPassIfMinEdgeOnePercentCount=Shadow(.01m),mostCommonBlocker=top?.Key??diagnosis,diagnosis,
          shadowThresholds=new{diagnosticsOnly=true,paperOpenAllowed=false,shadowPositiveAtMinEdge0=Shadow(0),shadowPositiveAtMinEdge0_0025=Shadow(.0025m),shadowPositiveAtMinEdge0_005=Shadow(.005m),shadowPositiveAtMinEdge0_0075=Shadow(.0075m),shadowPositiveAtMinEdge0_01=Shadow(.01m),shadowPositiveBeforeCost=rows.Count(x=>x.CurrentRawEdge>0),shadowPositiveAfterCost=rows.Count(x=>x.CurrentAfterCostEdge>0),shadowPositiveAfterSafety=rows.Count(x=>x.CurrentAfterSafetyEdge>0)},
          strategyScopeComparison=new[]{"SingleMarketBuyBoth","VerifiedMultiOutcome","AutoCandidateMultiOutcome","MultiOutcomeNearMiss"}.Select(x=>Strategy(x,ranking,rows)),
          reducedUniverseCoverage=new{reducedUniverseMarkets=h.ReducedUniverseMarkets,reducedUniverseMaxMarkets=h.ReducedUniverseMaxMarkets,discoveryMode="ReducedUniverseDiagnosticsOnly",universeCoverageEstimate=coverage,marketsSkippedByDiscovery=Math.Max(0,h.ReducedUniverseMaxMarkets-h.ReducedUniverseMarkets),marketsSkippedByOrderbookHealth=focus.SkippedByOrderbookHealth,marketsSkippedByMissingBook=Count(cap,"MissingBook"),marketsSkippedByStaleOrderbook=Count(cap,"StaleOrderbook"),validPricedRate=Rate(h.PaperPhase1LadderValidPriced,h.PaperPhase1LadderSeen),positiveRate=Rate(h.PaperPhase1LadderPositiveAfterSafety,h.PaperPhase1LadderValidPriced),paperEligibleRate=Rate(h.PaperPhase1LadderPaperEligible,h.PaperPhase1LadderValidPriced)},processRunId=h.ProcessRunId};
        Write(Path.Combine(root,"exports/phase1-no-edge-diagnostic-latest.json"),payload);
        Write(Path.Combine(root,"exports/phase1-near-misses-latest.json"),rows.Take(50).Select(x=>new{x.WatchlistId,candidateId=x.WatchlistId,marketId=x.MarketIdOrGroupKey,x.Strategy,rawEdge=x.CurrentRawEdge,afterCostEdge=x.CurrentAfterCostEdge,afterSafetyEdge=x.CurrentAfterSafetyEdge,distanceToMinEdge=min-x.CurrentAfterSafetyEdge,yesAsk=(decimal?)null,noAsk=(decimal?)null,yesNoAskSum=(decimal?)null,feesOrCostDrag=x.CurrentRawEdge-x.CurrentAfterCostEdge,safetyHaircut=x.CurrentAfterCostEdge-x.CurrentAfterSafetyEdge,depthAvailable=x.ExecutionReady,fillPassed=x.ExecutionReady,riskPassed=x.ExecutionReady,firstBlockingReason=x.LastRejectedReason,allBlockingReasons=Split(x.LastRejectedReason),lastSeenUtc=x.LastSeenUtc}));
        Write(Path.Combine(root,"exports/phase1-invalid-artifacts-latest.json"),cap.TopInvalidArtifacts.Select(x=>new{x.CandidateId,x.MarketId,apparentEdge=x.RawEdge,firstReason=x.FirstBlockingReason,allReasons=x.AllBlockingReasons,missingYesAsk=!x.HasYesAsk,missingNoAsk=!x.HasNoAsk,x.SuspiciousYesNoAskSum,depthInsufficient=!x.DepthSufficient,fillFailed=!x.FillPassed,riskFailed=!x.RiskPassed,paperOpenAllowed=false}));
        if((now-_lastHistory).TotalMinutes>=30){Append(Path.Combine(root,"exports/phase1-no-edge-diagnostic-history.jsonl"),payload);_lastHistory=now;_windowStart=now;}
        Current=new(rows.Count(x=>x.CurrentAfterSafetyEdge>=-.003m),Shadow(0),Shadow(.005m),raw,P(edges,.99),diagnosis);
        CleanPositiveToGateReconciliationService.Update(rows,min,root);}}
    private static object Strategy(string name,OpportunityFamilyRankingSnapshot r,IReadOnlyList<FocusUniverseItem> all){var f=r.PricedFamilies.Where(x=>Map(x.Strategy)==name).ToArray();var a=all.Where(x=>Map(x.Strategy)==name).ToArray();return new{strategy=name,diagnosticsOnly=true,paperOpenAllowed=false,validPriced=f.Sum(x=>x.ValidPriced),positiveBeforeCost=a.Count(x=>x.CurrentRawEdge>0),positiveAfterCost=a.Count(x=>x.CurrentAfterCostEdge>0),positiveAfterSafety=a.Count(x=>x.CurrentAfterSafetyEdge>0),executableLike=f.Sum(x=>x.ExecutionReady),blockedByPricing=f.Sum(x=>x.MissingPricingCount),blockedByVerification=f.Sum(x=>x.UnverifiedCount),blockedByDepth=a.Count(x=>x.LastRejectedReason.Contains("Depth",StringComparison.OrdinalIgnoreCase)),blockedByFill=a.Count(x=>x.LastRejectedReason.Contains("Fill",StringComparison.OrdinalIgnoreCase)),blockedByRisk=a.Count(x=>x.LastRejectedReason.Contains("Risk",StringComparison.OrdinalIgnoreCase)),bestAfterSafetyEdge=a.Select(x=>(decimal?)x.CurrentAfterSafetyEdge).Max(),bestReason=f.OrderByDescending(x=>x.Samples).FirstOrDefault()?.TopRejectedReason??"NoCandidates"};}
    private static string Map(string s)=>s.Contains("Auto",StringComparison.OrdinalIgnoreCase)?"AutoCandidateMultiOutcome":s.Contains("Verified",StringComparison.OrdinalIgnoreCase)?"VerifiedMultiOutcome":s.Contains("Multi",StringComparison.OrdinalIgnoreCase)?"MultiOutcomeNearMiss":"SingleMarketBuyBoth";
    private static string Diagnose(decimal? r,decimal? c,decimal? s,decimal m,int valid,int artifacts)=>valid==0?(artifacts>0?"PositiveSignalsAreInvalidArtifacts":"InsufficientValidPricing"):r<m?"BelowMinEdgeBeforeCost":c<m?"BelowMinEdgeAfterCost":s<m?"BelowMinEdgeAfterCostAndSafety":"RealGateMayHaveEligibleEdge";
    private static string[] Split(string s)=>string.IsNullOrWhiteSpace(s)||s=="None"?[]:s.Split(['|',',',';'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
    private static int Count(PaperPhase1PositiveCaptureState s,string reason)=>s.TopInvalidArtifacts.Concat(s.TopCaptures).Count(x=>x.AllBlockingReasons.Contains(reason,StringComparer.OrdinalIgnoreCase));
    private static decimal? P(decimal[] a,double p)=>a.Length==0?null:a[(int)Math.Clamp(Math.Ceiling(p*a.Length)-1,0,a.Length-1)];private static decimal Rate(long n,long d)=>d<=0?0:(decimal)n/d;
    private static void Write(string p,object v){Directory.CreateDirectory(Path.GetDirectoryName(p)!);var t=p+".tmp";File.WriteAllText(t,JsonSerializer.Serialize(v,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));File.Move(t,p,true);}private static void Append(string p,object v){Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.AppendAllText(p,JsonSerializer.Serialize(v)+Environment.NewLine);}
}
