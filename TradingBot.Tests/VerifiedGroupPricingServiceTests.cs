using TradingBot.Models;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class VerifiedGroupPricingServiceTests
{
    [Fact]
    public void Direct_no_ask_is_used()
    {
        var m = new Market { id = "m1", conditionId = "c1", outcomes = new() { "Yes", "No" }, clobTokenIds = new() { "y", "n" } };
        var s = new BinaryOrderBookSnapshot("m1", "q", "y", "n", null, null, null, new BookQuote(0.44m, 100));
        var r = VerifiedGroupPricingService.ResolveNoAsk(m, s, DateTime.UtcNow, 5000);
        Assert.Equal(0.44m, r.NoAsk);
        Assert.Equal("DirectNoAsk", r.Source);
    }

    [Fact]
    public void Derived_no_ask_from_yes_bid()
    {
        var m = new Market { id = "m1", conditionId = "c1", outcomes = new() { "Yes", "No" }, clobTokenIds = new() { "y", "n" } };
        var s = new BinaryOrderBookSnapshot("m1", "q", "y", "n", new BookQuote(0.31m, 55), null, null, null);
        var r = VerifiedGroupPricingService.ResolveNoAsk(m, s, DateTime.UtcNow, 5000);
        Assert.Equal(0.69m, r.NoAsk);
        Assert.Equal(55, r.NoAskQuantity);
        Assert.Equal("DerivedFromYesBid", r.Source);
    }

    [Fact]
    public void Missing_no_token_id_returns_specific_reason()
    {
        var m = new Market { id = "m1", conditionId = "c1", outcomes = new() { "Yes" }, clobTokenIds = new() { "y" } };
        var r = VerifiedGroupPricingService.ResolveNoAsk(m, null, DateTime.UtcNow, 5000);
        Assert.Equal("MissingNoTokenId", r.FailureReason);
    }

    [Fact]
    public void Missing_yes_bid_and_no_ask_returns_missing_yes_bid_for_derived()
    {
        var m = new Market { id = "m1", conditionId = "c1", outcomes = new() { "Yes", "No" }, clobTokenIds = new() { "y", "n" } };
        var s = new BinaryOrderBookSnapshot("m1", "q", "y", "n", null, null, null, null);
        var r = VerifiedGroupPricingService.ResolveNoAsk(m, s, DateTime.UtcNow, 5000);
        Assert.Equal("MissingYesBidForDerivedNoAsk", r.FailureReason);
    }
}
