using System.Text.Json;
using TradingBot.Options;

namespace TradingBot.Services.MultiOutcome;

public sealed class VerifiedBasketScreener
{
    public enum ExecutionStatus { NotExecutable, NearExecutable, ExecutableUnderActiveProfile, ExperimentalPaperCandidate, StableExperimentalPaperCandidate, PaperOpened, BlockedByStability, BlockedByProfilePolicy, DiagnosticsOnlyPositive }
    public sealed record QuantityScenarioResult(decimal Qty, bool DepthAvailable, string DepthMode, decimal WeightedAverageNoAskPerLeg, decimal NoAskSumAtQty, decimal GrossEdgePerBasket, decimal NetEdgePerBasket, decimal ExpectedProfit, string LimitingLeg, decimal MaxExecutableQty);
    public sealed record ProfileResult(string ProfileName, string FeeModel, decimal Fees, decimal Slippage, decimal Safety, decimal NetEdge, decimal ExpectedProfit, bool WouldBeExecutable, bool DiagnosticsOnly);
    public sealed record ScreenResult(string GroupKey, int Legs, decimal GuaranteedPayout, decimal NoAskSum, decimal GrossEdge, decimal ActiveProfileNetEdge, decimal ExecutableQty, decimal ExpectedProfit, string DominantCost, string Classification, string BestProfile, IReadOnlyList<ProfileResult> ProfileResults, IReadOnlyList<QuantityScenarioResult> QuantityScenarios, string TopMissingReason, DateTime EvaluatedAt, decimal CostReductionNeeded, bool NearExecutable, string RecommendedAction, IReadOnlyList<string> DiagnosticsOnlyPositiveProfiles, decimal ExperimentalProfileNetEdge, ExecutionStatus ExecutionStatus);
    public sealed record Snapshot(DateTime Timestamp, string ActiveProfile, string ExperimentalProfile, IReadOnlyList<string> Profiles, ScreenResult? BestByActiveProfile, ScreenResult? BestByRawEdge, ScreenResult? BestByConservative, ScreenResult? BestByPolymarketApprox, ScreenResult? BestByRaw, ScreenResult? BestNearExecutable, IReadOnlyList<ScreenResult> NearExecutableBaskets, IReadOnlyList<ScreenResult> VerifiedBaskets, IReadOnlyList<ScreenResult> ExperimentalCandidates, IReadOnlyList<ScreenResult> StableExperimentalCandidates, IReadOnlyList<ScreenResult> ActiveProfileExecutable, IReadOnlyList<ScreenResult> DiagnosticsOnlyPositive, IReadOnlyList<object> UnresolvedConfiguredGroups);

