namespace TradingBot.Services.MultiOutcome;

public static class VerifiedBasketDiagnostics
{
    public static VerifiedBasketCostBreakdown Compute(string groupKey, int legs, VerifiedBasketFormulaResult formula, decimal feePerLeg, decimal slippagePerLeg, decimal nearThreshold, decimal farThreshold)
    {
        var totalCosts = formula.Fees + formula.Slippage + formula.SafetyBuffer;
        var reduction = Math.Max(0m, totalCosts - formula.GrossEdge);
        var dominant = formula.Fees >= formula.Slippage && formula.Fees >= formula.SafetyBuffer ? "Fees" : formula.Slippage >= formula.SafetyBuffer ? "Slippage" : "Safety";
        var maxFeePerLeg = legs <= 0 ? 0m : Math.Max(0m, (formula.GrossEdge - formula.SafetyBuffer - formula.Slippage) / legs);
        var maxSlipPerLeg = legs <= 0 ? 0m : Math.Max(0m, (formula.GrossEdge - formula.SafetyBuffer - formula.Fees) / legs);
        var classification = Classify(formula, reduction, nearThreshold, farThreshold);
        var sensitivity = new VerifiedBasketSensitivityScenarios(
            formula.NetEdge,
            formula.GrossEdge - formula.Slippage - formula.SafetyBuffer,
            formula.GrossEdge - formula.Fees - formula.SafetyBuffer,
            formula.GrossEdge - formula.SafetyBuffer,
            formula.GrossEdge,
            formula.GrossEdge - (formula.Fees / 2m) - (formula.Slippage / 2m) - formula.SafetyBuffer);

        return new(groupKey, legs, formula.GuaranteedPayout, formula.NoAskSum, formula.GrossEdge, formula.Fees, formula.Slippage, formula.SafetyBuffer, formula.NetEdge, totalCosts, reduction, classification, dominant, maxFeePerLeg, maxSlipPerLeg, feePerLeg, slippagePerLeg, sensitivity);
    }

    public static string Classify(VerifiedBasketFormulaResult formula, decimal reductionNeeded, decimal nearThreshold, decimal farThreshold)
    {
        if (!formula.IsValid) return "FormulaError";
        if (formula.SkipReason == "MissingNoAsk") return "MissingPrices";
        if (formula.NetEdge > 0) return "Executable";
        if (formula.GrossEdge > 0 && formula.NetEdge <= 0) return "RawPositiveNetNegative";
        if (reductionNeeded <= nearThreshold) return "NearExecutable";
        if (reductionNeeded > farThreshold) return "FarFromExecutable";
        return "FarFromExecutable";
    }
}

public sealed record VerifiedBasketSensitivityScenarios(decimal Actual, decimal ZeroFees, decimal ZeroSlippage, decimal ZeroFeesZeroSlippage, decimal RawOnly, decimal HalfFeesHalfSlippage);
public sealed record VerifiedBasketCostBreakdown(string GroupKey, int Legs, decimal GuaranteedPayout, decimal NoAskSum, decimal GrossEdge, decimal Fees, decimal Slippage, decimal Safety, decimal NetEdge, decimal CurrentTotalCosts, decimal CostReductionNeeded, string Classification, string DominantCostComponent, decimal BreakEvenFeeLimit, decimal BreakEvenSlippageLimit, decimal FeePerLeg, decimal SlippagePerLeg, VerifiedBasketSensitivityScenarios SensitivityScenarios);
