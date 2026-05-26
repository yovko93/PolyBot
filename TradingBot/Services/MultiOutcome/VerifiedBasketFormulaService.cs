namespace TradingBot.Services.MultiOutcome;

public sealed class VerifiedBasketFormulaService
{
    public static VerifiedBasketFormulaResult Evaluate(IReadOnlyList<ResolvedNoAsk> pricedLegs, decimal feePerLeg, decimal slippagePerLeg, decimal safetyBufferPerGroup, bool requireAllPrices = true)
    {
        var warnings = new List<string>();
        if (pricedLegs.Count < 2) return new(false, "InsufficientResolvedMarkets", warnings, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (pricedLegs.Any(x => !x.NoAsk.HasValue)) return new(false, "MissingNoAsk", warnings, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var asks = pricedLegs.Select(x => x.NoAsk!.Value).ToList();
        if (asks.Any(x => x < 0m || x > 1m)) return new(false, "InvalidPriceNormalization", warnings, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var legs = asks.Count;
        var guaranteed = legs - 1m;
        if (legs > 2 && guaranteed == 1m) return new(false, "InvalidGuaranteedPayoutFormula", warnings, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
        var noAskSum = asks.Sum();
        if (noAskSum > legs) return new(false, "InvalidBasketCost", warnings, guaranteed, noAskSum, asks.Min(), asks.Max(), asks.Average(), 0, 0, 0, 0, 0);
        var gross = guaranteed - noAskSum;
        var fees = feePerLeg * legs;
        var slip = slippagePerLeg * legs;
        var net = gross - fees - slip - safetyBufferPerGroup;
        if (gross < -1m || net < -1m)
            warnings.Add($"NetEdge outside expected range. Legs={legs} ExpectedMinEdge=-1 Actual={net}");
        return new(true, "None", warnings, guaranteed, noAskSum, asks.Min(), asks.Max(), asks.Average(), gross, fees, slip, safetyBufferPerGroup, net);
    }
}

public sealed record VerifiedBasketFormulaResult(bool IsValid, string SkipReason, IReadOnlyList<string> FormulaWarnings, decimal GuaranteedPayout, decimal NoAskSum, decimal MinNoAsk, decimal MaxNoAsk, decimal AverageNoAsk, decimal GrossEdge, decimal Fees, decimal Slippage, decimal SafetyBuffer, decimal NetEdge);
