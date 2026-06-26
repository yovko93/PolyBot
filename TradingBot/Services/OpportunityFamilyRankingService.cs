using System.Text.Json;
using System.Text.RegularExpressions;
using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;

namespace TradingBot.Services;

public sealed record OpportunityFamilySummary(
    string FamilyKey,
    string Strategy,
    string FamilyType,
    string EventType,
    string MarketShape,
    int Samples,
    int ValidPriced,
    int InvalidOrUnpriced,
    int Positive,
    int ExecutionReady,
    int ShadowWouldOpen,
    int PaperOpened,
    decimal? BestRawEdge,
    decimal? BestAfterCostEdge,
    decimal? BestAfterSafetyEdge,
    decimal? P50AfterSafetyEdge,
    decimal? P90AfterSafetyEdge,
    decimal? P95AfterSafetyEdge,
    decimal? P99AfterSafetyEdge,
    decimal? MaxAfterSafetyEdge,
    decimal? AvgAfterSafetyEdge,
    int MissingPricingCount,
    int IncompleteCount,
    int ReviewOnlyCount,
    int DataQualityRejectedCount,
    int UnverifiedCount,
    int DifferentEventCount,
    int VerificationHigh,
    int VerificationMedium,
    int VerificationLow,
    int MissingLegCount,
    int BestVerificationScore,
    string VerificationConfidence,
    string TopRejectedReason,
    string BestMarketOrGroupKey,
    string BestTitle,
    string RecommendedAction);

public sealed record OpportunityFamilyRankingExport(
    DateTime GeneratedAtUtc,
    string ProcessRunId,
    TimeSpan Uptime,
    string DiscoveryMode,
    bool PaperDiagnosticsLimitedEligible,
    bool OrderbookStableNow,
    bool ReducedUniverseOrderbookStableNow,
    IReadOnlyList<object> RankedPricedFamilies,
    IReadOnlyList<object> RankedUnpricedFamilies,
    IReadOnlyList<string> Recommendations);

public sealed record OpportunityFamilyRankingSnapshot(
    bool Enabled,
    int Buckets,
    string BestPricedFamily,
    decimal? BestPricedAfterSafetyEdge,
    string BestUnpricedFamily,
    int BestUnpricedVerificationScore,
    int ClosestToBreakEvenCount,
    int PositiveFamilies,
    int ExecutableFamilies,
    string TopRecommendedAction,
    IReadOnlyList<OpportunityFamilySummary> PricedFamilies,
    IReadOnlyList<OpportunityFamilySummary> UnpricedFamilies);

public static class OpportunityFamilyRankingService
{
    private sealed record Sample(string Strategy,string FamilyType,string EventType,string MarketShape,int OutcomeCount,string VerificationConfidence,string RejectedReason,string PricingStatus,string Key,string Title,decimal? Raw,decimal? AfterCost,decimal? AfterSafety,int VerificationScore,bool Positive,bool Ready,bool Shadow,bool Paper);

