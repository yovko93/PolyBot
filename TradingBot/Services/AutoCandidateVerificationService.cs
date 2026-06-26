using System.Text.Json;
using System.Text.RegularExpressions;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services.MultiOutcome;

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
    string RecommendedAction,
    bool PricingAttempted = false,
    bool PricingSucceeded = false,
    string PricingSkippedReason = "NotAttempted",
    int ExpectedLegCount = 0,
    int PresentLegCount = 0,
    int MissingLegCount = 0,
    int ExtraLegCount = 0,
    IReadOnlyList<string>? MissingOutcomes = null,
    IReadOnlyList<string>? ExtraOutcomes = null,
    bool CanCompleteFromDiscoveredPool = false,
    string CompletionSource = "None",
    string CompletionConfidence = "Low",
    string CompletionReason = "NotEvaluated",
    bool ExecutableLike = false);

public sealed record AutoCandidateVerificationSummary(
    IReadOnlyList<AutoCandidateVerificationResult> Candidates,
    IReadOnlyDictionary<string, int> CategoryCounts,
    int High,
    int Medium,
    int Low,
    int ShadowWouldOpen,
    AutoCandidateVerificationResult? Best,
    int PricingAttempted,
    int PricingSucceeded,
    int PricingFailed,
    int PricingSkippedByHealth,
    int PricingSkippedIncomplete,
    int PricingMissingNoAsk,
    int PricingMissingYesAsk,
    int PricingEmptyBook,
    int CompletedFromVerifiedAllowlist,
    int CompletedFromCandidatePool,
    int CompletedFromDiscoveryPool,
    int CompletedGroups,
    int IncompleteGroups,
    decimal? BestRawEdge,
    decimal? BestAfterCostEdge,
    decimal? BestAfterSafetyEdge,
    string BestPricingReason)
{
    public int Count(string category) => CategoryCounts.TryGetValue(category, out var value) ? value : 0;
}

