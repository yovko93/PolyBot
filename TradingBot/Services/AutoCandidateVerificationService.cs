using System.Text.Json;
using System.Text.RegularExpressions;
using TradingBot.Engines;
using TradingBot.Models;

namespace TradingBot.Services;

public sealed record AutoCandidateVerificationResult(
    string TimestampUtc,
    string ProcessRunId,
    string CandidateId,
    string GroupKey,
    string NormalizedEventKey,
    string CandidateTitle,
    string Strategy,
    string VerificationCategory,
    int VerificationScore,
    string VerificationConfidence,
    string VerificationReason,
    string? MatchedVerifiedGroupKey,
    IReadOnlyList<string> MatchedMarketIds,
    IReadOnlyList<string> MissingLegs,
    IReadOnlyList<string> ExtraLegs,
    decimal? RawEdge,
    decimal? AfterCostEdge,
    decimal? AfterSafetyEdge,
    bool ValidPriced,
    bool WouldShadowOpen,
    string BlockedReason,
    string RecommendedAction);

public sealed record AutoCandidateVerificationSummary(
    IReadOnlyList<AutoCandidateVerificationResult> Candidates,
    IReadOnlyDictionary<string, int> CategoryCounts,
    int High,
    int Medium,
    int Low,
    int ShadowWouldOpen,
    AutoCandidateVerificationResult? Best)
{
    public int Count(string category) => CategoryCounts.TryGetValue(category, out var value) ? value : 0;
}