    public static OpportunityFamilyRankingSnapshot Build(
        IReadOnlyList<SingleMarketOpportunityAuditDto> singleMarket,
        IReadOnlyList<VerifiedGroupDiagnosticDto> verified,
        IReadOnlyList<VerifiedGroupPricingDto> verifiedPricing,
        AutoCandidateVerificationSummary? auto)
    {
        var samples = new List<Sample>();
        samples.AddRange(singleMarket.Select(x => new Sample("SingleMarketBuyBoth", "SingleMarketBuyBoth", ClassifyEvent(x.Title), "binary", 2, "High", Reason(x.RejectedReason), Status(x.RejectedReason, true, x.AfterSafetyEdge), x.MarketId, x.Title, x.RawEdge, x.AfterCostEdge, x.AfterSafetyEdge, 100, x.AfterSafetyEdge > 0, x.ExecutableQty > 0 && x.FillPassed && x.DepthPassed && x.RiskPassed && x.PaperDiagnosticsLimitedGatePassed, false, false)));
        var pricingByKey = verifiedPricing.GroupBy(x => x.GroupKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var v in verified)
        {
            pricingByKey.TryGetValue(v.GroupKey, out var p);
            var edge = p?.NetEdge ?? v.BestEdge;
            var reason = Reason(p?.SkipReason ?? v.SkipReason);
            samples.Add(new Sample("VerifiedMultiOutcome", "VerifiedMultiOutcome", ClassifyEvent(v.GroupKey), v.MissingMarketCount > 0 ? "multiOutcomeIncomplete" : "mutuallyExclusiveBasket", Math.Max(v.RequiredMarketCount, v.ResolvedMarketCount), Confidence(edge, reason), reason, Status(reason, edge.HasValue, edge), v.GroupKey, v.GroupKey, p?.GrossEdge, p?.NetEdge, edge, edge.HasValue ? 80 : 40, edge > 0, string.Equals(reason, "Executable", StringComparison.OrdinalIgnoreCase), false, false));
        }
        if (auto is not null)
        {
            foreach (var a in auto.Candidates)
            {
                var familyType = a.VerificationCategory switch
                {
                    "AutoCandidateExactVerifiedMatch" => "AutoCandidateExactVerified",
                    "AutoCandidateSemanticMatchUnpriced" => "AutoCandidateSemanticUnpriced",
                    "AutoCandidatePartialOverlap" => "AutoCandidatePartialOverlap",
                    "AutoCandidateMissingLeg" => "AutoCandidateMissingLeg",
                    "AutoCandidateDifferentEvent" => "AutoCandidateDifferentEvent",
                    _ when a.MissingLegCount > 0 => "AutoCandidateMissingLeg",
                    _ => "MultiOutcomeNearMiss"
                };
                var shape = a.MissingLegCount > 0 ? "multiOutcomeIncomplete" : a.ExpectedLegCount > 2 || a.PresentLegCount > 2 ? "mutuallyExclusiveBasket" : "unknown";
                var pricingStatus = a.PricingSucceeded ? Status(a.BlockedReason, true, a.AfterSafetyEdge) : a.MissingLegCount > 0 ? "incomplete" : a.VerificationCategory.Contains("Unpriced", StringComparison.OrdinalIgnoreCase) ? "unpriced" : "missingPricing";
                samples.Add(new Sample(a.Strategy, familyType, ClassifyEvent(a.CandidateTitle + " " + a.GroupKey), shape, Math.Max(a.ExpectedLegCount, a.PresentLegCount), a.VerificationConfidence, Reason(a.BlockedReason), pricingStatus, a.GroupKey, a.CandidateTitle, a.RawEdge, a.AfterCostEdge, a.AfterSafetyEdge, a.VerificationScore, (a.AfterSafetyEdge ?? 0) > 0, a.ExecutableLike, a.WouldShadowOpen, false));
            }
        }

        var buckets = samples.GroupBy(x => string.Join("|", x.Strategy, x.FamilyType, x.EventType, x.MarketShape, x.OutcomeCount, x.VerificationConfidence, x.RejectedReason, x.PricingStatus), StringComparer.OrdinalIgnoreCase).Select(BuildBucket).ToList();
        var priced = buckets.Where(x => x.ValidPriced > 0).OrderByDescending(x => x.BestAfterSafetyEdge ?? decimal.MinValue).ThenByDescending(x => x.Samples).Take(50).ToList();
        var unpriced = buckets.Where(x => x.ValidPriced == 0).OrderByDescending(x => x.BestVerificationScore).ThenByDescending(x => x.Samples).Take(50).ToList();
        var bestPriced = priced.FirstOrDefault(); var bestUnpriced = unpriced.FirstOrDefault();
        return new OpportunityFamilyRankingSnapshot(true, buckets.Count, bestPriced?.FamilyKey ?? "N/A", bestPriced?.BestAfterSafetyEdge, bestUnpriced?.FamilyKey ?? "N/A", bestUnpriced?.BestVerificationScore ?? 0, priced.Count(x => (x.BestAfterSafetyEdge ?? -1m) >= -0.01m), priced.Count(x => x.Positive > 0), priced.Count(x => x.ExecutionReady > 0), bestPriced?.RecommendedAction ?? bestUnpriced?.RecommendedAction ?? "KeepMonitoring", priced, unpriced);
    }

