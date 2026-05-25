using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class MultiOutcomeEnhancementsTests
{
    [Fact]
    public void Allowlisted_group_requires_market_match()
    {
        var v = new MutuallyExclusiveGroupValidator(new MultiOutcomeArbitrageOptions());
        var ok = v.Validate("winner:2026 fifa world cup|kind:generic", "generic", [new BasketArbLeg("m1","c1","Will France win the 2026 FIFA World Cup?","NO",0.4m,10)]);
        Assert.False(ok.IsValidForNoBasketArbitrage);
        Assert.Equal("VerifiedGroupMarketMismatch", ok.RejectionReason);
    }

    [Fact]
    public void Unverified_group_rejected()
    {
        var v = new MutuallyExclusiveGroupValidator(new MultiOutcomeArbitrageOptions());
        var r = v.Validate("winner:2026 nba finals|kind:generic", "generic", [new BasketArbLeg("m","c","Will team win NBA finals?","NO",0.4m,10)]);
        Assert.Equal("UnverifiedGroup", r.RejectionReason);
    }

    [Fact]
    public void MultiOutcome_report_has_rejected_reason_summary()
    {
        var report = new MultiOutcomeGroupArbEngine.MultiOutcomeScanReport(5,0,0,0,5,0,0.1m,0m,0m,"","UnverifiedGroup",new Dictionary<string,int>{{"UnverifiedGroup",5}},new []{ new MultiOutcomeGroupArbEngine.RejectedSample("g1","UnverifiedGroup") });
        Assert.Equal(5, report.RejectedByReason["UnverifiedGroup"]);
    }
}