public static class AutoCandidateVerificationService
{
    public static AutoCandidateVerificationSummary Verify(
        IReadOnlyList<MultiOutcomeGroupArbEngine.CandidateGroupReview> candidates,
        IReadOnlyList<VerifiedMultiOutcomeGroupConfig> verifiedGroups,
        string processRunId)
    {
        var now = DateTime.UtcNow.ToString("O");
        var verified = verifiedGroups.Select(g => new VerifiedRef(g, NormalizeEvent(g.Title ?? g.GroupKey), IdSet(g.MarketIds), IdSet(g.ConditionIds))).ToList();
        var rows = candidates.Select((c, i) => VerifyOne(c, verified, now, processRunId, i)).ToList();
        var ordered = rows.OrderByDescending(x => x.VerificationScore).ThenByDescending(x => x.AfterSafetyEdge ?? decimal.MinValue).ThenByDescending(x => ConfidenceRank(x.VerificationConfidence)).Take(100).ToList();
        return new AutoCandidateVerificationSummary(
            ordered,
            rows.GroupBy(x => x.VerificationCategory).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase),
            rows.Count(x => x.VerificationConfidence == "High"),
            rows.Count(x => x.VerificationConfidence == "Medium"),
            rows.Count(x => x.VerificationConfidence == "Low"),
            rows.Count(x => x.WouldShadowOpen),
            ordered.FirstOrDefault());
    }

    private static AutoCandidateVerificationResult VerifyOne(MultiOutcomeGroupArbEngine.CandidateGroupReview c, List<VerifiedRef> verified, string now, string runId, int index)
    {
        var candidateMarkets = c.Markets ?? Array.Empty<Market>();
        var marketIds = IdSet(candidateMarkets.Select(m => m.id));
        var tokenIds = IdSet(candidateMarkets.SelectMany(m => m.clobTokenIds ?? new List<string>()).Concat(candidateMarkets.Select(m => m.conditionId ?? string.Empty)));
        var eventKey = NormalizeEvent(c.GroupKey);
        var semantic = verified.Select(v => new Match(v, ScoreText(eventKey, v.EventKey), marketIds.Intersect(v.MarketIds, StringComparer.OrdinalIgnoreCase).Count(), tokenIds.Intersect(v.TokenIds, StringComparer.OrdinalIgnoreCase).Count())).OrderByDescending(x => x.TextScore).ThenByDescending(x => x.MarketOverlap + x.TokenOverlap).ToList();
        var exact = verified.FirstOrDefault(v => string.Equals(c.GroupKey, v.Group.GroupKey, StringComparison.OrdinalIgnoreCase) || (v.MarketIds.Count > 0 && SetEquals(marketIds, v.MarketIds)) || (v.TokenIds.Count > 0 && SetEquals(tokenIds, v.TokenIds)));
        string category; int score; string reason; VerifiedRef? matched = exact; List<string> missing = []; List<string> extra = [];
        if (exact != null) { category = "AutoCandidateExactVerifiedMatch"; score = 98; reason = "Verified group key, market ids, or token ids match the allowlist."; }
        else
        {
            var top = semantic.FirstOrDefault();
            var tied = top != null && semantic.Count(x => x.TextScore == top.TextScore && x.TextScore >= 70) > 1;
            matched = top?.Ref;
            if (tied) { category = "AutoCandidateAmbiguousGroup"; score = 35; reason = "Multiple verified groups have similar event semantics."; }
            else if (top != null && top.TextScore >= 85 && (top.MarketOverlap + top.TokenOverlap) > 0) { category = "AutoCandidateNearVerifiedMatch"; score = 88; reason = "Same normalized event with overlapping market/token ids; verified allowlist may be stale."; }
            else if (top != null && top.TextScore >= 80 && !c.EstimatedNetEdge.HasValue) { category = "AutoCandidateSemanticMatchUnpriced"; score = 82; reason = "Event semantics match but pricing/legs are incomplete."; }
            else if (string.Equals(c.RejectionReason, "MissingLeg", StringComparison.OrdinalIgnoreCase) || c.DetectedMarketsCount < (matched?.Group.RequiredOutcomeCount ?? 2)) { category = "AutoCandidateMissingLeg"; score = 65; reason = "Candidate cannot form a complete mutually exclusive/exhaustive basket."; }
            else if (top != null && (top.TextScore >= 60 || (top.MarketOverlap + top.TokenOverlap) > 0)) { category = "AutoCandidatePartialOverlap"; score = 55; reason = "Only part of the candidate overlaps a verified event/outcome set."; }
            else if (top != null && top.TextScore >= 40) { category = "AutoCandidateNeedsManualReview"; score = 30; reason = "Plausible but insufficient confidence for verified-like classification."; }
            else if (verified.Count > 0) { category = "AutoCandidateDifferentEvent"; score = 0; reason = "Candidate appears unrelated to verified events."; }
            else { category = "AutoCandidateUnverified"; score = 0; reason = "No verified reference was available."; }
        }
        if (matched != null)
        {
            missing = matched.MarketIds.Except(marketIds, StringComparer.OrdinalIgnoreCase).Concat(matched.TokenIds.Except(tokenIds, StringComparer.OrdinalIgnoreCase)).Take(25).ToList();
            extra = marketIds.Except(matched.MarketIds, StringComparer.OrdinalIgnoreCase).Take(25).ToList();
        }
        var validPriced = c.EstimatedNetEdge.HasValue && !category.EndsWith("Unpriced", StringComparison.OrdinalIgnoreCase);
        var confidence = score >= 80 ? "High" : score >= 40 ? "Medium" : "Low";
        var wouldShadowOpen = confidence == "High" && validPriced && (c.EstimatedNetEdge ?? 0m) > 0m;
        var action = category switch { "AutoCandidateDifferentEvent" => "IgnoreDifferentEvent", "AutoCandidateSemanticMatchUnpriced" => "NeedsPricing", "AutoCandidateMissingLeg" => "NeedsLegCompletion", "AutoCandidateUnverified" => "RejectUnverified", "AutoCandidateExactVerifiedMatch" or "AutoCandidateNearVerifiedMatch" => "KeepShadowOnly", _ => "ManualReviewForAllowlist" };
        return new(now, runId, $"auto-{index + 1}-{StableId(c.GroupKey)}", c.GroupKey, eventKey, c.Title, "AutoCandidateMultiOutcome", category, score, confidence, reason, matched?.Group.GroupKey, marketIds.Intersect(matched?.MarketIds ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase).ToList(), missing, extra, c.EstimatedGrossEdge, c.EstimatedNetEdge, c.EstimatedNetEdge, validPriced, wouldShadowOpen, wouldShadowOpen ? "ModeShadowPaperEligible" : c.RejectionReason, action);
    }

    public static void WriteExport(string path, AutoCandidateVerificationSummary summary)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(summary.Candidates, new JsonSerializerOptions { WriteIndented = true });
        var temp = path + ".tmp";
        for (var i = 0; i < 3; i++)
        {
            try { File.WriteAllText(temp, json); File.Move(temp, path, true); return; }
            catch (IOException) { Thread.Sleep(50 * (i + 1)); }
            catch (UnauthorizedAccessException) { Thread.Sleep(50 * (i + 1)); }
        }
        try { if (File.Exists(temp)) File.Delete(temp); } catch { }
    }

    private static HashSet<string> IdSet(IEnumerable<string?> ids) => ids.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static bool SetEquals(HashSet<string> a, HashSet<string> b) => a.Count > 0 && b.Count > 0 && a.SetEquals(b);
    private static string NormalizeEvent(string s) => Regex.Replace(Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9]+", " "), @"\s+", " ").Trim();
    private static int ScoreText(string a, string b) { if (a == b) return 100; var aa = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(); var bb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(); if (aa.Count == 0 || bb.Count == 0) return 0; return (int)Math.Round(100m * aa.Intersect(bb).Count() / Math.Max(aa.Count, bb.Count)); }
    private static int ConfidenceRank(string c) => c == "High" ? 3 : c == "Medium" ? 2 : c == "Low" ? 1 : 0;
    private static string StableId(string value) => Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(value ?? string.Empty)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    private sealed record VerifiedRef(VerifiedMultiOutcomeGroupConfig Group, string EventKey, HashSet<string> MarketIds, HashSet<string> TokenIds);
    private sealed record Match(VerifiedRef Ref, int TextScore, int MarketOverlap, int TokenOverlap);
}
