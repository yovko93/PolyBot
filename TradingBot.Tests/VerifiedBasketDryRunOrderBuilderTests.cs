using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class VerifiedBasketDryRunOrderBuilderTests
{
    [Fact]
    public void Creates_Three_Buy_No_Orders_For_Colombian_Basket()
    {
        var b = new VerifiedBasketDryRunOrderBuilder();
        var plan = b.Build(BaseOpp(), new VerifiedBasketPreTradeValidationResult(true, "Approved", 0.0045m, 125.6281407035175879m, 250m, 0.5653266331658291457m), new ExecutionOptions { MaxNotionalPerBasket = 250m });
        Assert.Equal(3, plan.Orders.Count);
        Assert.All(plan.Orders, o => Assert.Equal("BUY", o.Side));
        Assert.All(plan.Orders, o => Assert.Equal("NO", o.PositionSide));
        Assert.All(plan.Orders, o => Assert.True(o.DryRunOnly));
        Assert.All(plan.Orders, o => Assert.False(string.IsNullOrWhiteSpace(o.TokenId)));
        Assert.Equal(250m, decimal.Round(plan.TotalEstimatedCost, 6));
    }

    [Fact]
    public void Missing_NoTokenId_Rejects()
    {
        var opp = BaseOpp() with { Legs = BaseOpp().Legs.Select(x => x with { NoTokenId = "" }).ToArray() };
        var b = new VerifiedBasketDryRunOrderBuilder();
        var plan = b.Build(opp, new VerifiedBasketPreTradeValidationResult(true, "Approved", 0.0045m, 1m, 1.99m, 0.0045m), new ExecutionOptions());
        Assert.Equal(BasketOrderPlanStatus.Rejected, plan.Status);
        Assert.Contains("MissingNoTokenId", plan.ValidationErrors);
    }

    private static VerifiedMultiOutcomeOpportunity BaseOpp()
    {
        var legs = new[]
        {
            new VerifiedMultiOutcomeOpportunityLeg("m1", "c1", "q1", "NO", "t-no-1", 0.65m, 1327.75m, "DirectNoAsk", 0m, 0m),
            new VerifiedMultiOutcomeOpportunityLeg("m2", "c2", "q2", "NO", "t-no-2", 0.67m, 1327.75m, "DirectNoAsk", 0m, 0m),
            new VerifiedMultiOutcomeOpportunityLeg("m3", "c3", "q3", "NO", "t-no-3", 0.67m, 1327.75m, "DirectNoAsk", 0m, 0m),
        };
        return new VerifiedMultiOutcomeOpportunity("opp", "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", "winner:2026 colombian presidential election|kind:person", "Colombia", "Verified", 3, 2m, 1.99m, 0.01m, 0.0045m, "Conservative", 125.62m, 0.5653m, 250m, 250m, "PaperExecutable", legs);
    }
}
