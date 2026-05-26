using TradingBot.Services.MultiOutcome;
using TradingBot.Options;
using Xunit;

namespace TradingBot.Tests;

public class VerifiedBasketDiagnosticsTests
{
    [Fact]
    public void Computes_costs_and_sensitivity()
    {
        var f = new VerifiedBasketFormulaResult(true,"None",Array.Empty<string>(),35m,34.982m,0.8m,0.99m,0.97m,0.018m,0.036m,0.018m,0.001m,-0.037m);
        var d = VerifiedBasketDiagnostics.Compute("g",36,f,0.001m,0.0005m,0.01m,0.05m);
        Assert.Equal(-0.037m, d.NetEdge);
        Assert.Equal("Fees", d.DominantCostComponent);
        Assert.Equal(-0.001m, d.SensitivityScenarios.ZeroFees);
        Assert.Equal(-0.019m, d.SensitivityScenarios.ZeroSlippage);
        Assert.Equal(0.017m, d.SensitivityScenarios.ZeroFeesZeroSlippage);
        Assert.Equal(0.018m, d.SensitivityScenarios.RawOnly);
        Assert.Equal(0.055m, d.CurrentTotalCosts);
        Assert.Equal(0.037m, d.CostReductionNeeded);
        Assert.Equal("RawPositiveNetNegative", d.Classification);
    }

    [Fact]
    public void Config_binds_fee_slippage_safety()
    {
        var o = new TradingBotOptions();
        Assert.Equal(0.001m, o.MultiOutcomeArbitrage.FeePerLeg);
        Assert.Equal(0.0005m, o.MultiOutcomeArbitrage.SlippageBufferPerLeg);
        Assert.Equal(0.001m, o.MultiOutcomeArbitrage.SafetyBufferPerGroup);
    }
}
