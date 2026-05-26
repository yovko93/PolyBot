using System.Text.Json;
using TradingBot.Engines;
using TradingBot.Options;

namespace TradingBot.Services.MultiOutcome;

public sealed class MultiOutcomeCandidateExportService
{
    private readonly MultiOutcomeReviewOptions _options;
    private readonly string _absolutePath;
    private DateTime _lastExportAtUtc = DateTime.MinValue;
    private bool _hasLoggedNoCandidates;

    public MultiOutcomeCandidateExportService(MultiOutcomeReviewOptions options, string contentRootPath)
    {
        _options = options;
        _absolutePath = Path.IsPathRooted(options.ExportPath)
            ? options.ExportPath
            : Path.GetFullPath(Path.Combine(contentRootPath, options.ExportPath));

        if (_options.ExportCandidates)
            Console.WriteLine($"[MULTI_REVIEW] Candidate export enabled Path={_absolutePath}");
        else
            Console.WriteLine("[MULTI_REVIEW] Candidate export disabled");
    }

    public string ExportPathAbsolute => _absolutePath;

    public IReadOnlyList<object> BuildBoundedCandidates(IReadOnlyList<MultiOutcomeGroupArbEngine.CandidateGroupReview> groups, int topGroups, int maxMarkets, bool includeMarkets)
    {
        return groups
            .OrderByDescending(g => g.EstimatedNetEdge ?? decimal.MinValue)
            .ThenByDescending(g => g.DetectedMarketsCount)
            .Take(Math.Max(1, topGroups))
            .Select(g => new
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
                markets = includeMarkets
                    ? (g.Markets ?? Array.Empty<TradingBot.Models.Market>()).Take(Math.Max(1, maxMarkets)).Select(m => new
                    {
                        marketId = m.id,
                        conditionId = m.conditionId,
                        question = m.question,
                        slug = (string?)null,
                        outcome = m.outcomes.FirstOrDefault(),
                        tokenId = m.clobTokenIds.FirstOrDefault(),
                        yesAsk = (decimal?)null,
                        noAsk = (decimal?)null,
                        noAskQuantity = (decimal?)null,
                        active = m.active,
                        closed = m.closed,
                        archived = m.archived,
                        endDate = m.endDate ?? m.endDateIso
                    }).ToArray()
                    : Array.Empty<object>(),
                suggestedAllowlistTemplate = new
                {
                    enabled = true,
                    groupKey = g.GroupKey,
                    title = g.Title,
                    verificationStatus = "Verified",
                    groupType = "MutuallyExclusiveWinner",
                    allowedStrategy = "BUY_ALL_NO_MUTUALLY_EXCLUSIVE",
                    marketIds = (g.Markets ?? Array.Empty<TradingBot.Models.Market>()).Select(m => m.id).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    conditionIds = (g.Markets ?? Array.Empty<TradingBot.Models.Market>()).Select(m => m.conditionId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Cast<string>().ToArray(),
                    requiredOutcomeCount = g.DetectedMarketsCount,
                    settlementNotes = "Manually verify that all listed markets are mutually exclusive outcomes of the same event.",
                    verifiedBy = "manual",
                    verifiedAt = DateTime.UtcNow.ToString("yyyy-MM-dd")
                }
            })
            .Cast<object>()
            .ToArray();
    }

    public void ExportIfDue(IReadOnlyList<MultiOutcomeGroupArbEngine.CandidateGroupReview> groups)
    {
        if (!_options.ExportCandidates) return;
        if (groups.Count == 0)
        {
            if (!_hasLoggedNoCandidates)
            {
                Console.WriteLine("[MULTI_REVIEW] No candidate groups available for export yet");
                _hasLoggedNoCandidates = true;
            }
            return;
        }

        var now = DateTime.UtcNow;
        if (_lastExportAtUtc != DateTime.MinValue && now - _lastExportAtUtc < TimeSpan.FromMinutes(Math.Max(1, _options.ExportIntervalMinutes)))
            return;

        var payload = BuildBoundedCandidates(groups, _options.TopCandidateGroupsForReview, _options.MaxMarketsPerCandidateGroup, includeMarkets: true);
        Directory.CreateDirectory(Path.GetDirectoryName(_absolutePath)!);
        File.WriteAllText(_absolutePath, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        _lastExportAtUtc = now;
        _hasLoggedNoCandidates = false;
        Console.WriteLine($"[MULTI_REVIEW] Candidate export written Groups={payload.Count} Path={_absolutePath}");
    }
}
