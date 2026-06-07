using System.Text.Json;
using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record PaperOpportunityFunnelSnapshot(
    DateTime Timestamp,
    TimeSpan Uptime,
    int PaperPhase,
    int ScannedMarkets,
    int SingleMarketCandidates,
    int VerifiedBasketCandidates,
    int BelowMinEdge,
    int DataQualityRejected,
    int WaitingForStableEdge,
    int WaitingForExecutionReadiness,
    int FillSimulationRejected,
    int DiagnosticsOnlyProfile,
    int ExperimentalPaperDisabled,
    bool DiscoveryPartial,
    int OrderbookUnavailable,
    int InvalidTokenQuarantined,
    int RiskCapRejected,
    int DuplicateSuppressed,
    int PaperOpened,
    IReadOnlyList<object> TopRejectedSamples,
    IReadOnlyList<object> TopNearExecutableSamples);

public static class PaperOpportunityFunnelExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static PaperOpportunityFunnelSnapshot Build(
        TradingBotOptions options,
        BotRuntimeState state,
        SingleMarketScanStats singleMarketStats,
        MultiOutcomeGroupArbEngine.MultiOutcomeScanReport multiOutcomeReport,
        int scannedMarkets,
        bool discoveryPartial = false)
    {
        var single = state.SingleMarketSnapshot.Summary;
        var rejectCounts = Merge(single.RejectedByReason, multiOutcomeReport.RejectedByReason);
        var fillSimulationRejected = CountReasons(rejectCounts, "Fill", "PartialFill", "FillSimulation", "FillAdjustedProfitBelowThreshold");
        var riskCapRejected = CountReasons(rejectCounts, "Risk", "MaxPaper", "MaxOpen", "MaxExposure", "MaxNotional", "InsufficientPaperCash")
            + state.PaperPretradeRejectsByReason.Where(x => IsAny(x.Key, "MaxPaper", "MaxOpen", "MaxExposure", "MaxNotional", "Risk", "InsufficientPaperCash")).Sum(x => x.Value);
        var diagnosticsOnlyProfile = CountReasons(rejectCounts, "DiagnosticsOnly");
        var experimentalPaperDisabled = options.PaperRisk.AllowExperimentalPaper ? 0 : CountReasons(rejectCounts, "Experimental", "BlockedByProfilePolicy");

        var topRejected = rejectCounts
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .Select(x => (object)new { reason = x.Key, count = x.Value })
            .Concat(multiOutcomeReport.TopRejectedSamples.Take(10).Select(x => (object)new { source = "verifiedBasket", groupKey = x.GroupKey, reason = x.Reason }))
            .Concat(state.SingleMarketSnapshot.DataQualityRejectSamples.Take(10).Select(x => (object)new { source = "singleMarketDataQuality", x.MarketId, x.Title, x.Reason, x.RawSum, x.EdgePerShare }))
            .Take(25)
            .ToArray();

        var nearExecutable = state.SingleMarketSnapshot.TopNearMisses.Take(10)
            .Select(x => (object)new { source = "singleMarket", x.MarketId, x.Title, x.EdgePerShare, x.RequiredImprovement, x.RawSum })
            .Concat(multiOutcomeReport.CandidateGroupsForReview
                .OrderByDescending(x => x.EstimatedNetEdge ?? decimal.MinValue)
                .Take(10)
                .Select(x => (object)new { source = "verifiedBasket", x.GroupKey, status = x.VerificationStatus, reason = x.RejectionReason, edge = x.EstimatedNetEdge, cost = x.EstimatedNoBasketCost, guaranteedPayout = x.GuaranteedPayoutIfVerified }))
            .Take(20)
            .ToArray();

        return new PaperOpportunityFunnelSnapshot(
            Timestamp: DateTime.UtcNow,
            Uptime: RuntimeHealthSnapshot.From(state, options).Uptime,
            PaperPhase: options.TradingMode.PaperPhase,
            ScannedMarkets: scannedMarkets,
            SingleMarketCandidates: singleMarketStats.Candidates,
            VerifiedBasketCandidates: multiOutcomeReport.GroupsVerified,
            BelowMinEdge: single.BelowMinEdge + CountReasons(rejectCounts, "BelowMinEdge", "BelowMinEdgeThreshold"),
            DataQualityRejected: single.DataQualityRejected,
            WaitingForStableEdge: Math.Max(0, single.PositiveEdge - single.EdgeStable),
            WaitingForExecutionReadiness: Math.Max(0, single.EdgeStable - single.ExecutionReady),
            FillSimulationRejected: fillSimulationRejected,
            DiagnosticsOnlyProfile: diagnosticsOnlyProfile,
            ExperimentalPaperDisabled: experimentalPaperDisabled,
            DiscoveryPartial: discoveryPartial,
            OrderbookUnavailable: CountReasons(rejectCounts, "OrderbookUnavailable", "BookCacheMiss", "MissingOrderbook", "NoAsk", "MissingNoAsk"),
            InvalidTokenQuarantined: state.OrderBookServiceStats.QuarantinedTokens,
            RiskCapRejected: riskCapRejected,
            DuplicateSuppressed: state.PaperDuplicateSuppressions,
            PaperOpened: state.PaperExecutionsCount,
            TopRejectedSamples: topRejected,
            TopNearExecutableSamples: nearExecutable);
    }

    public static void ExportLatest(string exportsRoot, PaperOpportunityFunnelSnapshot snapshot)
    {
        Directory.CreateDirectory(exportsRoot);
        File.WriteAllText(Path.Combine(exportsRoot, "paper-opportunity-funnel-latest.json"), JsonSerializer.Serialize(snapshot, JsonOptions));
    }

    private static Dictionary<string, int> Merge(params IReadOnlyDictionary<string, int>[] dictionaries)
    {
        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var dictionary in dictionaries)
            foreach (var (key, value) in dictionary)
                merged[key] = merged.TryGetValue(key, out var existing) ? existing + value : value;
        return merged;
    }

    private static int CountReasons(IReadOnlyDictionary<string, int> reasons, params string[] tokens)
        => reasons.Where(x => IsAny(x.Key, tokens)).Sum(x => x.Value);

    private static bool IsAny(string value, params string[] tokens)
        => tokens.Any(t => value.Contains(t, StringComparison.OrdinalIgnoreCase));
}
