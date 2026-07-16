using System.Text.Json;
using TradingBot.Api;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public static class PaperPhase1EligibilityLadderExporter
{
    public static object Latest { get; private set; } = new { enabled = false };
    public static IReadOnlyList<object> LatestTopNearEligible { get; private set; } = Array.Empty<object>();

    public static object Build(BotRuntimeState state, TradingBotOptions options, RuntimeHealthSnapshot health)
    {
        var cfg = options.PaperPhase1EligibilityLadder;
        var minEdge = health.PaperPhase1MinEdge;
        var nearWindow = cfg.NearEdgeWindow;
        var audit = state.SingleMarketSnapshot.TopOpportunityAuditNearMisses
            .Where(x => HasBothAsks(x) && x.AfterSafetyEdge < minEdge && minEdge - x.AfterSafetyEdge <= nearWindow && !IsInvalidRawSpike(x))
            .OrderBy(x => minEdge - x.AfterSafetyEdge)
            .ThenByDescending(x => x.AfterSafetyEdge)
            .ThenByDescending(x => x.ExecutableQty)
            .Take(Math.Max(1, cfg.MaxItems))
            .Select((x, i) => ToNearEligible(x, i + 1, minEdge, state.SingleMarketSnapshot.ScanId))
            .Cast<object>()
            .ToArray();

        var rejectedByReason = BuildRejectedByReason(state.SingleMarketSnapshot.Summary);
        var payload = new
        {
            generatedAtUtc = DateTime.UtcNow,
            processRunId = health.ProcessRunId,
            profile = health.RuntimeProfile,
            enabled = cfg.Enabled && health.PaperPhase1ProfileActive,
            diagnosticsOnly = cfg.DiagnosticsOnly,
            minEdge,
            nearEdgeWindow = nearWindow,
            summary = new
            {
                seen = health.PaperPhase1LadderSeen,
                validPriced = health.PaperPhase1LadderValidPriced,
                afterSafetyComputed = health.PaperPhase1LadderAfterSafetyComputed,
                nearBreakEven = health.PaperPhase1LadderNearBreakEven,
                positiveAfterSafety = health.PaperPhase1LadderPositiveAfterSafety,
                paperEligible = health.PaperPhase1LadderPaperEligible,
                opened = health.PaperPhase1LadderOpened,
                canaryOpenAttempted = health.PaperPhase1CanaryLadderOpenAttempted,
                canaryOpened = health.PaperPhase1CanaryLadderOpened,
                canarySettled = health.PaperPhase1CanaryLadderSettled,
                bestAfterSafetyEdge = health.PaperPhase1LadderBestAfterSafetyEdge,
                bestDistanceToMinEdge = health.PaperPhase1LadderBestDistanceToMinEdge,
                medianDistanceToMinEdge = health.PaperPhase1LadderMedianDistanceToMinEdge,
                topBlockingReason = health.PaperPhase1LadderTopBlockingReason,
                consistent = health.PaperPhase1LadderConsistent
            },
            rejectedByReason,
            nearEligible = audit,
            invalidTopReasons = cfg.IncludeInvalidTopReasons ? state.SingleMarketSnapshot.Summary.DataQualityRejectedByReason.OrderByDescending(x => x.Value).Take(20).Select(x => (object)new { reason = NormalizeReason(x.Key), count = x.Value }).ToArray() : Array.Empty<object>(),
            missingPricingSummary = new { enabled = cfg.IncludeMissingPricingSummary, missingNoAsk = CountReason(state.SingleMarketSnapshot.Summary, "MissingNoAsk"), missingYesAsk = CountReason(state.SingleMarketSnapshot.Summary, "MissingYesAsk"), missingBothAsks = CountReason(state.SingleMarketSnapshot.Summary, "MissingBothAsks"), missingBook = CountReason(state.SingleMarketSnapshot.Summary, "MissingBook") },
            gateBreakdown = new { enabled = cfg.IncludeGateBreakdown, edgeRejected = health.PaperPhase1EdgeRejected, depthRejected = health.PaperPhase1DepthRejected, fillRejected = health.PaperPhase1FillRejected, riskRejected = health.PaperPhase1RiskRejected }
        };
        Latest = payload;
        LatestTopNearEligible = audit;
        return payload;
    }

    public static void ExportLatest(BotRuntimeState state, TradingBotOptions options, RuntimeHealthSnapshot health, string root)
    {
        if (!options.PaperPhase1EligibilityLadder.Enabled || !options.PaperPhase1EligibilityLadder.ExportEnabled || !health.PaperPhase1ProfileActive) return;
        try
        {
            var path = Path.IsPathRooted(options.PaperPhase1EligibilityLadder.ExportPath) ? options.PaperPhase1EligibilityLadder.ExportPath : Path.Combine(root, options.PaperPhase1EligibilityLadder.ExportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(Build(state, options, health), new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, true);
        }
        catch (Exception ex) { Console.WriteLine($"[PAPER_PHASE1_ELIGIBILITY_LADDER_EXPORT_WARNING] Error={ex.Message}"); }
    }

    private static object ToNearEligible(SingleMarketOpportunityAuditDto x, int rank, decimal minEdge, long sourceCycle)
    {
        var reasons = BlockingReasons(x, minEdge);
        return new
        {
            rank,
            candidateId = $"SingleMarketBuyBoth:{x.MarketId}:{sourceCycle}",
            marketId = x.MarketId,
            question = x.Title,
            category = "SingleMarketBuyBoth",
            stage = "NearBreakEven",
            firstBlockingReason = reasons.FirstOrDefault() ?? "BelowMinEdge",
            allBlockingReasons = reasons,
            rawEdge = x.RawEdge,
            afterCostEdge = x.AfterCostEdge,
            afterSafetyEdge = x.AfterSafetyEdge,
            distanceToBreakEven = Math.Max(0m, -x.AfterSafetyEdge),
            distanceToMinEdge = Math.Max(0m, minEdge - x.AfterSafetyEdge),
            yesAsk = x.YesAsk,
            noAsk = x.NoAsk,
            sumAsk = x.YesAsk + x.NoAsk,
            fees = x.AfterCostEdge - x.RawEdge,
            safetyBuffer = x.AfterSafetyEdge - x.AfterCostEdge,
            executableQty = x.ExecutableQty,
            yesAskSize = x.AvailableQty,
            noAskSize = x.AvailableQty,
            maxNotional = x.NotionalAtCap,
            tokenIds = Array.Empty<string>(),
            sourceCycle
        };
    }

    private static string[] BlockingReasons(SingleMarketOpportunityAuditDto x, decimal minEdge)
    {
        var reasons = new List<string>();
        if (x.AfterSafetyEdge < minEdge) reasons.Add("BelowMinEdge");
        if (!x.DepthPassed) reasons.Add("InsufficientDepth");
        if (!x.FillPassed) reasons.Add("FillRejected");
        if (!x.RiskPassed) reasons.Add("RiskRejected");
        if (!x.PaperDiagnosticsLimitedGatePassed) reasons.Add("PaperPhaseNotArmed");
        if (!string.IsNullOrWhiteSpace(x.DataQualityReason)) reasons.Add(NormalizeReason(x.DataQualityReason));
        return reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool HasBothAsks(SingleMarketOpportunityAuditDto x) => x.YesAsk > 0m && x.NoAsk > 0m;
    private static bool IsInvalidRawSpike(SingleMarketOpportunityAuditDto x) => !string.IsNullOrWhiteSpace(x.DataQualityReason) || x.RejectedReason.Contains("Missing", StringComparison.OrdinalIgnoreCase) || x.RejectedReason.Contains("Suspicious", StringComparison.OrdinalIgnoreCase);
    private static IReadOnlyDictionary<string, long> BuildRejectedByReason(SingleMarketScanSummaryDto summary) => summary.RejectedByReason.Concat(summary.DataQualityRejectedByReason).GroupBy(x => NormalizeReason(x.Key), StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => (long)x.Sum(v => v.Value), StringComparer.OrdinalIgnoreCase);
    private static long CountReason(SingleMarketScanSummaryDto summary, string reason) => BuildRejectedByReason(summary).TryGetValue(reason, out var count) ? count : 0;
    public static string NormalizeReason(string? reason) => string.IsNullOrWhiteSpace(reason) ? "Unknown" : reason switch
    {
        var r when r.Contains("MissingNoAsk", StringComparison.OrdinalIgnoreCase) => "MissingNoAsk",
        var r when r.Contains("MissingYesAsk", StringComparison.OrdinalIgnoreCase) => "MissingYesAsk",
        var r when r.Contains("MissingBoth", StringComparison.OrdinalIgnoreCase) => "MissingBothAsks",
        var r when r.Contains("MissingBook", StringComparison.OrdinalIgnoreCase) || r.Contains("OrderbookUnavailable", StringComparison.OrdinalIgnoreCase) => "MissingBook",
        var r when r.Contains("Token", StringComparison.OrdinalIgnoreCase) && r.Contains("Unverified", StringComparison.OrdinalIgnoreCase) => "TokenOutcomeMappingUnverified",
        var r when r.Contains("Suspicious", StringComparison.OrdinalIgnoreCase) => "SuspiciousYesNoAskSum",
        var r when r.Contains("Stale", StringComparison.OrdinalIgnoreCase) => "StaleOrderbook",
        var r when r.Contains("Depth", StringComparison.OrdinalIgnoreCase) => "InsufficientDepth",
        var r when r.Contains("Fill", StringComparison.OrdinalIgnoreCase) => "FillRejected",
        var r when r.Contains("Risk", StringComparison.OrdinalIgnoreCase) => "RiskRejected",
        var r when r.Contains("Stable", StringComparison.OrdinalIgnoreCase) => "EdgeNotStable",
        var r when r.Contains("BelowMinEdge", StringComparison.OrdinalIgnoreCase) => "BelowMinEdge",
        _ => reason!
    };
}
