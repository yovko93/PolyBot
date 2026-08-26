using System.Text.Json;

namespace TradingBot.Services;

public sealed record Phase1StrategyCompact(string BestShadowStrategy5m, decimal? BestShadowAfterSafetyEdge5m,
    int BestShadowExecutableLike5m, string BestShadowTopBlocker5m, string BestPaperStrategy5m,
    decimal? BestPaperAfterSafetyEdge5m, string BestPaperTopBlocker5m, string StrategyExpansionDiagnosis)
{
    public static Phase1StrategyCompact Empty { get; } = new("None",null,0,"NoCandidates","SingleMarketBuyBoth",null,"NoCandidates","PaperStrategyBelowMinEdge");
}

/// <summary>Export-only diagnostics. It has no dependency on eligibility, paper opening, signing, or execution.</summary>
public static class Phase1StrategyExpansionDiagnosticService
{
    private static readonly object Sync=new();
    private static readonly string[] Strategies=["SingleMarketBuyBoth","VerifiedMultiOutcome","AutoCandidateMultiOutcome","MultiOutcomeNearMiss"];
    private static OpportunityFamilyRankingSnapshot? _ranking; private static FocusUniverseSnapshot? _focus;
    private static DateTime _windowStart=DateTime.UtcNow,_last30=DateTime.MinValue;
    public static Phase1StrategyCompact Current { get; private set; }=Phase1StrategyCompact.Empty;

    public static void Observe(OpportunityFamilyRankingSnapshot ranking,FocusUniverseSnapshot focus){lock(Sync){_ranking=ranking;_focus=focus;}}

    public static Phase1StrategyCompact PublishFiveMinute(string root,decimal minEdge,DateTime now,string runId)
    { lock(Sync) {
        var rows=Strategies.Select(s=>Build(s,_ranking,_focus)).ToArray();
        var paper=rows.Single(x=>x.PaperOpenAllowed); var shadow=rows.Where(x=>!x.PaperOpenAllowed).OrderByDescending(x=>x.BestAfterSafetyEdge5m??decimal.MinValue).First();
        var diagnosis=Diagnose(paper,shadow,minEdge);
        Current=new(shadow.Strategy,shadow.BestAfterSafetyEdge5m,shadow.ExecutableLike5m,shadow.TopBlocker5m,paper.Strategy,paper.BestAfterSafetyEdge5m,paper.TopBlocker5m,diagnosis);
        var payload=new{generatedAtUtc=now,windowMinutes=5,windowStartUtc=_windowStart,windowEndUtc=now,processRunId=runId,productionMinEdge=minEdge,strategies=rows};
        Write(Path.Combine(root,"exports/phase1-strategy-comparison-latest.json"),payload);
        Append(Path.Combine(root,"exports/phase1-strategy-comparison-history.jsonl"),payload);
        var candidates=(_focus?.Items??[]).Where(x=>Map(x.Strategy,x.FamilyType)!="SingleMarketBuyBoth").OrderByDescending(x=>x.CurrentAfterSafetyEdge).Take(50).Select(x=>Candidate(x,minEdge));
        Write(Path.Combine(root,"exports/phase1-shadow-near-misses-latest.json"),new{generatedAtUtc=now,windowMinutes=5,diagnosticsOnly=true,paperOpenAllowed=false,processRunId=runId,candidates});
        if(_last30==DateTime.MinValue||(now-_last30).TotalMinutes>=30){
            var shadows=rows.Where(x=>!x.PaperOpenAllowed).ToArray();
            Write(Path.Combine(root,"exports/phase1-strategy-expansion-diagnostic-latest.json"),new{generatedAtUtc=now,windowMinutes=30,bestPaperStrategy=paper.Strategy,bestPaperAfterSafetyEdge=paper.BestAfterSafetyEdge5m,bestShadowStrategy=shadow.Strategy,bestShadowAfterSafetyEdge=shadow.BestAfterSafetyEdge5m,bestShadowExecutableLike=shadow.ExecutableLike5m,shadowPositiveAfterSafetyTotal=shadows.Sum(x=>x.PositiveAfterSafety5m),shadowExecutableLikeTotal=shadows.Sum(x=>x.ExecutableLike5m),shadowBlockedByPricing=shadows.Sum(x=>x.BlockedByPricing5m),shadowBlockedByVerification=shadows.Sum(x=>x.BlockedByVerification5m),shadowBlockedByDepth=shadows.Sum(x=>x.BlockedByDepth5m),shadowBlockedByFill=shadows.Sum(x=>x.BlockedByFill5m),shadowBlockedByRisk=shadows.Sum(x=>x.BlockedByRisk5m),recommendedNextAction=Recommend(rows,minEdge),processRunId=runId}); _last30=now;
        }
        _windowStart=now; return Current;
    }}

