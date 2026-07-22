using System.Text.Json;
using TradingBot.Api;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record PaperPhase1PositiveExcludedCandidate(string CandidateId, string MarketId, string Question,
    decimal YesAsk, decimal NoAsk, decimal RawEdge, decimal AfterCostEdge, decimal AfterSafetyEdge,
    bool TokenMappingVerified, bool HasBothBooks, bool HasYesAsk, bool HasNoAsk, bool OrderbookStale,
    bool EdgeStable, bool DepthSufficient, bool FillPassed, bool RiskPassed, bool PaperEligible,
    string FirstBlockingReason, IReadOnlyList<string> AllBlockingReasons);

public sealed record PaperPhase1PositiveReconciliationState(bool Enabled = true,
    long StrategyPositiveAfterSafety = 0, decimal? StrategyBestAfterSafetyEdge = null,
    string StrategyBestCandidateId = "None", string StrategyBestCandidateReason = "None",
    long RealWatchPositiveAfterSafety = 0, decimal? RealWatchBestAfterSafetyEdge = null,
    string RealWatchBestCandidateId = "None", bool Mismatch = false, string MismatchReason = "None",
    bool MismatchBlocking = false, bool Consistent = true, long PositiveExcludedTotal = 0,
    IReadOnlyDictionary<string,long>? PositiveExcludedByReason = null,
    string BestExcludedCandidateId = "None", string BestExcludedMarketId = "None",
    decimal? BestExcludedAfterSafetyEdge = null, string BestExcludedReason = "None",
    string BestExcludedAllReasons = "None", long RealPositiveAfterSafetyRaw = 0,
    long RealPositiveAfterSafetyValid = 0, long RealPositiveAfterSafetyExecutableLike = 0,
    long RealPositiveAfterSafetyExcluded = 0, IReadOnlyList<PaperPhase1PositiveExcludedCandidate>? TopPositiveCandidates = null)
{
    public IReadOnlyDictionary<string,long> ExcludedByReason => PositiveExcludedByReason ?? new Dictionary<string,long>();
    public IReadOnlyList<PaperPhase1PositiveExcludedCandidate> TopCandidates => TopPositiveCandidates ?? Array.Empty<PaperPhase1PositiveExcludedCandidate>();
}

public sealed class PaperPhase1PositiveReconciliationService(TradingBotOptions options)
{
    public static PaperPhase1PositiveReconciliationState Latest { get; private set; } = new();
    private DateTime _lastLogUtc = DateTime.MinValue;

