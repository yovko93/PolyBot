using TradingBot.Options;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class VerifiedBasketScreenerTests
{
    [Fact]
    public void Nba_sample_profile_edges_match_expected()
    {
        var o = new MultiOutcomeArbitrageOptions();
        var legs = new[] { 0.666m, 0.666m, 0.666m }.Select((v,i)=>new ResolvedNoAsk($"m{i}", null, v, 100m, "book", null, null, null, DateTime.UtcNow, false, null)).ToList();
        var r = VerifiedBasketScreener.Evaluate("winner:2026 nba finals|kind:generic", legs, o);
        Assert.Equal(-0.0035m, r.ProfileResults.First(x=>x.ProfileName=="Conservative").NetEdge);
        Assert.Equal(0.0005m, r.ProfileResults.First(x=>x.ProfileName=="PolymarketApprox").NetEdge);
        Assert.Equal(0.002m, r.ProfileResults.First(x=>x.ProfileName=="RawOnly").NetEdge);
    }

    [Fact]
    public void Fifa_sample_profile_edges_match_expected()
    {
        var o = new MultiOutcomeArbitrageOptions();
        var vals = Enumerable.Repeat(0.9717222222m, 36).ToArray(); vals[0] = 0.982m;
        var legs = vals.Select((v,i)=>new ResolvedNoAsk($"m{i}", null, v, 10m, "book", null, null, null, DateTime.UtcNow, false, null)).ToList();
        var r = VerifiedBasketScreener.Evaluate("winner:2026 fifa world cup|kind:generic", legs, o);
        Assert.Equal(-0.037m, Math.Round(r.ProfileResults.First(x=>x.ProfileName=="Conservative").NetEdge,3));
        Assert.Equal(-0.001m, Math.Round(r.ProfileResults.First(x=>x.ProfileName=="PolymarketApprox").NetEdge,3));
        Assert.Equal(0.018m, Math.Round(r.ProfileResults.First(x=>x.ProfileName=="RawOnly").NetEdge,3));
    }

    [Fact]
    public void Quantity_expected_profit_is_net_times_qty()
    {
        var o = new MultiOutcomeArbitrageOptions();
        var legs = new[] { 0.666m, 0.666m, 0.666m }.Select((v,i)=>new ResolvedNoAsk($"m{i}", null, v, 7m, "book", null, null, null, DateTime.UtcNow, false, null)).ToList();
        var r = VerifiedBasketScreener.Evaluate("g", legs, o);
        var q5 = r.QuantityResults.First(x=>x.Qty==5m);
        Assert.Equal(q5.NetEdgePerBasket * 5m, q5.ExpectedProfit);
    }

    [Fact]
    public void Near_executable_when_cost_reduction_small()
    {
        var o = new MultiOutcomeArbitrageOptions();
        var legs = new[] { 0.666m, 0.666m, 0.666m }.Select((v,i)=>new ResolvedNoAsk($"m{i}", null, v, 100m, "book", null, null, null, DateTime.UtcNow, false, null)).ToList();
        var r = VerifiedBasketScreener.Evaluate("g", legs, o);
        Assert.True(r.NearExecutable);
    }

    [Fact]
    public void Snapshot_tie_break_prefers_lower_cost_reduction_when_net_equal()
    {
        var o = new MultiOutcomeArbitrageOptions();
        var a = new[] { 0.666m, 0.666m, 0.666m }.Select((v,i)=>new ResolvedNoAsk($"a{i}", null, v, 100m, "book", null, null, null, DateTime.UtcNow, false, null)).ToList();
        var b = new[] { 0.6655m, 0.6665m, 0.666m }.Select((v,i)=>new ResolvedNoAsk($"b{i}", null, v, 100m, "book", null, null, null, DateTime.UtcNow, false, null)).ToList();
        var ra = VerifiedBasketScreener.Evaluate("A", a, o);
        var rb = VerifiedBasketScreener.Evaluate("B", b, o);
        var snap = VerifiedBasketScreener.BuildSnapshot("Conservative", new[] { ra, rb }, Array.Empty<string>());
        Assert.NotEmpty(snap.Ranking);
    }
}
