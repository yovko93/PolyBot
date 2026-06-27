using System.Text.Json;
using System.Text.RegularExpressions;
using TradingBot.Api;
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

public sealed record InvalidRawSpikeFamilySummary(
    string FamilyKey,
    string Strategy,
    decimal? BestRawEdge,
    string InvalidReason,
    int Samples,
    int DataQualityRejectedCount,
    int MissingPricingCount,
    string RecommendedAction = "IgnoreInvalidPricingOrFixMapping");

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
    IReadOnlyList<object> InvalidRawSpikeFamilies,
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
    int InvalidRawSpikeFamiliesCount,
    decimal? InvalidRawSpikeBestEdge,
    string InvalidRawSpikeTopReason,
    bool RankingConsistent,
    string RankingConsistencyReason,
    string TopRecommendedAction,
    IReadOnlyList<OpportunityFamilySummary> PricedFamilies,
    IReadOnlyList<OpportunityFamilySummary> UnpricedFamilies,
    IReadOnlyList<InvalidRawSpikeFamilySummary> InvalidRawSpikeFamilies);

public static class OpportunityFamilyRankingService
{
    private static readonly string[] InvalidPricedReasons =
    {
        "MissingYesAsk", "MissingNoAsk", "MissingLeg", "IncompleteLegs", "DataQualityRejected",
        "SuspiciousYesNoAskSum", "AutoCandidateUnverified", "ReviewOnly", "DiagnosticsOnly",
        "TokenOutcomeMappingUnverified", "N/A"
    };

    private sealed record Sample(
        string Strategy,
        string FamilyType,
        string EventType,
        string MarketShape,
        int OutcomeCount,
        string VerificationConfidence,
        string RejectedReason,
        string PricingStatus,
        string Key,
        string Title,
        decimal? Raw,
        decimal? AfterCost,
        decimal? AfterSafety,
        int VerificationScore,
        bool CandidateValid,
        bool CandidatePriced,
        bool ValidPriced,
        bool DataQualityRejected,
        bool MissingPricing,
        bool Positive,
        bool Ready,
        bool Shadow,
        bool Paper)
    {
        public bool IsInvalidRawSpike => Raw > 0 && !IsValidPricedForRanking;
        public bool IsValidPricedForRanking => CandidateValid
            && CandidatePriced
            && ValidPriced
            && AfterSafety.HasValue
            && !DataQualityRejected
            && !MissingPricing
            && !IsInvalidPricedReason(RejectedReason)
            && PricingStatus is "priced" or "notExecutable";
    }