    public static ScreenResult Evaluate(string groupKey, IReadOnlyList<ResolvedNoAsk> legs, MultiOutcomeArbitrageOptions options)
    {
        var quantityScenarios = BuildQuantityResults(legs, options);
        var noAskForActive = legs.Select(x => new ResolvedNoAsk(x.MarketId, x.ConditionId, x.NoAsk, x.NoAskQuantity, x.Source, x.YesBid, x.YesBidQuantity, x.NoTokenId, x.TimestampUtc, x.IsStale, x.FailureReason)).ToList();
        var results = new List<ProfileResult>();
        foreach (var kv in options.CostProfiles.Profiles)
        {
            var f = VerifiedBasketFormulaService.Evaluate(noAskForActive, kv.Value.FeePerLeg, kv.Value.SlippageBufferPerLeg, kv.Value.SafetyBufferPerGroup);
            var diagnosticsOnly = !kv.Key.Equals(options.CostProfiles.ActiveProfile, StringComparison.OrdinalIgnoreCase);
            results.Add(new ProfileResult(kv.Key, kv.Value.FeeModel, f.Fees, f.Slippage, f.SafetyBuffer, f.NetEdge, f.NetEdge, f.NetEdge > options.MinMultiOutcomeEdge, diagnosticsOnly));
        }

        var active = results.First(x => string.Equals(x.ProfileName, options.CostProfiles.ActiveProfile, StringComparison.OrdinalIgnoreCase));
        var activeCfg = options.CostProfiles.Profiles[active.ProfileName];
        var formula = VerifiedBasketFormulaService.Evaluate(legs, activeCfg.FeePerLeg, activeCfg.SlippageBufferPerLeg, activeCfg.SafetyBufferPerGroup);
        var breakdown = VerifiedBasketDiagnostics.Compute(groupKey, legs.Count, formula, activeCfg.FeePerLeg, activeCfg.SlippageBufferPerLeg, options.NearExecutableCostReductionThreshold, options.FarFromExecutableCostReductionThreshold);
        var bestProfile = results.OrderByDescending(x => x.NetEdge).First().ProfileName;
        var poly = results.FirstOrDefault(x => x.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase));
        var raw = results.FirstOrDefault(x => x.ProfileName.Equals("RawOnly", StringComparison.OrdinalIgnoreCase));
        var nearExecutable = formula.NetEdge <= 0m && ((poly?.NetEdge > 0m) || (raw?.NetEdge > 0m) || breakdown.CostReductionNeeded <= 0.005m);
        var diagPositive = results.Where(x => x.DiagnosticsOnly && x.NetEdge > 0m).Select(x => x.ProfileName).ToArray();
        var recommendedAction = nearExecutable ? "NearExecutableReviewCosts"
            : formula.GrossEdge > 0m ? "Monitor"
            : formula.GrossEdge < 0m ? "DisableUntilBetterPricing"
            : "NotActionable";
        var experimentalNet = poly?.NetEdge ?? 0m;
        var orderbook = results.FirstOrDefault(x => x.ProfileName.Equals("OrderbookOnly", StringComparison.OrdinalIgnoreCase))?.NetEdge;
        var status = formula.NetEdge > options.MinMultiOutcomeEdge ? ExecutionStatus.ExecutableUnderActiveProfile
            : experimentalNet > options.MinMultiOutcomeEdge ? ExecutionStatus.ExperimentalPaperCandidate
            : ((raw?.NetEdge > options.MinMultiOutcomeEdge) || (orderbook > options.MinMultiOutcomeEdge)) ? ExecutionStatus.DiagnosticsOnlyPositive
            : nearExecutable ? ExecutionStatus.NearExecutable : ExecutionStatus.NotExecutable;
        return new ScreenResult(groupKey, legs.Count, formula.GuaranteedPayout, formula.NoAskSum, formula.GrossEdge, formula.NetEdge, formula.NetEdge > options.MinMultiOutcomeEdge ? 1m : 0m, formula.NetEdge, breakdown.DominantCostComponent, breakdown.Classification, bestProfile, results, quantityScenarios, "None", DateTime.UtcNow, breakdown.CostReductionNeeded, nearExecutable, recommendedAction, diagPositive, experimentalNet, status);
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
            var net = gross - (active.FeePerLeg * legs.Count) - (active.SlippageBufferPerLeg * legs.Count) - active.SafetyBufferPerGroup;
            results.Add(new QuantityScenarioResult(qty, false, "BestAskOnly", weighted, noAskSum, gross, net, net * qty, legs.FirstOrDefault(x => (x.NoAskQuantity ?? decimal.MaxValue) == maxQty)?.MarketId ?? "None", maxQty));
        }
        return results;
    }

    public static Snapshot BuildSnapshot(string activeProfile, string experimentalProfile, IReadOnlyList<ScreenResult> groups, IReadOnlyList<object> unresolvedConfiguredGroups)
    {
        var ranking = groups.OrderByDescending(x => x.ActiveProfileNetEdge)
            .ThenByDescending(x => x.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? decimal.MinValue)
            .ThenByDescending(x => x.GrossEdge).ThenByDescending(x => x.ExecutableQty).ThenBy(x => x.Legs).ThenBy(x => x.GroupKey, StringComparer.OrdinalIgnoreCase).ToList();
        var near = groups.Where(x => x.NearExecutable).ToList();
        ScreenResult? BestBy(string profile) => groups.OrderByDescending(x => x.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals(profile, StringComparison.OrdinalIgnoreCase))?.NetEdge ?? decimal.MinValue).ThenBy(x => x.CostReductionNeeded).FirstOrDefault();
        return new Snapshot(DateTime.UtcNow, activeProfile, experimentalProfile, groups.SelectMany(x => x.ProfileResults.Select(p => p.ProfileName)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray(), ranking.FirstOrDefault(), groups.OrderByDescending(x => x.GrossEdge).FirstOrDefault(), BestBy("Conservative"), BestBy("PolymarketApprox"), BestBy("RawOnly"), near.OrderByDescending(x => x.GrossEdge).FirstOrDefault(), near, ranking, groups.Where(x=>x.ExecutionStatus==ExecutionStatus.ExperimentalPaperCandidate).ToArray(), groups.Where(x=>x.ExecutionStatus==ExecutionStatus.StableExperimentalPaperCandidate).ToArray(), groups.Where(x=>x.ExecutionStatus==ExecutionStatus.ExecutableUnderActiveProfile).ToArray(), groups.Where(x=>x.ExecutionStatus==ExecutionStatus.DiagnosticsOnlyPositive).ToArray(), unresolvedConfiguredGroups);
    }

    public static void Export(string path, Snapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    }
}
