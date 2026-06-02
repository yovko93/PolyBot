using System.Text.Json;
using MsOptions = Microsoft.Extensions.Options.Options;
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
        var plan = BuildPlan();

        Assert.Equal(BasketOrderPlanStatus.PaperOnly, plan.Status);
        Assert.Equal(3, plan.Orders.Count);
        Assert.Equal(3, plan.LegsCount);
        Assert.All(plan.Orders, o => Assert.Equal("BUY", o.Side));
        Assert.All(plan.Orders, o => Assert.Equal("NO", o.PositionSide));
        Assert.All(plan.Orders, o => Assert.True(o.DryRunOnly));
        Assert.All(plan.Orders, o => Assert.False(string.IsNullOrWhiteSpace(o.TokenId)));
        Assert.Equal(new[] { "t-no-1", "t-no-2", "t-no-3" }, plan.Orders.Select(o => o.TokenId).ToArray());
        Assert.Equal(250m, decimal.Round(plan.TotalEstimatedCost, 6));
    }

    [Fact]
    public void Each_Order_Uses_No_Token_Buy_No_Limit_And_Planned_Qty()
    {
        var pre = ColombianPreTrade();
        var plan = BuildPlan(preTrade: pre);

        Assert.All(plan.Orders, o =>
        {
            Assert.Equal("BUY", o.Side);
            Assert.Equal("NO", o.PositionSide);
            Assert.Equal("LIMIT", o.OrderType);
            Assert.Equal(pre.Quantity, o.Quantity);
            Assert.True(o.TokenId.StartsWith("t-no-", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(o.Price * o.Quantity, o.EstimatedCost);
        });
    }

    [Fact]
    public void Sum_Order_Costs_Total_Cost_MaxNotional_And_ExpectedProfit_Are_Consistent()
    {
        var pre = ColombianPreTrade();
        var plan = BuildPlan(preTrade: pre);

        Assert.Equal(plan.Orders.Sum(x => x.EstimatedCost), plan.TotalEstimatedCost);
        Assert.True(plan.TotalEstimatedCost <= 250m);
        Assert.Equal(pre.Quantity * pre.NetEdge, plan.ExpectedProfit);
    }

    [Fact]
    public void Missing_NoTokenId_Rejects()
    {
        var opp = BaseOpp() with { Legs = BaseOpp().Legs.Select(x => x with { NoTokenId = "" }).ToArray() };
        var plan = BuildPlan(opp);

        Assert.Equal(BasketOrderPlanStatus.Rejected, plan.Status);
        Assert.Contains("MissingNoTokenId", plan.ValidationErrors);
    }

    [Fact]
    public void Invalid_Price_Rejects()
    {
        var opp = BaseOpp() with { Legs = BaseOpp().Legs.Select((x, i) => i == 0 ? x with { NoAsk = 1.01m } : x).ToArray() };
        var plan = BuildPlan(opp, new VerifiedBasketPreTradeValidationResult(true, "Approved", 0.0045m, 1m, 2.35m, 0.0045m));

        Assert.Equal(BasketOrderPlanStatus.Rejected, plan.Status);
        Assert.Contains("InvalidPrice", plan.ValidationErrors);
    }

    [Fact]
    public void Cost_Mismatch_Rejects()
    {
        var plan = BuildPlan(preTrade: ColombianPreTrade() with { EstimatedCost = 249m });

        Assert.Equal(BasketOrderPlanStatus.Rejected, plan.Status);
        Assert.Contains("CostMismatch", plan.ValidationErrors);
    }

    [Fact]
    public async Task PaperOnly_Blocks_Live_Executor()
    {
        var executor = new DisabledExchangeOrderExecutor();
        var plan = BuildPlan();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => executor.SubmitAsync(plan.Orders[0], new ExecutionOptions { PaperOnly = true, EnableLiveOrderSubmission = false }));
        Assert.Equal("LiveExecutionDisabled", ex.Message);
    }

    [Fact]
    public void Dry_Run_Plan_Is_Created_Before_Paper_Open_And_Audit_Contains_Event()
    {
        var coord = new VerifiedBasketExecutionCoordinator(MsOptions.Create(new ExecutionOptions { PaperOnly = true, MaxNotionalPerBasket = 250m, DuplicateCooldownMinutes = 60 }));
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = BaseOpp();
        var pre = coord.Validate(opp, book);
        var plan = BuildPlan(opp, pre);
        coord.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "DryRunOrderBuildStarted", "Started", "DryRunBuildStarted", pre.NetEdge, pre.ExpectedProfit, pre.EstimatedCost, pre.Quantity, ""));
        coord.RecordDryRunPlan(plan);
        coord.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "DryRunOrderPlanCreated", "Ok", "DryRunOnly", pre.NetEdge, pre.ExpectedProfit, pre.EstimatedCost, pre.Quantity, $"Orders={plan.Orders.Count}"));

        var opened = coord.OpenPaperPosition(opp, pre, book, plan, ColombianFill(plan, pre));

        Assert.NotNull(opened);
        var stages = coord.ListAudit(200).Select(x => x.Stage).ToArray();
        Assert.Contains("DryRunOrderPlanCreated", stages);
        Assert.True(Array.IndexOf(stages, "DryRunOrderPlanCreated") < Array.IndexOf(stages, "PaperOpened"));
    }

    [Fact]
    public void Api_Source_Returns_Latest_Dry_Run_Order_Plan()
    {
        var coord = new VerifiedBasketExecutionCoordinator(MsOptions.Create(new ExecutionOptions { PaperOnly = true, MaxNotionalPerBasket = 250m }));
        var plan = BuildPlan();
        coord.RecordDryRunPlan(plan);

        var response = coord.ListDryRunPlanSummaries(50);
        var json = JsonSerializer.Serialize(response);

        Assert.Single(response);
        Assert.Contains("dryRunOnly", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("winner:2026 colombian presidential election|kind:person", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Paper_Open_Without_Valid_Dry_Run_Plan_Is_Blocked()
    {
        var coord = new VerifiedBasketExecutionCoordinator(MsOptions.Create(new ExecutionOptions { PaperOnly = true, MaxNotionalPerBasket = 250m, DuplicateCooldownMinutes = 60 }));
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = BaseOpp();
        var pre = coord.Validate(opp, book);

        var opened = coord.OpenPaperPosition(opp, pre, book, null, ColombianFill(BuildPlan(opp, pre), pre));

        Assert.Null(opened);
        Assert.Contains(coord.ListAudit(), x => x.Stage == "PaperOpenBlocked" && x.Reason == "DryRunOrderPlanMissing");
    }

    private static BasketOrderPlan BuildPlan(VerifiedMultiOutcomeOpportunity? opp = null, VerifiedBasketPreTradeValidationResult? preTrade = null, ExecutionOptions? cfg = null)
        => new VerifiedBasketDryRunOrderBuilder().Build(opp ?? BaseOpp(), preTrade ?? ColombianPreTrade(), cfg ?? new ExecutionOptions { MaxNotionalPerBasket = 250m, PaperOnly = true, EnableDryRunOrderBuilder = true, EnableLiveOrderSubmission = false });

    private static VerifiedBasketPreTradeValidationResult ColombianPreTrade()
    {
        var qty = 250m / 1.99m;
        var netEdge = 0.0045m;
        return new VerifiedBasketPreTradeValidationResult(true, "Approved", netEdge, qty, 250m, qty * netEdge);
    }

    private static FillSimulationResult ColombianFill(BasketOrderPlan plan, VerifiedBasketPreTradeValidationResult pre)
    {
        var legs = plan.Orders.Select(o => new LegFillSimulation(o.MarketId, o.ConditionId, o.Question, o.TokenId, o.Side, o.PositionSide, o.Quantity, o.Price, o.Quantity, o.Quantity, o.Price, o.EstimatedCost, FillSimulationStatus.FullyFillable, null, DateTime.UtcNow, false)).ToArray();
        return new FillSimulationResult("sim", plan.Id, plan.GroupKey, plan.Strategy, DateTime.UtcNow, FillSimulationStatus.FullyFillable, 3, 3, 0, 0, pre.Quantity, pre.Quantity, pre.Quantity, 0m, pre.EstimatedCost, pre.EstimatedCost, pre.ExpectedProfit, 0m, 0.01m, pre.NetEdge, 0.01m, pre.NetEdge, pre.ExpectedProfit, pre.ExpectedProfit, plan.CostPerBasket, plan.CostPerBasket, 0m, 0m, 0.0055m, "Conservative", false, true, [], [], legs);
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