    public static OpportunityFamilyRankingSnapshot Build(
        IReadOnlyList<SingleMarketOpportunityAuditDto> singleMarket,
        IReadOnlyList<VerifiedGroupDiagnosticDto> verified,
        IReadOnlyList<VerifiedGroupPricingDto> verifiedPricing,
        AutoCandidateVerificationSummary? auto)
    {
        var samples = new List<Sample>();
        samples.AddRange(singleMarket.Select(x =>
        {
            var reason = Reason(x.DataQualityReason ?? x.RejectedReason);
            var dataQualityRejected = !string.IsNullOrWhiteSpace(x.DataQualityReason) || IsDataQualityReason(reason);
            var missingPricing = IsMissingPricingReason(reason);
            var validPriced = !dataQualityRejected && !missingPricing && !IsInvalidPricedReason(reason) && x.AfterSafetyEdge.HasValue;
            return new Sample("SingleMarketBuyBoth", "SingleMarketBuyBoth", ClassifyEvent(x.Title), "binary", 2, "High", reason, Status(reason, validPriced, x.AfterSafetyEdge), x.MarketId, x.Title, x.RawEdge, x.AfterCostEdge, x.AfterSafetyEdge, 100, validPriced, validPriced, validPriced, dataQualityRejected, missingPricing, validPriced && x.AfterSafetyEdge > 0, validPriced && x.ExecutableQty > 0 && x.FillPassed && x.DepthPassed && x.RiskPassed && x.PaperDiagnosticsLimitedGatePassed, false, false);
        }));

        var pricingByKey = verifiedPricing.GroupBy(x => x.GroupKey, StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var v in verified)
        {
            pricingByKey.TryGetValue(v.GroupKey, out var p);
            var edge = p?.NetEdge ?? v.BestEdge;
            var reason = Reason(p?.SkipReason ?? v.SkipReason);
            var dataQualityRejected = IsDataQualityReason(reason);
            var missingPricing = IsMissingPricingReason(reason) || v.MissingNoAskCount > 0 || v.MissingMarketCount > 0 || p is null;
            var validPriced = edge.HasValue && !dataQualityRejected && !missingPricing && !IsInvalidPricedReason(reason);
            samples.Add(new Sample("VerifiedMultiOutcome", "VerifiedMultiOutcome", ClassifyEvent(v.GroupKey), v.MissingMarketCount > 0 ? "multiOutcomeIncomplete" : "mutuallyExclusiveBasket", Math.Max(v.RequiredMarketCount, v.ResolvedMarketCount), Confidence(edge, reason), reason, Status(reason, validPriced, edge), v.GroupKey, v.GroupKey, p?.GrossEdge ?? edge, p?.NetEdge, edge, edge.HasValue ? 80 : 40, validPriced, validPriced, validPriced, dataQualityRejected, missingPricing, validPriced && edge > 0, validPriced && string.Equals(reason, "Executable", StringComparison.OrdinalIgnoreCase), false, false));
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
                var reason = Reason(a.BlockedReason);
                var dataQualityRejected = IsDataQualityReason(reason);
                var missingPricing = !a.PricingSucceeded || a.MissingLegCount > 0 || IsMissingPricingReason(reason);
                var validPriced = a.ValidPriced && a.PricingSucceeded && a.AfterSafetyEdge.HasValue && !dataQualityRejected && !missingPricing && !IsInvalidPricedReason(reason) && !a.VerificationCategory.Equals("AutoCandidateUnverified", StringComparison.OrdinalIgnoreCase);
                var shape = a.MissingLegCount > 0 ? "multiOutcomeIncomplete" : a.ExpectedLegCount > 2 || a.PresentLegCount > 2 ? "mutuallyExclusiveBasket" : "unknown";
                var pricingStatus = validPriced ? Status(reason, true, a.AfterSafetyEdge) : a.MissingLegCount > 0 ? "incomplete" : a.VerificationCategory.Contains("Unpriced", StringComparison.OrdinalIgnoreCase) ? "unpriced" : "missingPricing";
                samples.Add(new Sample(a.Strategy, familyType, ClassifyEvent(a.CandidateTitle + " " + a.GroupKey), shape, Math.Max(a.ExpectedLegCount, a.PresentLegCount), a.VerificationConfidence, reason, pricingStatus, a.GroupKey, a.CandidateTitle, a.RawEdge, a.AfterCostEdge, a.AfterSafetyEdge, a.VerificationScore, validPriced, validPriced, validPriced, dataQualityRejected, missingPricing, validPriced && (a.AfterSafetyEdge ?? 0) > 0, validPriced && a.ExecutableLike, validPriced && a.WouldShadowOpen, false));
            }
        }