    public PaperPhase1PositiveReconciliationState Reconcile(BotRuntimeState state, RuntimeHealthSnapshot health)
    {
        health.StrategyCounters.TryGetValue("SingleMarketBuyBoth", out var strategy);
        strategy ??= health.StrategyCounters.Values.FirstOrDefault(x => x.StrategyName.Equals("SingleMarketBuyBoth", StringComparison.OrdinalIgnoreCase));
        var strategyPositive = strategy?.PositiveEdges ?? health.SingleMarketPositiveAfterSafety;
        var strategyBest = strategy?.BestEdge ?? health.SingleMarketBestAfterSafetyEdge;
        var minEdge = health.PaperPhase1MinEdge;
        var auditCandidates = state.SingleMarketSnapshot.TopOpportunityAuditNearMisses
            .Where(x => x.AfterSafetyEdge >= minEdge)
            .OrderByDescending(x => x.AfterSafetyEdge)
            .Select(x => Classify(x, state.SingleMarketSnapshot.ScanId, minEdge))
            .ToArray();
        var auditedMarkets = auditCandidates.Select(x => x.MarketId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var snapshotCandidates = state.SingleMarketSnapshot.PositiveCandidates
            .Where(x => x.EdgePerShare >= minEdge && !auditedMarkets.Contains(x.MarketId))
            .Select(x => ClassifySnapshotPositive(x, state.SingleMarketSnapshot.ScanId))
            .ToArray();
        var audits = auditCandidates.Concat(snapshotCandidates).OrderByDescending(x => x.AfterSafetyEdge).ToArray();
        var realPositive = health.PaperPhase1LadderPositiveAfterSafety;
        var eligibleAuditMismatch = realPositive == 0 ? audits.LongCount(x => x.PaperEligible) : 0;
        if (eligibleAuditMismatch > 0)
            audits = audits.Select(x => x.PaperEligible
                ? x with { PaperEligible=false, FirstBlockingReason="CandidateExcludedFromRealWatchBug", AllBlockingReasons=new[]{"CandidateExcludedFromRealWatchBug"} }
                : x).ToArray();
        var excluded = audits.Where(x => !x.PaperEligible).ToList();
        var missingFromSnapshot = Math.Max(0, strategyPositive - realPositive - excluded.Count);
        var counts = excluded.GroupBy(x => x.FirstBlockingReason, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => (long)x.Count(), StringComparer.OrdinalIgnoreCase);
        if (missingFromSnapshot > 0) counts["CandidateSnapshotMismatch"] = missingFromSnapshot;
        var excludedTotal = counts.Values.Sum();
        var best = excluded.FirstOrDefault();
        var mismatch = strategyPositive != realPositive;
        var unexplained = Math.Max(0, strategyPositive - realPositive - excludedTotal);
        var blockingReason = eligibleAuditMismatch > 0 || excluded.Any(x => x.FirstBlockingReason is "CandidateExcludedFromRealWatchBug" or "Unknown") || unexplained > 0;
        var reason = !mismatch ? "None" : blockingReason ? "PositiveCandidateReconciliationMissing" :
            missingFromSnapshot > 0 ? "ExplainedByDifferentAggregationWindow:CandidateSnapshotMismatch" : "ExplainedByCandidateGateExclusions";
        var bestReason = best?.FirstBlockingReason ?? (eligibleAuditMismatch > 0 ? "CandidateExcludedFromRealWatchBug" : missingFromSnapshot > 0 ? "CandidateSnapshotMismatch" : NormalizeBestReason(strategy?.BestCandidateReason, strategyBest, minEdge));
        var aggregateCandidateId = strategyPositive > 0 ? "SingleMarketBuyBoth:SnapshotAggregate" : "None";
        Latest = new(true, strategyPositive, strategyBest, best?.CandidateId ?? aggregateCandidateId, bestReason,
            realPositive, health.PaperPhase1LadderBestAfterSafetyEdge, health.PaperPhase1RealWatchBestCandidateId,
            mismatch, reason, blockingReason, !blockingReason, excludedTotal, counts,
            best?.CandidateId ?? aggregateCandidateId, best?.MarketId ?? "None", best?.AfterSafetyEdge ?? strategyBest,
            bestReason, best is null ? bestReason : string.Join("|", best.AllBlockingReasons), strategyPositive,
            Math.Max(0, strategyPositive - (strategy?.DataQualityRejected ?? 0)), audits.LongCount(x => x.PaperEligible),
            excludedTotal, audits.Take(10).ToArray());
        Export(health);
        MaybeLog();
        return Latest;
    }

    public static string NormalizeBestReason(string? reason, decimal? edge, decimal minEdge)
        => edge >= minEdge && string.Equals(reason, "BelowMinEdge", StringComparison.OrdinalIgnoreCase)
            ? "CandidateSnapshotMismatch" : string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason;

    private static PaperPhase1PositiveExcludedCandidate Classify(SingleMarketOpportunityAuditDto x, long scanId, decimal minEdge)
    {
        var reasons = new List<string>();
        var dq = string.IsNullOrWhiteSpace(x.DataQualityReason) ? x.RejectedReason : x.DataQualityReason;
        if (dq.Contains("Suspicious", StringComparison.OrdinalIgnoreCase)) reasons.Add("SuspiciousYesNoAskSum");
        if (dq.Contains("Token", StringComparison.OrdinalIgnoreCase)) reasons.Add("TokenOutcomeMappingUnverified");
        if (dq.Contains("Stale", StringComparison.OrdinalIgnoreCase)) reasons.Add("StaleOrderbook");
        if (dq.Contains("MissingYes", StringComparison.OrdinalIgnoreCase)) reasons.Add("MissingYesAsk");
        if (dq.Contains("MissingNo", StringComparison.OrdinalIgnoreCase)) reasons.Add("MissingNoAsk");
        if (dq.Contains("MissingBook", StringComparison.OrdinalIgnoreCase)) reasons.Add("MissingBook");
        if (!string.IsNullOrWhiteSpace(x.DataQualityReason) && reasons.Count == 0) reasons.Add("InvalidRawSpike");
        if (x.RejectedReason.Contains("Stable", StringComparison.OrdinalIgnoreCase)) reasons.Add("EdgeNotStable");
        if (!x.DepthPassed) reasons.Add("DepthInsufficient");
        if (!x.FillPassed) reasons.Add("FillFailed");
        if (!x.RiskPassed) reasons.Add("RiskFailed");
        if (!x.PaperDiagnosticsLimitedGatePassed) reasons.Add("PaperDiagnosticsLimitedIneligible");
        if (x.AfterSafetyEdge < minEdge) reasons.Add("BelowMinEdge");
        var eligible = reasons.Count == 0;
        if (!eligible && reasons[0] == "BelowMinEdge" && x.AfterSafetyEdge >= minEdge) reasons[0] = "CandidateSnapshotMismatch";
        if (!eligible && reasons.Count == 0) reasons.Add("CandidateExcludedFromRealWatchBug");
        return new($"SingleMarketBuyBoth:{x.MarketId}:{scanId}", x.MarketId, x.Title, x.YesAsk, x.NoAsk,
            x.RawEdge, x.AfterCostEdge, x.AfterSafetyEdge, !dq.Contains("Token", StringComparison.OrdinalIgnoreCase),
            x.YesAsk > 0 && x.NoAsk > 0, x.YesAsk > 0, x.NoAsk > 0, dq.Contains("Stale", StringComparison.OrdinalIgnoreCase),
            !x.RejectedReason.Contains("Stable", StringComparison.OrdinalIgnoreCase), x.DepthPassed, x.FillPassed,
            x.RiskPassed, eligible, eligible ? "None" : reasons[0], reasons);
    }

    private static PaperPhase1PositiveExcludedCandidate ClassifySnapshotPositive(SingleMarketArbOpportunityDto x, long scanId)
    {
        var reasons = new List<string>();
        var stated = x.Reason ?? "";
        if (stated.Contains("Stable", StringComparison.OrdinalIgnoreCase)) reasons.Add("EdgeNotStable");
        if (stated.Contains("Depth", StringComparison.OrdinalIgnoreCase)) reasons.Add("DepthInsufficient");
        if (stated.Contains("Fill", StringComparison.OrdinalIgnoreCase) || x.FillSimulationStatus.Equals("Rejected", StringComparison.OrdinalIgnoreCase)) reasons.Add("FillFailed");
        if (stated.Contains("Risk", StringComparison.OrdinalIgnoreCase)) reasons.Add("RiskFailed");
        if (stated.Contains("Stale", StringComparison.OrdinalIgnoreCase)) reasons.Add("StaleOrderbook");
        if (reasons.Count == 0) reasons.Add("CandidateSnapshotMismatch");
        var edgeStable = (int)x.State >= (int)SingleMarketArbState.EdgeStable;
        var fillPassed = x.FillSimulationStatus.Equals("Passed", StringComparison.OrdinalIgnoreCase);
        return new($"SingleMarketBuyBoth:{x.MarketId}:{scanId}", x.MarketId, x.Question, x.YesAsk, x.NoAsk,
            1m-x.RawAskSum, x.EdgePerShare, x.EdgePerShare, true, x.YesAsk>0m&&x.NoAsk>0m, x.YesAsk>0m, x.NoAsk>0m,
            false, edgeStable, false, fillPassed, false, false, reasons[0], reasons);
    }

    private void Export(RuntimeHealthSnapshot h)
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "exports", "paper-phase1-positive-reconciliation-latest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var x = Latest;
        var payload = new { generatedAtUtc=DateTime.UtcNow, processRunId=ProcessRunContext.ProcessRunId,
            profile=options.RuntimeProfile, minEdge=h.PaperPhase1MinEdge,
            strategySummary=new { positiveAfterSafety=x.StrategyPositiveAfterSafety,bestAfterSafetyEdge=x.StrategyBestAfterSafetyEdge,bestCandidateId=x.StrategyBestCandidateId,bestCandidateReason=x.StrategyBestCandidateReason,bestCandidateValid=true,bestCandidatePriced=true,bestCandidateExecutableLike=x.RealPositiveAfterSafetyExecutableLike>0 },
            realWatch=new { positiveAfterSafety=x.RealWatchPositiveAfterSafety,paperEligible=h.PaperPhase1LadderPaperEligible,bestAfterSafetyEdge=x.RealWatchBestAfterSafetyEdge,bestCandidateId=x.RealWatchBestCandidateId,topBlockingReason=h.PaperPhase1LadderTopBlockingReason },
            opportunityFamilyRanking=new { positive=h.OpportunityFamilyPositiveFamilies,bestAfterSafetyEdge=h.OpportunityFamilyBestPricedAfterSafetyEdge,consistent=h.OpportunityFamilyRankingConsistent },
            edgeTransition=new { positive=h.EdgeTransitionPositiveCandidates,bestCurrentEdge=h.EdgeTransitionBestCurrentEdge,lastAlertReason=h.EdgeTransitionLastAlertReason,consistent=h.EdgeTransitionConsistent },
            mismatch=new { present=x.Mismatch,blocking=x.MismatchBlocking,reason=x.MismatchReason },
            positiveExcluded=new { total=x.PositiveExcludedTotal,byReason=x.ExcludedByReason,bestExcluded=new { candidateId=x.BestExcludedCandidateId,marketId=x.BestExcludedMarketId,afterSafetyEdge=x.BestExcludedAfterSafetyEdge,firstReason=x.BestExcludedReason,allReasons=x.BestExcludedAllReasons } },
            topPositiveCandidates=x.TopCandidates.Select((c,i)=>new { rank=i+1,c.CandidateId,c.MarketId,c.Question,c.YesAsk,c.NoAsk,sumAsk=c.YesAsk+c.NoAsk,c.RawEdge,c.AfterCostEdge,c.AfterSafetyEdge,c.TokenMappingVerified,c.HasBothBooks,c.HasYesAsk,c.HasNoAsk,c.OrderbookStale,c.EdgeStable,c.DepthSufficient,c.FillPassed,c.RiskPassed,c.PaperEligible,c.FirstBlockingReason,c.AllBlockingReasons }),
            safety=new { paperOpenAllowed=!x.MismatchBlocking,reason=x.MismatchBlocking?"PositiveCandidateMismatchBlocking":"ReconciliationConsistent",liveTradingDisabled=true,signingDisabled=LiveTradingGuard.SigningAttempts==0 } };
        File.WriteAllText(path, JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
    }

    private void MaybeLog()
    {
        if (Latest.StrategyPositiveAfterSafety <= 0 || DateTime.UtcNow-_lastLogUtc < TimeSpan.FromSeconds(60)) return;
        _lastLogUtc=DateTime.UtcNow;
        Console.WriteLine($"[PAPER_PHASE1_POSITIVE_RECONCILIATION] StrategyPositiveAfterSafety={Latest.StrategyPositiveAfterSafety} StrategyBestAfterSafetyEdge={Latest.StrategyBestAfterSafetyEdge:0.####} RealWatchPositiveAfterSafety={Latest.RealWatchPositiveAfterSafety} PositiveExcludedTotal={Latest.PositiveExcludedTotal} BestExcludedCandidateId={Latest.BestExcludedCandidateId} BestExcludedAfterSafetyEdge={Latest.BestExcludedAfterSafetyEdge:0.####} BestExcludedReason={Latest.BestExcludedReason} MismatchBlocking={Latest.MismatchBlocking.ToString().ToLowerInvariant()} ProcessRunId={ProcessRunContext.ProcessRunId}");
        foreach(var c in Latest.TopCandidates.Where(x=>!x.PaperEligible).Take(5)) Console.WriteLine($"[PAPER_PHASE1_POSITIVE_EXCLUDED] CandidateId={c.CandidateId} MarketId={c.MarketId} AfterSafetyEdge={c.AfterSafetyEdge:0.####} FirstReason={c.FirstBlockingReason} AllReasons={string.Join("|",c.AllBlockingReasons)} ProcessRunId={ProcessRunContext.ProcessRunId}");
        if(Latest.MismatchBlocking) Console.WriteLine($"[PAPER_PHASE1_POSITIVE_RECONCILIATION_BLOCKED] Reason={Latest.MismatchReason} ProcessRunId={ProcessRunContext.ProcessRunId}");
    }
}