    private static OpportunityFamilySummary BuildBucket(IGrouping<string, Sample> g)
    {
        var a = g.ToList(); var pricedEdges = a.Where(x => x.AfterSafety.HasValue && x.PricingStatus is "priced" or "notExecutable" or "reviewOnly").Select(x => x.AfterSafety!.Value).OrderBy(x => x).ToArray();
        var best = a.OrderByDescending(x => x.AfterSafety ?? decimal.MinValue).ThenByDescending(x => x.VerificationScore).First();
        var topReason = a.GroupBy(x => x.RejectedReason).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).First().Key;
        return new OpportunityFamilySummary(g.Key, best.Strategy, best.FamilyType, best.EventType, best.MarketShape, a.Count, pricedEdges.Length, a.Count - pricedEdges.Length, a.Count(x => x.Positive), a.Count(x => x.Ready), a.Count(x => x.Shadow), a.Count(x => x.Paper), a.Max(x => x.Raw), a.Max(x => x.AfterCost), pricedEdges.Length == 0 ? null : pricedEdges.Max(), P(pricedEdges,.50m), P(pricedEdges,.90m), P(pricedEdges,.95m), P(pricedEdges,.99m), pricedEdges.Length == 0 ? null : pricedEdges.Max(), pricedEdges.Length == 0 ? null : pricedEdges.Average(), a.Count(x => x.PricingStatus is "missingPricing" or "unpriced"), a.Count(x => x.PricingStatus == "incomplete"), a.Count(x => x.PricingStatus == "reviewOnly"), a.Count(x => x.PricingStatus == "dataQualityRejected"), a.Count(x => x.RejectedReason.Contains("Unverified", StringComparison.OrdinalIgnoreCase)), a.Count(x => x.FamilyType == "AutoCandidateDifferentEvent"), a.Count(x => x.VerificationConfidence == "High"), a.Count(x => x.VerificationConfidence == "Medium"), a.Count(x => x.VerificationConfidence == "Low"), a.Count(x => x.FamilyType.Contains("MissingLeg", StringComparison.OrdinalIgnoreCase)), a.Max(x => x.VerificationScore), best.VerificationConfidence, topReason, best.Key, best.Title, Recommend(best, topReason, pricedEdges));
    }

    public static OpportunityFamilyRankingExport ToExport(OpportunityFamilyRankingSnapshot s, RuntimeHealthSnapshot h) => new(DateTime.UtcNow, h.ProcessRunId, h.Uptime, h.DiscoverySelectedSource, h.PaperDiagnosticsLimitedEligible, h.OrderbookStableNow, h.ReducedUniverseOrderbookStableNow, s.PricedFamilies.Select((x,i)=>(object)new { rank=i+1, x.FamilyKey, x.Strategy, x.FamilyType, x.EventType, x.MarketShape, x.Samples, x.ValidPriced, x.BestAfterSafetyEdge, x.P50AfterSafetyEdge, x.P90AfterSafetyEdge, x.P95AfterSafetyEdge, x.P99AfterSafetyEdge, x.MaxAfterSafetyEdge, x.AvgAfterSafetyEdge, x.Positive, x.ExecutionReady, x.ShadowWouldOpen, x.PaperOpened, x.TopRejectedReason, x.BestMarketOrGroupKey, x.BestTitle, x.RecommendedAction }).ToArray(), s.UnpricedFamilies.Select((x,i)=>(object)new { rank=i+1, x.FamilyKey, x.Strategy, x.FamilyType, x.EventType, x.MarketShape, x.Samples, x.VerificationHigh, x.VerificationMedium, x.VerificationLow, bestVerificationScore=x.BestVerificationScore, x.MissingPricingCount, x.IncompleteCount, missingLegCount=x.MissingLegCount, x.DifferentEventCount, x.TopRejectedReason, x.BestMarketOrGroupKey, x.BestTitle, x.RecommendedAction }).ToArray(), s.PricedFamilies.Concat(s.UnpricedFamilies).GroupBy(x => x.RecommendedAction).OrderByDescending(x => x.Count()).Select(x => x.Key).Take(5).ToArray());

    public static void WriteExportAtomic(string path, OpportunityFamilyRankingExport export)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        for (var i = 0; i < 3; i++) { try { File.WriteAllText(tmp, json); File.Move(tmp, path, true); return; } catch when (i < 2) { Thread.Sleep(50); } }
    }

    private static decimal? P(decimal[] sorted, decimal p) => sorted.Length == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1)];
    private static string Reason(string? r) => string.IsNullOrWhiteSpace(r) ? "None" : r.Trim().Replace(' ', '_');
    private static string Status(string reason, bool priced, decimal? edge) => reason.Contains("Review", StringComparison.OrdinalIgnoreCase) ? "reviewOnly" : reason.Contains("DataQuality", StringComparison.OrdinalIgnoreCase) || reason.Contains("Suspicious", StringComparison.OrdinalIgnoreCase) ? "dataQualityRejected" : !priced ? "missingPricing" : edge.HasValue && edge.Value <= 0 ? "notExecutable" : "priced";
    private static string Confidence(decimal? edge, string reason) => edge.HasValue ? "High" : reason.Contains("Review", StringComparison.OrdinalIgnoreCase) ? "Medium" : "Low";
    private static string Recommend(Sample best, string reason, decimal[] pricedEdges) => pricedEdges.Length > 0 ? (pricedEdges[^1] > 0 ? "CandidateForFuturePaperOnlyAfterVerified" : "NoActionNegativeEdge") : best.FamilyType.Contains("MissingLeg", StringComparison.OrdinalIgnoreCase) ? "InvestigateMissingLegs" : best.PricingStatus.Contains("pricing", StringComparison.OrdinalIgnoreCase) || best.PricingStatus == "unpriced" ? "InvestigatePricing" : best.VerificationScore >= 80 ? "ManualReviewCandidateFamily" : "IgnoreLowQualityFamily";
    private static string ClassifyEvent(string? text)
    {
        var t = (text ?? string.Empty).ToLowerInvariant();
        if (Regex.IsMatch(t, @"\b(nba|nfl|mlb|nhl|soccer|tennis|football|basketball|baseball|ufc|fight|team|game|match|league)\b")) return "sports";
        if (Regex.IsMatch(t, @"\b(election|president|senate|house|congress|mayor|governor|trump|biden|democrat|republican)\b")) return "politics";
        if (Regex.IsMatch(t, @"\b(bitcoin|btc|ethereum|eth|crypto|solana|xrp)\b")) return "crypto";
        if (Regex.IsMatch(t, @"\b(stock|fed|rate|nasdaq|s&p|dow|earnings|finance)\b")) return "finance";
        if (Regex.IsMatch(t, @"\b(movie|oscar|grammy|music|culture|celebrity|album)\b")) return "culture";
        if (Regex.IsMatch(t, @"\b(cpi|inflation|gdp|unemployment|jobs|macro)\b")) return "macro";
        return "other";
    }
}
