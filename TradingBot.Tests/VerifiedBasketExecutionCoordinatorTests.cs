using MsOptions = Microsoft.Extensions.Options.Options;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class VerifiedBasketExecutionCoordinatorTests
{
    private static VerifiedBasketExecutionCoordinator Build(ExecutionOptions? o = null)
        => new(MsOptions.Create(o ?? new ExecutionOptions { DuplicateCooldownMinutes = 60, PaperOnly = true, PreventDuplicateGroupPositions = true }));

    [Fact]
    public void PreTrade_Approves_ValidVerifiedBasket()
    {
        var c = Build();
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = BaseOpp();

        var res = c.Validate(opp, book);

        Assert.True(res.Approved);
        Assert.Equal("Approved", res.Reason);
    }

    [Fact]
    public void PreTrade_Rejects_InsufficientLiquidity()
    {
        var c = Build();
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = BaseOpp() with { Legs = new[] { new VerifiedMultiOutcomeOpportunityLeg("m1", "c1", "q1", "NO", "t1", 0.22m, 0.5m, "DirectNoAsk", 1m, 0.22m) } };

        var res = c.Validate(opp, book);

        Assert.False(res.Approved);
        Assert.Equal("InsufficientLiquidity", res.Reason);
    }


    [Fact]
    public void PreTrade_Rejects_MaxNotionalExceeded()
    {
        var c = Build(new ExecutionOptions { MaxNotionalPerBasket = 0.5m, DuplicateCooldownMinutes = 60, PaperOnly = true, PreventDuplicateGroupPositions = true });
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = BaseOpp();

        var res = c.Validate(opp, book);

        Assert.False(res.Approved);
        Assert.Equal("MaxNotionalExceeded", res.Reason);
    }
    [Fact]
    public void DuplicateOpenGroup_IsRejected()
    {
        var c = Build();
        var csv = Path.GetTempFileName();
        var book = new PaperPositionBook(csv);
        var opp = BaseOpp();

        var first = c.Validate(opp, book);
        Assert.True(first.Approved);
        var opened = c.OpenPaperPosition(opp, first, book, SuccessfulFill(first.Quantity));
        Assert.NotNull(opened);

        var second = c.Validate(opp with { Id = "opp-2" }, book);
        Assert.False(second.Approved);
        Assert.Equal("DuplicatePosition", second.Reason);
    }

    [Fact]
    public void Audit_Contains_Key_Stages()
    {
        var c = Build();
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = BaseOpp();

        c.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "Detected", "Ok", "VerifiedExecutable", opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, ""));
        c.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "PromotedToOpportunity", "Ok", "Actionable", opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, ""));
        var pre = c.Validate(opp, book);
        if (pre.Approved) c.OpenPaperPosition(opp, pre, book, SuccessfulFill(pre.Quantity));

        var events = c.ListAudit(200);
        Assert.Contains(events, e => e.Stage == "Detected");
        Assert.Contains(events, e => e.Stage == "PromotedToOpportunity");
        Assert.Contains(events, e => e.Stage == "PreTradeStarted");
    }


    private static FillSimulationResult SuccessfulFill(decimal qty)
    {
        var legs = new[]
        {
            new LegFillSimulation("m1", "c1", "q1", "t1", "BUY", "NO", qty, 0.22m, qty, qty, 0.22m, 0.22m * qty, FillSimulationStatus.FullyFillable, null, DateTime.UtcNow, false),
            new LegFillSimulation("m2", "c2", "q2", "t2", "BUY", "NO", qty, 0.33m, qty, qty, 0.33m, 0.33m * qty, FillSimulationStatus.FullyFillable, null, DateTime.UtcNow, false),
            new LegFillSimulation("m3", "c3", "q3", "t3", "BUY", "NO", qty, 0.40m, qty, qty, 0.40m, 0.40m * qty, FillSimulationStatus.FullyFillable, null, DateTime.UtcNow, false),
        };
        return new FillSimulationResult("sim", "plan", "winner:2026 colombian presidential election|kind:person", "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", DateTime.UtcNow, FillSimulationStatus.FullyFillable, 3, 3, 0, 0, qty, qty, qty, 0m, 0.95m * qty, 0.95m * qty, 0.0035m * qty, 0m, 1.05m, 0.0035m, 1.05m, 0.0035m, 0.0035m * qty, 0.0035m * qty, 0.95m, 0.95m, 0m, 0m, 1.0465m, "Conservative", false, true, [], [], legs);
    }

    private static VerifiedMultiOutcomeOpportunity BaseOpp()
    {
        var legs = new[]
        {
            new VerifiedMultiOutcomeOpportunityLeg("m1", "c1", "q1", "NO", "t1", 0.22m, 5m, "DirectNoAsk", 1m, 0.22m),
            new VerifiedMultiOutcomeOpportunityLeg("m2", "c2", "q2", "NO", "t2", 0.33m, 5m, "DirectNoAsk", 1m, 0.33m),
            new VerifiedMultiOutcomeOpportunityLeg("m3", "c3", "q3", "NO", "t3", 0.40m, 5m, "DirectNoAsk", 1m, 0.40m),
        };

        return new VerifiedMultiOutcomeOpportunity(
            "opp-1",
            "BUY_ALL_NO_MUTUALLY_EXCLUSIVE",
            "winner:2026 colombian presidential election|kind:person",
            "Colombia 2026",
            "Verified",
            3,
            2m,
            0.95m,
            1.05m,
            0.0035m,
            "Conservative",
            1m,
            0.0035m,
            100m,
            0.95m,
            "PaperExecutable",
            legs);
    }
}
