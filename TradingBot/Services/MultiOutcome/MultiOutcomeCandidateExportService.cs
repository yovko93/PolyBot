using System.Text.Json;
using System.Text.RegularExpressions;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services.MultiOutcome;

public sealed class MultiOutcomeCandidateExportService
{
    private sealed record ReviewRow(
        string groupKey,
        string title,
        string kind,
        int detectedMarketsCount,
        int pricedLegs,
        int missingPrices,
        decimal? estimatedGrossEdge,
        decimal? estimatedNetEdgeConservative,
        decimal? estimatedNetEdgeRawOnly,
        int candidateQualityScore,
        string recommendedAction,
        string rejectionReason,
        IReadOnlyList<string> warnings,
        IReadOnlyList<object> markets,
        object? suggestedAllowlistTemplate,
        string copyInstructions,
        bool alreadyAllowlisted);
    private readonly MultiOutcomeReviewOptions _options;
    private readonly string _absolutePath;
    private readonly string _reviewAbsolutePath;
    private readonly string _verifiedPricingAbsolutePath;
    private readonly string _verifiedAllowlistPath;
    private DateTime _lastExportAtUtc = DateTime.MinValue;
    private bool _hasLoggedNoCandidates;

    public MultiOutcomeCandidateExportService(MultiOutcomeReviewOptions options, string contentRootPath)
    {
        _options = options;
        _absolutePath = ResolvePath(contentRootPath, options.ExportPath);
        _reviewAbsolutePath = ResolvePath(contentRootPath, options.ExportReviewPath);
        _verifiedPricingAbsolutePath = ResolvePath(contentRootPath, options.ExportVerifiedPricingPath);
        _verifiedAllowlistPath = Path.Combine(contentRootPath, "config", "verified-multi-outcome-groups.json");

        Console.WriteLine(_options.ExportCandidates
            ? $"[MULTI_REVIEW] Candidate export enabled Path={_absolutePath}"
            : "[MULTI_REVIEW] Candidate export disabled");
    }

