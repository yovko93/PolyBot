using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class VerifiedMultiOutcomeGroupResolverTests
{
    [Fact]
    public void Resolves_against_full_discovered_pool()
    {
        var markets = Enumerable.Range(1, 42).Select(i => new Market { id = $"m{i}", conditionId = $"c{i}", question = "Will Brazil win the 2026 FIFA World Cup?" }).ToDictionary(x => x.id);
        var allow = new VerifiedMultiOutcomeGroupConfig(true, "winner:2026 fifa world cup|kind:generic", "wc", markets.Keys.ToList(), new List<string>(), 42, "Verified");
        var r = new VerifiedMultiOutcomeGroupResolver().ResolveVerifiedGroups(new[] { allow }, markets, new MultiOutcomeArbitrageOptions()).Single();
        Assert.Equal("VerifiedGroupResolved", r.ValidationStatus);
        Assert.Equal(42, r.ResolvedMarkets.Count);
    }

    [Fact]
    public void Missing_in_full_pool_is_mismatch()
    {
        var markets = Enumerable.Range(1, 41).Select(i => new Market { id = $"m{i}", conditionId = $"c{i}", question = "Will Brazil win the 2026 FIFA World Cup?" }).ToDictionary(x => x.id);
        var allow = new VerifiedMultiOutcomeGroupConfig(true, "winner:2026 fifa world cup|kind:generic", "wc", Enumerable.Range(1, 42).Select(i => $"m{i}").ToList(), new List<string>(), 42, "Verified");
        var r = new VerifiedMultiOutcomeGroupResolver().ResolveVerifiedGroups(new[] { allow }, markets, new MultiOutcomeArbitrageOptions()).Single();
        Assert.Equal("VerifiedGroupMarketMismatch", r.RejectionReason);
    }
}