public static class AutoCandidateVerificationService
{
    public static async Task<AutoCandidateVerificationSummary> VerifyAsync(
        IReadOnlyList<MultiOutcomeGroupArbEngine.CandidateGroupReview> candidates,
        IReadOnlyList<VerifiedMultiOutcomeGroupConfig> verifiedGroups,
        IReadOnlyList<Market> discoveredMarkets,
        IOrderBookProvider orderBooks,
        SemaphoreSlim orderbookSemaphore,
        MultiOutcomeArbitrageOptions multiOutcomeOptions,
        AutoCandidatePricingOptions pricingOptions,
        bool orderbookHealthClean,
        bool paperDiagnosticsEligible,
        StrategyMode strategyMode,
        string processRunId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow.ToString("O");
        var verified = verifiedGroups.Select(g => new VerifiedRef(g, NormalizeEvent(g.Title ?? g.GroupKey), IdSet(g.MarketIds), IdSet(g.ConditionIds))).ToList();
        var discoveredByMarket = discoveredMarkets.Where(m => !string.IsNullOrWhiteSpace(m.id)).GroupBy(m => m.id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var rows = candidates.Select((c, i) => VerifyOne(c, verified, discoveredByMarket, now, processRunId, i)).ToList();

        var toPrice = rows
            .Where(x => pricingOptions.Enabled && IsVerifiedLike(x) && (x.VerificationConfidence == "High" || x.VerificationConfidence == "Medium"))
            .OrderByDescending(x => x.VerificationScore)
            .ThenByDescending(x => x.AfterSafetyEdge ?? decimal.MinValue)
            .Take(Math.Max(0, pricingOptions.MaxCandidatesPerCycle))
            .ToList();

        var pricedById = new Dictionary<string, AutoCandidateVerificationResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in toPrice)
            pricedById[row.CandidateId] = await TryPriceAsync(row, discoveredByMarket, orderBooks, orderbookSemaphore, multiOutcomeOptions, pricingOptions, orderbookHealthClean, paperDiagnosticsEligible, strategyMode, ct);
        rows = rows.Select(r => pricedById.TryGetValue(r.CandidateId, out var priced) ? priced : r).ToList();

        var ordered = rows.OrderByDescending(x => x.VerificationScore).ThenByDescending(x => x.AfterSafetyEdge ?? decimal.MinValue).ThenByDescending(x => ConfidenceRank(x.VerificationConfidence)).Take(100).ToList();
        var bestPriced = rows.Where(x => x.PricingSucceeded).OrderByDescending(x => x.AfterSafetyEdge ?? decimal.MinValue).FirstOrDefault();
        return new AutoCandidateVerificationSummary(
            ordered,
            rows.GroupBy(x => x.VerificationCategory).ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase),
            rows.Count(x => x.VerificationConfidence == "High"),
            rows.Count(x => x.VerificationConfidence == "Medium"),
            rows.Count(x => x.VerificationConfidence == "Low"),
            rows.Count(x => x.WouldShadowOpen),
            ordered.FirstOrDefault(),
            rows.Count(x => x.PricingAttempted),
            rows.Count(x => x.PricingSucceeded),
            rows.Count(x => x.PricingAttempted && !x.PricingSucceeded),
            rows.Count(x => x.PricingSkippedReason == "OrderbookHealth"),
            rows.Count(x => x.PricingSkippedReason == "IncompleteLegs"),
            rows.Count(x => x.PricingSkippedReason == "MissingNoAsk"),
            rows.Count(x => x.PricingSkippedReason == "MissingYesAsk"),
            rows.Count(x => x.PricingSkippedReason == "EmptyBook"),
            rows.Count(x => x.CompletionSource == "VerifiedAllowlist"),
            rows.Count(x => x.CompletionSource == "CandidatePool"),
            rows.Count(x => x.CompletionSource == "DiscoveryPool"),
            rows.Count(x => x.MissingLegCount == 0),
            rows.Count(x => x.MissingLegCount > 0),
            bestPriced?.RawEdge,
            bestPriced?.AfterCostEdge,
            bestPriced?.AfterSafetyEdge,
            bestPriced?.BlockedReason ?? "NoPricedCandidate");
    }

    private static AutoCandidateVerificationResult VerifyOne(MultiOutcomeGroupArbEngine.CandidateGroupReview c, List<VerifiedRef> verified, Dictionary<string, Market> discoveredByMarket, string now, string runId, int index)
    {
        var candidateMarkets = c.Markets ?? Array.Empty<Market>();
        var marketIds = IdSet(candidateMarkets.Select(m => m.id));
        var tokenIds = IdSet(candidateMarkets.SelectMany(m => m.clobTokenIds ?? new List<string>()).Concat(candidateMarkets.Select(m => m.conditionId ?? string.Empty)));
        var eventKey = NormalizeEvent(c.GroupKey);
        var semantic = verified.Select(v => new Match(v, ScoreText(eventKey, v.EventKey), marketIds.Intersect(v.MarketIds, StringComparer.OrdinalIgnoreCase).Count(), tokenIds.Intersect(v.TokenIds, StringComparer.OrdinalIgnoreCase).Count())).OrderByDescending(x => x.TextScore).ThenByDescending(x => x.MarketOverlap + x.TokenOverlap).ToList();
        var exact = verified.FirstOrDefault(v => string.Equals(c.GroupKey, v.Group.GroupKey, StringComparison.OrdinalIgnoreCase) || (v.MarketIds.Count > 0 && SetEquals(marketIds, v.MarketIds)) || (v.TokenIds.Count > 0 && SetEquals(tokenIds, v.TokenIds)));
        string category; int score; string reason; VerifiedRef? matched = exact;
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

        var expectedIds = matched?.MarketIds.Count > 0 ? matched.MarketIds : marketIds;
        var missing = matched is null ? new List<string>() : matched.MarketIds.Except(marketIds, StringComparer.OrdinalIgnoreCase).Concat(matched.TokenIds.Except(tokenIds, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Take(25).ToList();
        var missingMarketIds = matched is null ? new List<string>() : matched.MarketIds.Except(marketIds, StringComparer.OrdinalIgnoreCase).ToList();
        var extra = matched is null ? new List<string>() : marketIds.Except(matched.MarketIds, StringComparer.OrdinalIgnoreCase).Take(25).ToList();
        var canComplete = missingMarketIds.Count > 0 && missingMarketIds.All(discoveredByMarket.ContainsKey);
        var completionSource = missingMarketIds.Count == 0 ? (matched is not null ? "VerifiedAllowlist" : "CandidatePool") : canComplete ? "DiscoveryPool" : "None";
        var completionConfidence = missingMarketIds.Count == 0 ? "High" : canComplete ? "Medium" : "Low";
        var completionReason = missingMarketIds.Count == 0 ? "Candidate already has all expected verified legs." : canComplete ? "Missing verified legs are present in the discovered pool for diagnostics-only completion." : "One or more verified legs are absent from the discovered pool.";
        var confidence = score >= 80 ? "High" : score >= 40 ? "Medium" : "Low";
        var validPriced = c.EstimatedNetEdge.HasValue && !category.EndsWith("Unpriced", StringComparison.OrdinalIgnoreCase);
        var action = category switch { "AutoCandidateDifferentEvent" => "IgnoreDifferentEvent", "AutoCandidateSemanticMatchUnpriced" => "NeedsPricing", "AutoCandidateMissingLeg" => "NeedsLegCompletion", "AutoCandidateUnverified" => "RejectUnverified", "AutoCandidateExactVerifiedMatch" or "AutoCandidateNearVerifiedMatch" => "KeepShadowOnly", _ => "ManualReviewForAllowlist" };
        return new(now, runId, $"auto-{index + 1}-{StableId(c.GroupKey)}", c.GroupKey, eventKey, c.Title, "AutoCandidateMultiOutcome", category, score, confidence, reason, matched?.Group.GroupKey, marketIds.Intersect(matched?.MarketIds ?? new HashSet<string>(), StringComparer.OrdinalIgnoreCase).ToList(), missing, extra, c.EstimatedGrossEdge, c.EstimatedNetEdge, c.EstimatedNetEdge, validPriced, false, c.RejectionReason, action, ExpectedLegCount: Math.Max(expectedIds.Count, matched?.Group.RequiredOutcomeCount ?? 0), PresentLegCount: marketIds.Count, MissingLegCount: missingMarketIds.Count, ExtraLegCount: extra.Count, MissingOutcomes: missingMarketIds, ExtraOutcomes: extra, CanCompleteFromDiscoveredPool: canComplete, CompletionSource: completionSource, CompletionConfidence: completionConfidence, CompletionReason: completionReason);
    }

    private static async Task<AutoCandidateVerificationResult> TryPriceAsync(AutoCandidateVerificationResult row, Dictionary<string, Market> discoveredByMarket, IOrderBookProvider orderBooks, SemaphoreSlim orderbookSemaphore, MultiOutcomeArbitrageOptions options, AutoCandidatePricingOptions pricingOptions, bool healthClean, bool paperDiagnosticsEligible, StrategyMode strategyMode, CancellationToken ct)
    {
        if (!healthClean && (pricingOptions.RequireOrderbookStableNow || pricingOptions.RequireReducedUniverseOrderbookStableNow))
            return row with { PricingSkippedReason = "OrderbookHealth", BlockedReason = "OrderbookHealth", ValidPriced = false, ExecutableLike = false };
        var ids = row.MatchedVerifiedGroupKey is not null && row.MissingLegs.Count == 0 ? row.MatchedMarketIds : row.MatchedMarketIds.Concat(row.MissingOutcomes ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (row.MissingLegCount > 0 && !row.CanCompleteFromDiscoveredPool)
            return row with { PricingSkippedReason = "IncompleteLegs", BlockedReason = "IncompleteLegs", ValidPriced = false, ExecutableLike = false };
        var markets = ids.Select(id => discoveredByMarket.TryGetValue(id, out var m) ? m : null).Where(m => m is not null).Cast<Market>().ToList();
        if (markets.Count < Math.Max(2, row.ExpectedLegCount))
            return row with { PricingSkippedReason = "IncompleteLegs", BlockedReason = "IncompleteLegs", ValidPriced = false, ExecutableLike = false };
        var resolved = new List<ResolvedNoAsk>();
        foreach (var market in markets)
        {
            await orderbookSemaphore.WaitAsync(ct);
            BinaryOrderBookSnapshot? snapshot = null;
            try { snapshot = await orderBooks.GetBinarySnapshotAsync(market, ct); }
            finally { orderbookSemaphore.Release(); }
            var noAsk = VerifiedGroupPricingService.ResolveNoAsk(market, snapshot, DateTime.UtcNow, options.VerifiedGroupOrderbookMaxAgeMs);
            if (!noAsk.NoAsk.HasValue)
            {
                var skip = noAsk.FailureReason == "EmptyBook" ? "EmptyBook" : "MissingNoAsk";
                return row with { PricingAttempted = true, PricingSkippedReason = skip, BlockedReason = skip, ValidPriced = false, ExecutableLike = false };
            }
            resolved.Add(noAsk);
        }
        var screen = VerifiedBasketScreener.Evaluate(row.GroupKey, resolved, options);
        var executableLike = screen.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.ExecutableUnderActiveProfile && screen.ActiveProfileNetEdge > 0m;
        var paperBlocked = executableLike && !paperDiagnosticsEligible;
        var wouldShadow = strategyMode == StrategyMode.ShadowPaperEligible && row.VerificationConfidence == "High" && executableLike && healthClean && !paperBlocked;
        var blocked = wouldShadow ? "ShadowMode" : paperBlocked ? "PaperDiagnosticsLimitedGate" : screen.ExecutionStatus.ToString();
        return row with { PricingAttempted = true, PricingSucceeded = true, PricingSkippedReason = "None", RawEdge = screen.GrossEdge, AfterCostEdge = screen.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? screen.ActiveProfileNetEdge, AfterSafetyEdge = screen.ActiveProfileNetEdge, ValidPriced = true, ExecutableLike = executableLike, WouldShadowOpen = wouldShadow, BlockedReason = blocked, RecommendedAction = row.RecommendedAction == "NeedsPricing" ? "KeepShadowOnly" : row.RecommendedAction };
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

    private static bool IsVerifiedLike(AutoCandidateVerificationResult x) => x.VerificationCategory is not "AutoCandidateDifferentEvent" and not "AutoCandidateUnverified";
    private static HashSet<string> IdSet(IEnumerable<string?> ids) => ids.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private static bool SetEquals(HashSet<string> a, HashSet<string> b) => a.Count > 0 && b.Count > 0 && a.SetEquals(b);
    private static string NormalizeEvent(string s) => Regex.Replace(Regex.Replace((s ?? "").ToLowerInvariant(), @"[^a-z0-9]+", " "), @"\s+", " ").Trim();
    private static int ScoreText(string a, string b) { if (a == b) return 100; var aa = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(); var bb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(); if (aa.Count == 0 || bb.Count == 0) return 0; return (int)Math.Round(100m * aa.Intersect(bb).Count() / Math.Max(aa.Count, bb.Count)); }
    private static int ConfidenceRank(string c) => c == "High" ? 3 : c == "Medium" ? 2 : c == "Low" ? 1 : 0;
    private static string StableId(string value) => Math.Abs(StringComparer.OrdinalIgnoreCase.GetHashCode(value ?? string.Empty)).ToString(System.Globalization.CultureInfo.InvariantCulture);
    private sealed record VerifiedRef(VerifiedMultiOutcomeGroupConfig Group, string EventKey, HashSet<string> MarketIds, HashSet<string> TokenIds);
    private sealed record Match(VerifiedRef Ref, int TextScore, int MarketOverlap, int TokenOverlap);
}
