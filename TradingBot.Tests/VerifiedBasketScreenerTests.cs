using TradingBot.Options;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class VerifiedBasketScreenerTests
{
    private static List<ResolvedNoAsk> FifaLegs()
    {
        var vals = Enumerable.Repeat(0.9717222222m, 36).ToArray();
        vals[0] = 0.982m; // tune sum ~=34.982
        return vals.Select((v,i)=>new ResolvedNoAsk($"m{i}", null, v, 10m, "book", null, null, null, DateTime.UtcNow, false, null)).ToList();
    }

    [Fact]
    public void Cost_profiles_compute_expected_edges()
    {
        var o = new MultiOutcomeArbitrageOptions();
        var legs = FifaLegs();
        var r = VerifiedBasketScreener.Evaluate("winner:2026 fifa world cup|kind:generic", legs, o);
        Assert.Equal("Fees", r.DominantCost);
        Assert.Equal("RawPositiveNetNegative", r.Classification);
        Assert.Equal(-0.037m, Math.Round(r.ProfileResults.First(x=>x.ProfileName=="Conservative").NetEdge,3));
        Assert.Equal(0.018m, Math.Round(r.ProfileResults.First(x=>x.ProfileName=="RawOnly").NetEdge,3));
        Assert.Equal(0.017m, Math.Round(r.ProfileResults.First(x=>x.ProfileName=="PaperZeroFees").NetEdge,3));
    }
}