    private static string ResolvePath(string root, string path) => Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path));

    public string ReviewExportPathAbsolute => _reviewAbsolutePath;

    public IReadOnlyList<object> BuildBoundedCandidates(IReadOnlyList<MultiOutcomeGroupArbEngine.CandidateGroupReview> groups, int topGroups, int maxMarkets, bool includeMarkets)
    {
        return groups.OrderByDescending(g => g.EstimatedNetEdge ?? decimal.MinValue).ThenByDescending(g => g.DetectedMarketsCount).Take(Math.Max(1, topGroups)).Select(g => new
        {
            groupKey = g.GroupKey,
            title = g.Title,
            kind = g.Kind,
            detectedMarketsCount = g.DetectedMarketsCount,
            verificationStatus = g.VerificationStatus,
            rejectionReason = g.RejectionReason,
            estimatedNoBasketCost = g.EstimatedNoBasketCost,
            estimatedGrossEdge = g.EstimatedGrossEdge,
            estimatedNetEdge = g.EstimatedNetEdge,
            guaranteedPayoutIfVerified = g.GuaranteedPayoutIfVerified,
            warnings = g.Warnings,
            markets = includeMarkets ? (g.Markets ?? Array.Empty<Market>()).Take(Math.Max(1, maxMarkets)).Select(ToMarketNode).ToArray() : Array.Empty<object>()
        }).Cast<object>().ToArray();
    }

    public IReadOnlyList<object> BuildReviewReport(IReadOnlyList<MultiOutcomeGroupArbEngine.CandidateGroupReview> groups, bool allowUnpricedLegsInTemplate = false)
    {
        var allowlistedKeys = LoadAllowlistedKeys();
        var rows = groups.Take(Math.Max(1, _options.TopCandidateGroupsForReview)).Select(g => EvaluateCandidate(g, allowlistedKeys, allowUnpricedLegsInTemplate)).OrderByDescending(x => x.candidateQualityScore).ThenByDescending(x => x.estimatedNetEdgeConservative ?? decimal.MinValue).Take(Math.Max(1, _options.TopCandidateGroupsForReview)).ToArray();
        return rows.Cast<object>().ToArray();
    }

    private static object ToMarketNode(Market m) => new
    {
        marketId = m.id,
        conditionId = m.conditionId,
        question = m.question,
        active = m.active,
        closed = m.closed,
        archived = m.archived,
        endDate = m.endDate ?? m.endDateIso
    };

    private ReviewRow EvaluateCandidate(MultiOutcomeGroupArbEngine.CandidateGroupReview g, HashSet<string> allowlistedKeys, bool allowUnpricedLegsInTemplate)
    {
        var markets = (g.Markets ?? Array.Empty<Market>()).ToArray();
        var dangerous = LooksDangerous(g.GroupKey, g.Title, markets);
        var winnerLike = LooksWinnerLike(g.GroupKey, g.Title);
        var independentMatches = LooksIndependentMatches(markets);
        var genericNoSemantics = string.Equals(g.Kind, "generic", StringComparison.OrdinalIgnoreCase) && !winnerLike;
        var pricedLegs = markets.Count(m => m.active != false && m.closed != true);
        var missingPrices = Math.Max(0, g.DetectedMarketsCount - pricedLegs);
        var gross = g.EstimatedGrossEdge;
        var net = g.EstimatedNetEdge;
        var rawOnly = gross;
        var score = 0;
        if (winnerLike) score += 50;
        if (g.DetectedMarketsCount >= 5) score += 20;
        if (missingPrices == 0) score += 20;
        if ((gross ?? 0m) >= 0m) score += 20;
        if (dangerous) score -= 100;
        if (independentMatches) score -= 100;
        if (genericNoSemantics) score -= 100;

        var already = allowlistedKeys.Contains(g.GroupKey);
        string action;
        string? rejection = null;
        if (already) action = "AlreadyVerified";
        else if (missingPrices > 0) action = "InsufficientPricing";
        else if (dangerous) { action = "DoNotVerify"; rejection = "DangerousMarketPattern"; }
        else if (independentMatches || genericNoSemantics) { action = "LikelyFalsePositive"; rejection = independentMatches ? "IndependentMatches" : "GenericNoWinnerSemantics"; }
        else if (winnerLike && score > 0) action = "SafeCandidateForManualVerification";
        else action = "NeedsHumanReview";

        var template = action == "SafeCandidateForManualVerification" ? BuildTemplate(g, markets, allowUnpricedLegsInTemplate) : null;
        return new ReviewRow(
            g.GroupKey,
            g.Title,
            g.Kind,
            g.DetectedMarketsCount,
            pricedLegs,
            missingPrices,
            gross,
            net,
            rawOnly,
            score,
            action,
            rejection ?? g.RejectionReason,
            g.Warnings,
            markets.Select(ToMarketNode).Cast<object>().ToArray(),
            template,
            "Copy suggestedAllowlistTemplate into config/verified-multi-outcome-groups.json as a new array item. Restart backend. Confirm VerifiedConfigured increases.",
            already);
    }

    private static object BuildTemplate(MultiOutcomeGroupArbEngine.CandidateGroupReview g, Market[] markets, bool allowUnpricedLegsInTemplate)
    {
        var filtered = markets.Where(m => m.active != false && m.closed != true && !LooksDangerous(m.question, m.question, new[] { m })).ToArray();
        if (!allowUnpricedLegsInTemplate)
            filtered = filtered.Where(m => !string.IsNullOrWhiteSpace(m.id)).ToArray();
        return new
        {
            enabled = true,
            groupKey = g.GroupKey,
            title = g.Title,
            verificationStatus = "Verified",
            groupType = "MutuallyExclusiveWinner",
            allowedStrategy = "BUY_ALL_NO_MUTUALLY_EXCLUSIVE",
            marketIds = filtered.Select(x => x.id).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            conditionIds = filtered.Select(x => x.conditionId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            requiredOutcomeCount = filtered.Length,
            requireExactOutcomeCount = false,
            settlementNotes = "Manually verify that all listed markets are mutually exclusive outcomes of the same event.",
            verifiedBy = "manual",
            verifiedAt = DateTime.UtcNow.ToString("yyyy-MM-dd")
        };
    }

    private HashSet<string> LoadAllowlistedKeys()
    {
        if (!File.Exists(_verifiedAllowlistPath)) return new(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(File.ReadAllText(_verifiedAllowlistPath));
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in doc.RootElement.EnumerateArray())
            if (item.TryGetProperty("groupKey", out var k) && k.ValueKind == JsonValueKind.String)
                keys.Add(k.GetString()!);
        return keys;
    }

    private static bool LooksWinnerLike(string a, string b)
    {
        var t = $"{a} {b}".ToLowerInvariant();
        return t.Contains("winner") || t.Contains("champion") || t.Contains("nominee") || t.Contains("presidential election") || t.Contains("finals winner") || t.Contains("league winner") || t.Contains("tournament winner") || t.Contains("party nominee");
    }

    private static bool LooksDangerous(string key, string title, IEnumerable<Market> markets)
    {
        var txt = $"{key} {title} {string.Join(' ', markets.Select(m => m.question))}".ToLowerInvariant();
        var patterns = new[] { "spread", "o/u", "over/under", " total", "handicap", "set handicap", "game total", "exact score", "margin", "points line", "threshold" };
        return patterns.Any(p => txt.Contains(p));
    }

    private static bool LooksIndependentMatches(Market[] markets)
    {
        var matchRegex = new Regex(@"\b.+\s+vs\.?\s+.+\b", RegexOptions.IgnoreCase);
        var matches = markets.Select(m => m.question ?? string.Empty).Where(q => matchRegex.IsMatch(q)).Select(q => q.ToLowerInvariant()).Distinct().Count();
        return matches >= 2;
    }

    public void ExportIfDue(IReadOnlyList<MultiOutcomeGroupArbEngine.CandidateGroupReview> groups)
    {
        if (!_options.ExportCandidates) return;
        if (groups.Count == 0) { if (!_hasLoggedNoCandidates) { Console.WriteLine("[MULTI_REVIEW] No candidate groups available for export yet"); _hasLoggedNoCandidates = true; } return; }
        var now = DateTime.UtcNow;
        if (_lastExportAtUtc != DateTime.MinValue && now - _lastExportAtUtc < TimeSpan.FromMinutes(Math.Max(1, _options.ExportIntervalMinutes))) return;

        var payload = BuildBoundedCandidates(groups, _options.TopCandidateGroupsForReview, _options.MaxMarketsPerCandidateGroup, includeMarkets: true);
        var review = BuildReviewReport(groups, _options.AllowUnpricedLegsInTemplate);
        Directory.CreateDirectory(Path.GetDirectoryName(_absolutePath)!);
        File.WriteAllText(_absolutePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(_reviewAbsolutePath, JsonSerializer.Serialize(review, new JsonSerializerOptions { WriteIndented = true }));
        _lastExportAtUtc = now;
        _hasLoggedNoCandidates = false;
        Console.WriteLine($"[MULTI_REVIEW] Candidate export written Groups={payload.Count} Path={_absolutePath}");
        Console.WriteLine($"[MULTI_REVIEW] Review report written Groups={review.Count} Path={_reviewAbsolutePath}");
    }

    public void ExportVerifiedPricing(IReadOnlyList<object> payload)
    {
        if (!_options.ExportVerifiedPricing) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_verifiedPricingAbsolutePath)!);
        File.WriteAllText(_verifiedPricingAbsolutePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
}
