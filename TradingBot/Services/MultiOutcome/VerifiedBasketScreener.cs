using System.Text.Json;
using TradingBot.Options;

namespace TradingBot.Services.MultiOutcome;

public sealed class VerifiedBasketScreener
{
    public sealed record QuantityScenarioResult(decimal Qty, bool DepthUnavailable, decimal WeightedAverageNoAskPerLeg, decimal TotalBasketCost, decimal GrossEdgePerBasket, decimal TotalFees, decimal TotalSlippage, decimal NetEdgePerBasket, decimal ExpectedProfit, string LimitingLeg, decimal MaxExecutableQty);
    public sealed record ProfileResult(string ProfileName, string FeeModel, decimal Fees, decimal Slippage, decimal Safety, decimal NetEdge, decimal ExpectedProfit, bool WouldBeExecutable);
    public sealed record ScreenResult(string GroupKey, int Legs, decimal GuaranteedPayout, decimal NoAskSum, decimal GrossEdge, decimal ActiveProfileNetEdge, decimal ExecutableQty, decimal ExpectedProfit, string DominantCost, string Classification, string BestProfile, IReadOnlyList<ProfileResult> ProfileResults, IReadOnlyList<QuantityScenarioResult> QuantityResults, string TopMissingReason, DateTime EvaluatedAt, decimal CostReductionNeeded, bool NearExecutable);
    public sealed record ProfileComparisonSummary(string BestConservative, string BestPolymarketApprox, string BestOrderbookOnly, string BestRaw);
    public sealed record Snapshot(string ActiveCostProfile, DateTime Timestamp, IReadOnlyList<ScreenResult> VerifiedGroups, IReadOnlyList<ScreenResult> Ranking, IReadOnlyList<ScreenResult> NearExecutableBaskets, ProfileComparisonSummary ProfileComparison, IReadOnlyList<string> RecommendedActions);

    public static ScreenResult Evaluate(string groupKey, IReadOnlyList<ResolvedNoAsk> legs, MultiOutcomeArbitrageOptions options)
    {
        var quantityScenarios = BuildQuantityResults(legs, options);
        var noAskForActive = legs.Select(x => new ResolvedNoAsk(x.MarketId, x.ConditionId, x.NoAsk, x.NoAskQuantity, x.Source, x.YesBid, x.YesBidQuantity, x.NoTokenId, x.TimestampUtc, x.IsStale, x.FailureReason)).ToList();
        var results = new List<ProfileResult>();
        foreach (var kv in options.CostProfiles.Profiles)
        {
            var f = VerifiedBasketFormulaService.Evaluate(noAskForActive, kv.Value.FeePerLeg, kv.Value.SlippageBufferPerLeg, kv.Value.SafetyBufferPerGroup);
            results.Add(new ProfileResult(kv.Key, kv.Value.FeeModel, f.Fees, f.Slippage, f.SafetyBuffer, f.NetEdge, f.NetEdge, f.NetEdge > options.MinMultiOutcomeEdge));
        }

        var active = results.First(x => string.Equals(x.ProfileName, options.CostProfiles.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        var activeCfg = options.CostProfiles.Profiles[active.ProfileName];
        var formula = VerifiedBasketFormulaService.Evaluate(legs, activeCfg.FeePerLeg, activeCfg.SlippageBufferPerLeg, activeCfg.SafetyBufferPerGroup);
        var breakdown = VerifiedBasketDiagnostics.Compute(groupKey, legs.Count, formula, activeCfg.FeePerLeg, activeCfg.SlippageBufferPerLeg, options.NearExecutableCostReductionThreshold, options.FarFromExecutableCostReductionThreshold);
        var bestProfile = results.OrderByDescending(x => x.NetEdge).First().ProfileName;
        var poly = results.FirstOrDefault(x => x.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase));
        var nearExecutable = formula.GrossEdge > 0m && formula.NetEdge <= 0m && ((poly?.NetEdge ?? decimal.MinValue) > 0m || breakdown.CostReductionNeeded <= 0.005m);

        return new ScreenResult(groupKey, legs.Count, formula.GuaranteedPayout, formula.NoAskSum, formula.GrossEdge, formula.NetEdge, formula.NetEdge > options.MinMultiOutcomeEdge ? 1m : 0m, formula.NetEdge, breakdown.DominantCostComponent, breakdown.Classification, bestProfile, results, quantityScenarios, "None", DateTime.UtcNow, breakdown.CostReductionNeeded, nearExecutable);
    }

    private static List<QuantityScenarioResult> BuildQuantityResults(IReadOnlyList<ResolvedNoAsk> legs, MultiOutcomeArbitrageOptions options)
    {
        var maxQty = legs.Count == 0 ? 0m : legs.Min(x => x.NoAskQuantity ?? 1m);
        var qtys = new List<decimal> { 1m, 5m, 10m, 25m, 50m, maxQty }.Distinct().OrderBy(x => x).ToList();
        var active = options.CostProfiles.Profiles[options.CostProfiles.ActiveProfile];
        var results = new List<QuantityScenarioResult>();
        foreach (var qty in qtys)
        {
            var weighted = legs.Where(x => x.NoAsk.HasValue).Select(x => x.NoAsk!.Value).DefaultIfEmpty(0m).Average();
            var noAskSum = legs.Where(x => x.NoAsk.HasValue).Sum(x => x.NoAsk!.Value);
            var guaranteed = legs.Count - 1m;
            var gross = guaranteed - noAskSum;
            var fees = active.FeePerLeg * legs.Count;
            var slippage = active.SlippageBufferPerLeg * legs.Count;
            var net = gross - fees - slippage - active.SafetyBufferPerGroup;
            results.Add(new QuantityScenarioResult(qty, false, weighted, noAskSum, gross, fees, slippage, net, net * qty, legs.FirstOrDefault(x => (x.NoAskQuantity ?? decimal.MaxValue) == maxQty)?.MarketId ?? "None", maxQty));
        }
        return results;
    }

    public static Snapshot BuildSnapshot(string activeProfile, IReadOnlyList<ScreenResult> groups, IReadOnlyList<string> recommendedActions)
    {
        var ranking = groups.OrderByDescending(x => x.ActiveProfileNetEdge).ThenByDescending(x => x.GrossEdge).ThenBy(x => x.CostReductionNeeded).ThenByDescending(x => x.ExecutableQty).ToList();
        var near = groups.Where(x => x.NearExecutable).ToList();
        string Best(string profile) => groups.OrderByDescending(x => x.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals(profile, StringComparison.OrdinalIgnoreCase))?.NetEdge ?? decimal.MinValue).ThenBy(x => x.CostReductionNeeded).FirstOrDefault()?.GroupKey ?? "N/A";
        var pc = new ProfileComparisonSummary(Best("Conservative"), Best("PolymarketApprox"), Best("OrderbookOnly"), Best("RawOnly"));
        return new Snapshot(activeProfile, DateTime.UtcNow, groups, ranking, near, pc, recommendedActions);
    }

    public static void Export(string path, Snapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    }
}
