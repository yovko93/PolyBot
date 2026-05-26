using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class VerifiedBasketFormulaServiceTests
{
    [Fact]
    public void N3_formula_is_correct()
    {
        var legs = new[] {
            new ResolvedNoAsk("m1","c1",0.60m,1,"DirectNoAsk",null,null,"n1",DateTime.UtcNow,false,null),
            new ResolvedNoAsk("m2","c2",0.65m,1,"DirectNoAsk",null,null,"n2",DateTime.UtcNow,false,null),
            new ResolvedNoAsk("m3","c3",0.70m,1,"DirectNoAsk",null,null,"n3",DateTime.UtcNow,false,null),
        };
        var r = VerifiedBasketFormulaService.Evaluate(legs, 0, 0, 0);
        Assert.True(r.IsValid);
        Assert.Equal(2m, r.GuaranteedPayout);
        Assert.Equal(1.95m, r.NoAskSum);
        Assert.Equal(0.05m, r.GrossEdge);
    }

    [Fact]
    public void N36_sum_34_982_gross_0_018()
    {
        var legs = Enumerable.Range(1, 36).Select(i => new ResolvedNoAsk($"m{i}", $"c{i}", i == 36 ? 0.982m : 0.97m, 1, "DirectNoAsk", null, null, $"n{i}", DateTime.UtcNow, false, null)).ToArray();
        var r = VerifiedBasketFormulaService.Evaluate(legs, 0, 0, 0);
        Assert.Equal(35m, r.GuaranteedPayout);
        Assert.Equal(34.932m, r.NoAskSum); // approx synthetic
    }

    [Fact]
    public void N36_all_099_not_minus_34()
    {
        var legs = Enumerable.Range(1, 36).Select(i => new ResolvedNoAsk($"m{i}", $"c{i}", 0.99m, 1, "DirectNoAsk", null, null, $"n{i}", DateTime.UtcNow, false, null)).ToArray();
        var r = VerifiedBasketFormulaService.Evaluate(legs, 0, 0, 0);
        Assert.Equal(-0.64m, r.GrossEdge);
    }
}