    private static StrategyRow Build(string strategy,OpportunityFamilyRankingSnapshot? ranking,FocusUniverseSnapshot? focus)
    {
        var families=(ranking?.PricedFamilies??[]).Concat(ranking?.UnpricedFamilies??[]).Where(x=>Map(x.Strategy,x.FamilyType)==strategy).ToArray();
        var items=(focus?.Items??[]).Where(x=>Map(x.Strategy,x.FamilyType)==strategy).ToArray();
        var valid=items.Where(x=>x.ValidPriced&&x.CurrentRawEdge.HasValue&&x.CurrentAfterCostEdge.HasValue).ToArray();
        var reasons=items.SelectMany(x=>Split(x.LastRejectedReason)).Concat(families.SelectMany(x=>Enumerable.Repeat(x.TopRejectedReason,Math.Max(1,x.Samples)))).Where(x=>x!="None").ToArray();
        var top=reasons.GroupBy(x=>x,StringComparer.OrdinalIgnoreCase).OrderByDescending(x=>x.Count()).ThenBy(x=>x.Key,StringComparer.OrdinalIgnoreCase).FirstOrDefault()?.Key??"NoCandidates";
        var safe=valid.Select(x=>x.CurrentAfterSafetyEdge).OrderBy(x=>x).ToArray(); bool paper=strategy=="SingleMarketBuyBoth";
        bool Passed(FocusUniverseItem x,string b)=>x.ValidPriced&&!x.LastRejectedReason.Contains(b,StringComparison.OrdinalIgnoreCase);
        return new(strategy,Math.Max(items.Length,families.Sum(x=>x.Samples)),Math.Max(valid.Length,families.Sum(x=>x.ValidPriced)),valid.Count(x=>x.CurrentRawEdge>0),valid.Count(x=>x.CurrentAfterCostEdge>0),valid.Count(x=>x.CurrentAfterSafetyEdge>0),valid.Count(x=>x.CurrentAfterSafetyEdge>=0),valid.Count(x=>x.CurrentAfterSafetyEdge>=.005m),Max(valid.Select(x=>x.CurrentRawEdge)),Max(valid.Select(x=>x.CurrentAfterCostEdge)),Max(valid.Select(x=>(decimal?)x.CurrentAfterSafetyEdge))??Max(families.Select(x=>x.BestAfterSafetyEdge)),P(safe,.95),P(safe,.99),Math.Max(items.Count(x=>x.ExecutionReady),families.Sum(x=>x.ExecutionReady)),items.Count(x=>Passed(x,"Depth")),items.Count(x=>Passed(x,"Fill")),items.Count(x=>Passed(x,"Risk")),paper?items.Count(x=>x.ExecutionReady):0,paper?items.Count(x=>x.PaperOpened):0,top,!paper,paper,families.Sum(x=>x.MissingPricingCount),families.Sum(x=>x.UnverifiedCount),items.Count(x=>x.LastRejectedReason.Contains("Depth",StringComparison.OrdinalIgnoreCase)),items.Count(x=>x.LastRejectedReason.Contains("Fill",StringComparison.OrdinalIgnoreCase)),items.Count(x=>x.LastRejectedReason.Contains("Risk",StringComparison.OrdinalIgnoreCase)));
    }
    private static object Candidate(FocusUniverseItem x,decimal min){var strategy=Map(x.Strategy,x.FamilyType);var reasons=Split(x.LastRejectedReason);var vr=reasons.FirstOrDefault(r=>r.Contains("Verif",StringComparison.OrdinalIgnoreCase))??"None";return new{candidateId=x.WatchlistId,marketId=x.MarketIdOrGroupKey,strategy,rawEdge=x.CurrentRawEdge,afterCostEdge=x.CurrentAfterCostEdge,afterSafetyEdge=x.CurrentAfterSafetyEdge,distanceToMinEdge=min-x.CurrentAfterSafetyEdge,isPaperEligibleStrategy=false,paperOpenAllowed=false,executableLike=x.ExecutionReady,depthPassed=!Has(reasons,"Depth"),fillPassed=!Has(reasons,"Fill"),riskPassed=!Has(reasons,"Risk"),firstBlockingReason=reasons.FirstOrDefault()??"None",allBlockingReasons=reasons,verificationConfidence=vr=="None"?"Unknown":"Low",verificationReason=vr,lastSeenUtc=x.LastSeenUtc};}
    private static string Diagnose(StrategyRow p,StrategyRow s,decimal min)
    {
        var v=VerifiedMultiOutcomeDiscoveryDiagnostics.Current;
        if(v.Discovered==0)return "VerifiedMultiOutcomeDiscoveryBlocked";
        if(v.Evaluated>0&&v.ValidPriced==0&&v.Reason("VerifiedGroupMissingOrderbooks")>0)return "VerifiedMultiOutcomeMissingOrderbooks";
        if(v.ValidPriced>0&&v.PositiveAfterSafety==0)return "VerifiedMultiOutcomeBelowMinEdge";
        if(v.PositiveAfterSafety>0&&v.ExecutableLike==0)return "VerifiedMultiOutcomeHasEdgeButNotExecutable";
        if(v.ExecutableLike>0)return "VerifiedMultiOutcomeShadowCandidateFound";
        return (p.BestAfterSafetyEdge5m??decimal.MinValue)>=min?"PaperStrategyHasEdge":"VerifiedMultiOutcomeDiscoveryBlocked";
    }
    private static string Recommend(StrategyRow[] rows,decimal min){var s=rows.Where(x=>!x.PaperOpenAllowed).OrderByDescending(x=>x.BestAfterSafetyEdge5m??decimal.MinValue).First();if(s.Strategy=="VerifiedMultiOutcome"&&s.ExecutableLike5m>0&&s.BestAfterSafetyEdge5m>=min)return"PromoteVerifiedMultiOutcomeToPaperCandidateAfterFixtureTests";if(s.Strategy=="AutoCandidateMultiOutcome"&&s.BlockedByVerification5m>0)return"ImproveAutoCandidateVerification";if(s.BlockedByPricing5m>0&&s.ValidPriced5m==0)return"ImprovePricingCoverage";if(s.BlockedByDepth5m+s.BlockedByFill5m>0)return"ImproveDepthOrFillModel";if(rows.All(x=>!x.BestAfterSafetyEdge5m.HasValue))return"ExpandUniverseCoverage";if(rows.All(x=>(x.BestAfterSafetyEdge5m??decimal.MinValue)<min))return"NoEdgeAcrossStrategies";return"KeepMonitoringSingleMarket";}
    private static string Map(string s,string f=""){var v=s+" "+f;if(v.Contains("NearMiss",StringComparison.OrdinalIgnoreCase))return"MultiOutcomeNearMiss";if(v.Contains("Auto",StringComparison.OrdinalIgnoreCase))return"AutoCandidateMultiOutcome";if(v.Contains("Verified",StringComparison.OrdinalIgnoreCase))return"VerifiedMultiOutcome";if(v.Contains("Multi",StringComparison.OrdinalIgnoreCase))return"MultiOutcomeNearMiss";return"SingleMarketBuyBoth";}
    private static bool Has(string[] a,string v)=>a.Any(x=>x.Contains(v,StringComparison.OrdinalIgnoreCase)); private static string[] Split(string s)=>string.IsNullOrWhiteSpace(s)||s=="None"?[]:s.Split(['|',',',';'],StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
    private static decimal? Max(IEnumerable<decimal?> v){var a=v.Where(x=>x.HasValue).Select(x=>x!.Value).ToArray();return a.Length==0?null:a.Max();} private static decimal? P(decimal[] a,double p)=>a.Length==0?null:a[(int)Math.Clamp(Math.Ceiling(p*a.Length)-1,0,a.Length-1)];
    private static void Write(string p,object v){Directory.CreateDirectory(Path.GetDirectoryName(p)!);var t=p+".tmp";File.WriteAllText(t,JsonSerializer.Serialize(v,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));File.Move(t,p,true);} private static void Append(string p,object v){Directory.CreateDirectory(Path.GetDirectoryName(p)!);File.AppendAllText(p,JsonSerializer.Serialize(v,new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase})+Environment.NewLine);}
    private sealed record StrategyRow(string Strategy,int Seen5m,int ValidPriced5m,int PositiveBeforeCost5m,int PositiveAfterCost5m,int PositiveAfterSafety5m,int ShadowPositiveAtMinEdge0_5m,int ShadowPositiveAtMinEdge0_005_5m,decimal? BestRawEdge5m,decimal? BestAfterCostEdge5m,decimal? BestAfterSafetyEdge5m,decimal? P95AfterSafety5m,decimal? P99AfterSafety5m,int ExecutableLike5m,int DepthPassed5m,int FillPassed5m,int RiskPassed5m,int PaperEligible5m,int PaperOpened5m,string TopBlocker5m,bool DiagnosticsOnly,bool PaperOpenAllowed,int BlockedByPricing5m,int BlockedByVerification5m,int BlockedByDepth5m,int BlockedByFill5m,int BlockedByRisk5m)
    {
        private VerifiedDiscoverySnapshot V => VerifiedMultiOutcomeDiscoveryDiagnostics.Current;
        public int? DiscoveryInputGroups => Strategy=="VerifiedMultiOutcome"?V.Discovered:null;
        public int? VerifiedGroupsAvailable => Strategy=="VerifiedMultiOutcome"?V.Discovered-V.Reason("VerifiedGroupOutsideReducedUniverse"):null;
        public int? GroupsEvaluated => Strategy=="VerifiedMultiOutcome"?V.Evaluated:null;
        public int? GroupsValidPriced => Strategy=="VerifiedMultiOutcome"?V.ValidPriced:null;
        public int? GroupsPositiveAfterSafety => Strategy=="VerifiedMultiOutcome"?V.PositiveAfterSafety:null;
        public int? GroupsExecutableLike => Strategy=="VerifiedMultiOutcome"?V.ExecutableLike:null;
        public int? GroupsBlockedBeforePricing => Strategy=="VerifiedMultiOutcome"?V.Skipped:null;
        public int? GroupsBlockedByVerification => Strategy=="VerifiedMultiOutcome"?BlockedByVerification5m:null;
        public int? GroupsBlockedByMissingOrderbook => Strategy=="VerifiedMultiOutcome"?V.Reason("VerifiedGroupMissingOrderbooks"):null;
        public int? GroupsBlockedByStaleOrderbook => Strategy=="VerifiedMultiOutcome"?V.Reason("VerifiedGroupStaleOrderbook"):null;
        public int? GroupsBlockedByDepth => Strategy=="VerifiedMultiOutcome"?BlockedByDepth5m:null;
        public int? GroupsBlockedByFill => Strategy=="VerifiedMultiOutcome"?BlockedByFill5m:null;
        public int? GroupsBlockedByRisk => Strategy=="VerifiedMultiOutcome"?BlockedByRisk5m:null;
        public decimal? BestAfterSafetyEdge => Strategy=="VerifiedMultiOutcome"?BestAfterSafetyEdge5m:null;
        public string? TopBlocker => Strategy=="VerifiedMultiOutcome"?V.TopBlocker:null;
    }
}
