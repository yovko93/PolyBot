using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class MultiOutcomeValidationTests
{
    private static BasketArbLeg Leg(string q) => new("m","t",q,"NO",0.4m,10);

    [Fact]
    public void SpreadLinesRejected()
    {
        var v = new MutuallyExclusiveGroupValidator(new MultiOutcomeArbitrageOptions());
        var r = v.Validate("colon-event:spread|kind:generic","generic", [Leg("Spread: Knicks (-1.5)"),Leg("Spread: Knicks (-2.5)"),Leg("Spread: Knicks (-3.5)")]);
        Assert.False(r.IsValidForNoBasketArbitrage);
        Assert.Equal("AutoCandidateUnverified", r.RejectionReason);
    }

    [Fact]
    public void IndependentMatchesRejected()
    {
        var v = new MutuallyExclusiveGroupValidator(new MultiOutcomeArbitrageOptions());
        var r = v.Validate("colon-event:t20 blast|kind:generic","generic", [Leg("T20 Blast: Kent vs Sussex"),Leg("T20 Blast: Lancashire vs Nottinghamshire")]);
        Assert.False(r.IsValidForNoBasketArbitrage);
    }
}