        var buckets = samples.GroupBy(FamilyKey, StringComparer.OrdinalIgnoreCase).Select(BuildBucket).ToList();
        var priced = buckets.Where(x => x.ValidPriced > 0).OrderByDescending(x => x.BestAfterSafetyEdge ?? decimal.MinValue).ThenByDescending(x => x.Samples).Take(50).ToList();
        var unpriced = buckets.Where(x => x.ValidPriced == 0).OrderByDescending(x => x.BestVerificationScore).ThenByDescending(x => x.Samples).Take(50).ToList();
        var invalidRawSpikeFamilies = samples.Where(x => x.IsInvalidRawSpike)
            .GroupBy(x => string.Join("|", x.Strategy, x.FamilyType, x.EventType, x.MarketShape, x.OutcomeCount, x.RejectedReason), StringComparer.OrdinalIgnoreCase)
            .Select(g => new InvalidRawSpikeFamilySummary(
                g.Key,
                g.First().Strategy,
                g.Max(x => x.Raw),
                g.GroupBy(x => x.RejectedReason).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).First().Key,
                g.Count(),
                g.Count(x => x.DataQualityRejected),
                g.Count(x => x.MissingPricing)))
            .OrderByDescending(x => x.BestRawEdge ?? decimal.MinValue)
            .ThenByDescending(x => x.Samples)
            .Take(50)
            .ToList();

        var bestPriced = priced.FirstOrDefault();
        var bestUnpriced = unpriced.FirstOrDefault();
        var topInvalid = invalidRawSpikeFamilies.FirstOrDefault();
        return new OpportunityFamilyRankingSnapshot(
            true,
            buckets.Count,
            bestPriced?.FamilyKey ?? "N/A",
            bestPriced?.BestAfterSafetyEdge,
            bestUnpriced?.FamilyKey ?? "N/A",
            bestUnpriced?.BestVerificationScore ?? 0,
            priced.Count(x => (x.BestAfterSafetyEdge ?? -1m) >= -0.01m),
            priced.Count(x => x.Positive > 0),
            priced.Count(x => x.ExecutionReady > 0),
            invalidRawSpikeFamilies.Count,
            topInvalid?.BestRawEdge,
            topInvalid?.InvalidReason ?? "None",
            true,
            "None",
            bestPriced?.RecommendedAction ?? bestUnpriced?.RecommendedAction ?? "KeepMonitoring",
            priced,
            unpriced,
            invalidRawSpikeFamilies);
    }

    public static OpportunityFamilyRankingSnapshot WithConsistency(OpportunityFamilyRankingSnapshot snapshot, long totalPositive, int singleMarketValidAfterSafetyPositive)
    {
        var consistent = !(snapshot.BestPricedAfterSafetyEdge > 0 && totalPositive == 0);
        return snapshot with
        {
            RankingConsistent = consistent,
            RankingConsistencyReason = consistent ? "None" : "PositiveFamilyWithoutValidPositiveCandidate"
        };
    }

    private static string FamilyKey(Sample x) => string.Join("|", x.Strategy, x.FamilyType, x.EventType, x.MarketShape, x.OutcomeCount, x.VerificationConfidence, x.RejectedReason, x.PricingStatus);

    private static OpportunityFamilySummary BuildBucket(IGrouping<string, Sample> g)
    {
        var a = g.ToList();
        var validPriced = a.Where(x => x.IsValidPricedForRanking).ToList();
        var pricedEdges = validPriced.Select(x => x.AfterSafety!.Value).OrderBy(x => x).ToArray();
        var best = (validPriced.Count > 0 ? validPriced : a).OrderByDescending(x => x.AfterSafety ?? decimal.MinValue).ThenByDescending(x => x.VerificationScore).First();
        var topReason = a.GroupBy(x => x.RejectedReason).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).First().Key;
        return new OpportunityFamilySummary(g.Key, best.Strategy, best.FamilyType, best.EventType, best.MarketShape, a.Count, validPriced.Count, a.Count - validPriced.Count, validPriced.Count(x => x.AfterSafety > 0), validPriced.Count(x => x.Ready), validPriced.Count(x => x.Shadow), validPriced.Count(x => x.Paper), validPriced.Count == 0 ? null : validPriced.Max(x => x.Raw), validPriced.Count == 0 ? null : validPriced.Max(x => x.AfterCost), pricedEdges.Length == 0 ? null : pricedEdges.Max(), P(pricedEdges,.50m), P(pricedEdges,.90m), P(pricedEdges,.95m), P(pricedEdges,.99m), pricedEdges.Length == 0 ? null : pricedEdges.Max(), pricedEdges.Length == 0 ? null : pricedEdges.Average(), a.Count(x => x.MissingPricing), a.Count(x => x.PricingStatus == "incomplete"), a.Count(x => x.PricingStatus == "reviewOnly" || x.RejectedReason.Equals("ReviewOnly", StringComparison.OrdinalIgnoreCase)), a.Count(x => x.DataQualityRejected), a.Count(x => x.RejectedReason.Contains("Unverified", StringComparison.OrdinalIgnoreCase)), a.Count(x => x.FamilyType == "AutoCandidateDifferentEvent"), a.Count(x => x.VerificationConfidence == "High"), a.Count(x => x.VerificationConfidence == "Medium"), a.Count(x => x.VerificationConfidence == "Low"), a.Count(x => x.FamilyType.Contains("MissingLeg", StringComparison.OrdinalIgnoreCase)), a.Max(x => x.VerificationScore), best.VerificationConfidence, topReason, best.Key, best.Title, Recommend(best, pricedEdges));
    }

    public static OpportunityFamilyRankingExport ToExport(OpportunityFamilyRankingSnapshot s, RuntimeHealthSnapshot h) => new(DateTime.UtcNow, h.ProcessRunId, h.Uptime, h.DiscoverySelectedSource, h.PaperDiagnosticsLimitedEligible, h.OrderbookStableNow, h.ReducedUniverseOrderbookStableNow, s.PricedFamilies.Select((x,i)=>(object)new { rank=i+1, x.FamilyKey, x.Strategy, x.FamilyType, x.EventType, x.MarketShape, x.Samples, x.ValidPriced, x.BestAfterSafetyEdge, x.P50AfterSafetyEdge, x.P90AfterSafetyEdge, x.P95AfterSafetyEdge, x.P99AfterSafetyEdge, x.MaxAfterSafetyEdge, x.AvgAfterSafetyEdge, x.Positive, x.ExecutionReady, x.ShadowWouldOpen, x.PaperOpened, x.TopRejectedReason, x.BestMarketOrGroupKey, x.BestTitle, x.RecommendedAction }).ToArray(), s.UnpricedFamilies.Select((x,i)=>(object)new { rank=i+1, x.FamilyKey, x.Strategy, x.FamilyType, x.EventType, x.MarketShape, x.Samples, x.VerificationHigh, x.VerificationMedium, x.VerificationLow, bestVerificationScore=x.BestVerificationScore, x.MissingPricingCount, x.IncompleteCount, missingLegCount=x.MissingLegCount, x.DifferentEventCount, x.TopRejectedReason, x.BestMarketOrGroupKey, x.BestTitle, x.RecommendedAction }).ToArray(), s.InvalidRawSpikeFamilies.Select(x => (object)new { x.FamilyKey, x.Strategy, x.BestRawEdge, invalidReason = x.InvalidReason, x.Samples, x.DataQualityRejectedCount, x.MissingPricingCount, x.RecommendedAction }).ToArray(), s.PricedFamilies.Concat(s.UnpricedFamilies).GroupBy(x => x.RecommendedAction).OrderByDescending(x => x.Count()).Select(x => x.Key).Take(5).ToArray());

    public static void WriteExportAtomic(string path, OpportunityFamilyRankingExport export)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        for (var i = 0; i < 3; i++) { try { File.WriteAllText(tmp, json); File.Move(tmp, path, true); return; } catch when (i < 2) { Thread.Sleep(50); } }
    }

    private static decimal? P(decimal[] sorted, decimal p) => sorted.Length == 0 ? null : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * p) - 1, 0, sorted.Length - 1)];
    private static string Reason(string? r) => string.IsNullOrWhiteSpace(r) ? "N/A" : r.Trim().Replace(' ', '_');
    private static string Status(string reason, bool validPriced, decimal? edge) => !validPriced ? (IsDataQualityReason(reason) ? "dataQualityRejected" : IsMissingPricingReason(reason) ? "missingPricing" : reason.Contains("Review", StringComparison.OrdinalIgnoreCase) ? "reviewOnly" : "incomplete") : edge.HasValue && edge.Value <= 0 ? "notExecutable" : "priced";
    private static string Confidence(decimal? edge, string reason) => edge.HasValue && !IsInvalidPricedReason(reason) ? "High" : reason.Contains("Review", StringComparison.OrdinalIgnoreCase) ? "Medium" : "Low";
    private static string Recommend(Sample best, decimal[] pricedEdges) => pricedEdges.Length > 0 ? (pricedEdges[^1] > 0 ? "CandidateForFuturePaperOnlyAfterVerified" : "NoActionNegativeEdge") : best.FamilyType.Contains("MissingLeg", StringComparison.OrdinalIgnoreCase) ? "InvestigateMissingLegs" : best.MissingPricing ? "InvestigatePricing" : best.VerificationScore >= 80 ? "ManualReviewCandidateFamily" : "IgnoreLowQualityFamily";
    private static bool IsInvalidPricedReason(string reason) => InvalidPricedReasons.Any(x => reason.Equals(x, StringComparison.OrdinalIgnoreCase) || reason.Contains(x, StringComparison.OrdinalIgnoreCase));
    private static bool IsMissingPricingReason(string reason) => reason.Contains("MissingNoAsk", StringComparison.OrdinalIgnoreCase) || reason.Contains("MissingYesAsk", StringComparison.OrdinalIgnoreCase) || reason.Contains("MissingPricing", StringComparison.OrdinalIgnoreCase) || reason.Contains("IncompleteLeg", StringComparison.OrdinalIgnoreCase) || reason.Contains("MissingLeg", StringComparison.OrdinalIgnoreCase);
    private static bool IsDataQualityReason(string reason) => reason.Contains("DataQuality", StringComparison.OrdinalIgnoreCase) || reason.Contains("SuspiciousYesNoAskSum", StringComparison.OrdinalIgnoreCase) || reason.Contains("TokenOutcomeMappingUnverified", StringComparison.OrdinalIgnoreCase);
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
