using System.Text.Json;
using TradingBot.Options;

namespace TradingBot.Services.MultiOutcome;

public sealed class VerifiedBasketScreener
{
    public sealed record ProfileResult(string ProfileName, decimal Fees, decimal Slippage, decimal Safety, decimal NetEdge, decimal ExpectedProfit, bool WouldBeExecutable);
    public sealed record ScreenResult(string GroupKey, int Legs, decimal GuaranteedPayout, decimal NoAskSum, decimal GrossEdge, decimal ActiveProfileNetEdge, decimal ExecutableQty, decimal ExpectedProfit, string DominantCost, string Classification, string BestProfile, IReadOnlyList<ProfileResult> ProfileResults, string TopMissingReason, DateTime EvaluatedAt, decimal CostReductionNeeded);
    public sealed record Snapshot(string ActiveCostProfile, DateTime Timestamp, IReadOnlyList<ScreenResult> VerifiedGroups, IReadOnlyList<ScreenResult> Ranking, IReadOnlyList<string> RecommendedActions);

    public static ScreenResult Evaluate(string groupKey, IReadOnlyList<ResolvedNoAsk> legs, MultiOutcomeArbitrageOptions options)
    {
        var profiles = options.CostProfiles.Profiles;
        var results = new List<ProfileResult>();
        foreach (var kv in profiles)
        {
            var f = VerifiedBasketFormulaService.Evaluate(legs, kv.Value.FeePerLeg, kv.Value.SlippageBufferPerLeg, kv.Value.SafetyBufferPerGroup);
            results.Add(new ProfileResult(kv.Key, f.Fees, f.Slippage, f.SafetyBuffer, f.NetEdge, f.NetEdge, f.NetEdge > options.MinMultiOutcomeEdge));
        }

        var active = results.FirstOrDefault(x => string.Equals(x.ProfileName, options.CostProfiles.ActiveProfile, StringComparison.OrdinalIgnoreCase)) ?? results.First();
        var activeCfg = profiles[active.ProfileName];
        var formula = VerifiedBasketFormulaService.Evaluate(legs, activeCfg.FeePerLeg, activeCfg.SlippageBufferPerLeg, activeCfg.SafetyBufferPerGroup);
        var breakdown = VerifiedBasketDiagnostics.Compute(groupKey, legs.Count, formula, activeCfg.FeePerLeg, activeCfg.SlippageBufferPerLeg, options.NearExecutableCostReductionThreshold, options.FarFromExecutableCostReductionThreshold);
        var bestProfile = results.OrderByDescending(x => x.NetEdge).First().ProfileName;

        return new ScreenResult(groupKey, legs.Count, formula.GuaranteedPayout, formula.NoAskSum, formula.GrossEdge, formula.NetEdge, formula.NetEdge > options.MinMultiOutcomeEdge ? 1m : 0m, formula.NetEdge, breakdown.DominantCostComponent, breakdown.Classification, bestProfile, results, "None", DateTime.UtcNow, breakdown.CostReductionNeeded);
    }

    public static void Export(string path, Snapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
    }
}
