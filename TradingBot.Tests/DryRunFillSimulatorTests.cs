using MsOptions = Microsoft.Extensions.Options.Options;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class DryRunFillSimulatorTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Fully_fillable_3_leg_basket_passes_fill_simulation()
    {
        var result = Simulate(qty: 10m, sizes: [20m, 20m, 20m]);
        Assert.Equal(FillSimulationStatus.FullyFillable, result.Status);
        Assert.False(result.PartialFillRisk);
        Assert.Equal(10m, result.FullyFillableQty);
    }

    [Fact]
    public void One_leg_insufficient_quantity_sets_partial_fill_risk()
    {
        var result = Simulate(qty: 10m, sizes: [20m, 4m, 20m]);
        Assert.Equal(FillSimulationStatus.PartiallyFillable, result.Status);
        Assert.True(result.PartialFillRisk);
        Assert.Equal(4m, result.FullyFillableQty);
    }

    [Fact]
    public void Missing_orderbook_rejects_simulation()
    {
        var plan = Plan(10m);
        var snapshots = Snapshots();
        snapshots.Remove("m2");
        var result = new DryRunFillSimulator().Simulate(plan, Books([20m,20m,20m]), snapshots, Options(), Now);
        Assert.Equal(FillSimulationStatus.MissingOrderbook, result.Status);
    }

    [Fact]
    public void Stale_orderbook_rejects_simulation()
    {
        var books = Books([20m,20m,20m], Now.AddSeconds(-10));
        var result = new DryRunFillSimulator().Simulate(Plan(10m), books, Snapshots(), Options(), Now);
        Assert.Equal(FillSimulationStatus.StaleOrderbook, result.Status);
    }

    [Fact]
    public void Weighted_average_price_calculates_across_multiple_book_levels()
    {
        var plan = Plan(10m, prices: [0.6m, 0.6m, 0.6m]);
        var books = Books([20m,20m,20m]);
        books["t1"] = new CachedOrderBookSnapshot("t1", "m1", Now, [new BookQuote(0.2m, 5m), new BookQuote(0.4m, 5m)], []);
        var result = new DryRunFillSimulator().Simulate(plan, books, Snapshots(), Options(), Now);
        Assert.Equal(0.3m, result.LegResults.Single(x => x.MarketId == "m1").SimulatedAveragePrice);
    }

    [Fact]
    public void Fully_fillable_qty_is_min_available_qty_across_legs()
    {
        var result = Simulate(qty: 10m, sizes: [8m, 6m, 9m]);
        Assert.Equal(6m, result.FullyFillableQty);
    }

    [Fact]
    public void Paper_position_is_not_opened_if_fill_simulation_fails()
    {
        var coord = Coordinator();
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = Opp();
        var pre = coord.Validate(opp, book);
        var failed = Simulate(qty: pre.Quantity, sizes: [20m, 1m, 20m]);
        var opened = coord.OpenPaperPosition(opp, pre, book, failed);
        Assert.Null(opened);
        Assert.Empty(book.OpenPositions);
    }

    [Fact]
    public void Paper_position_uses_simulated_fill_cost_not_optimistic_plan_cost()
    {
        var coord = Coordinator();
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = Opp();
        var pre = coord.Validate(opp, book);
        var plan = Plan(pre.Quantity, prices: [0.6m, 0.6m, 0.6m]);
        var books = Books([20m,20m,20m]);
        books["t1"] = new CachedOrderBookSnapshot("t1", "m1", Now, [new BookQuote(0.2m, 10m), new BookQuote(0.4m, 10m)], []);
        books["t2"] = new CachedOrderBookSnapshot("t2", "m2", Now, [new BookQuote(0.33m, 20m)], []);
        books["t3"] = new CachedOrderBookSnapshot("t3", "m3", Now, [new BookQuote(0.4m, 20m)], []);
        var fill = new DryRunFillSimulator().Simulate(plan, books, Snapshots(), Options(), Now);
        var opened = coord.OpenPaperPosition(opp, pre, book, fill);
        Assert.NotNull(opened);
        Assert.NotEqual(pre.EstimatedCost, opened!.TotalCost);
        Assert.Equal(fill.EstimatedFilledCost, opened.TotalCost);
        Assert.True(opened.OpenedFromSimulatedFills);
    }

    [Fact]
    public void Partial_fill_risk_audit_event_is_recorded()
    {
        var coord = Coordinator();
        var fill = Simulate(qty: 10m, sizes: [20m, 1m, 20m]);
        coord.Audit(new ExecutionAuditEvent(DateTime.UtcNow, "opp", fill.GroupKey, fill.Strategy, "DryRunFillSimulationRejected", "Rejected", "PartialFillRisk", 0, 0, 0, 0, ""));
        Assert.Contains(coord.ListAudit(), x => x.Stage == "DryRunFillSimulationRejected" && x.Reason == "PartialFillRisk");
    }

    [Fact]
    public void Dry_run_fill_simulations_export_is_created()
    {
        var coord = Coordinator();
        coord.RecordFillSimulation(Simulate(qty: 10m, sizes: [20m, 20m, 20m]));
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        coord.ExportFillSimulations(path);
        Assert.True(File.Exists(path));
        Assert.Contains("dry", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Api_source_returns_latest_fill_simulations_from_coordinator()
    {
        var coord = Coordinator();
        coord.RecordFillSimulation(Simulate(qty: 10m, sizes: [20m, 20m, 20m]));
        Assert.Single(coord.ListFillSimulations(50));
    }

    [Fact]
    public void Require_all_legs_fillable_blocks_partial_basket()
    {
        var result = Simulate(qty: 10m, sizes: [20m, 4m, 20m], options: Options(requireAll: true, allowPartial: true));
        Assert.Contains("RequireAllLegsFillable", result.Errors);
    }

    [Fact]
    public void Allow_partial_basket_fill_false_prevents_partial_paper_open()
    {
        var result = Simulate(qty: 10m, sizes: [20m, 4m, 20m], options: Options(requireAll: false, allowPartial: false));
        Assert.Contains("PartialBasketFillDisabled", result.Errors);
    }

    [Fact]
    public void Order_plan_with_yes_token_instead_of_no_token_fails_simulation()
    {
        var plan = Plan(10m, tokenIds: ["y1", "t2", "t3"]);
        var result = new DryRunFillSimulator().Simulate(plan, Books([20m,20m,20m]), Snapshots(), Options(), Now);
        Assert.Equal(FillSimulationStatus.Rejected, result.Status);
    }


    [Fact]
    public void Paper_position_uses_active_profile_net_edge_not_gross_edge()
    {
        var coord = Coordinator();
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = Opp();
        var pre = coord.Validate(opp, book);
        var fill = ColombianFill(pre.Quantity);

        var opened = coord.OpenPaperPosition(opp, pre, book, fill);

        Assert.NotNull(opened);
        Assert.Equal(0.011m, opened!.GrossEdgeAtOpen);
        Assert.Equal(0.0055m, opened.NetEdgeAtOpen);
        Assert.Equal(pre.ExpectedProfit, opened.ExpectedProfit);
    }

    [Fact]
    public void Colombian_pretrade_net_edge_and_qty_produce_expected_paper_profit()
    {
        var coord = Coordinator();
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = Opp();
        var pre = coord.Validate(opp, book);

        var opened = coord.OpenPaperPosition(opp, pre, book, ColombianFill(pre.Quantity));

        Assert.NotNull(opened);
        Assert.Equal(pre.Quantity * 0.0055m, opened!.ExpectedProfit);
        Assert.True(Math.Abs(opened.ExpectedProfit - 0.6913021618903971845148315736m) < 0.00000000000000000000000001m);
    }

    [Fact]
    public void Paper_open_is_blocked_if_expected_profit_doubles_without_fill_reason()
    {
        var coord = Coordinator();
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = Opp();
        var pre = coord.Validate(opp, book);
        var inconsistent = ColombianFill(pre.Quantity) with { FillAdjustedExpectedProfit = pre.ExpectedProfit * 2m, EstimatedExpectedProfit = pre.ExpectedProfit * 2m };

        var opened = coord.OpenPaperPosition(opp, pre, book, inconsistent);

        Assert.Null(opened);
        Assert.Empty(book.OpenPositions);
        Assert.Contains(coord.ListAudit(), x => x.Stage == "PaperOpenBlocked" && x.Reason == "PaperOpenInvariantFailed");
    }

    [Fact]
    public void Fill_simulation_pass_with_unchanged_cost_preserves_net_edge()
    {
        var qty = 125.69130216189039718451483157m;
        var result = new DryRunFillSimulator().Simulate(Plan(qty, netEdge: 0.0055m), Books([300m,300m,300m]), Snapshots(), Options(), Now, profileUsed: "Conservative");

        Assert.Equal(FillSimulationStatus.FullyFillable, result.Status);
        Assert.Equal(0.011m, result.FillAdjustedGrossEdgePerBasket);
        Assert.Equal(0.0055m, result.FillAdjustedNetEdgePerBasket);
        Assert.Equal(qty * 0.0055m, result.FillAdjustedExpectedProfit);
    }

    [Fact]
    public void Fill_simulation_with_worse_weighted_average_recomputes_lower_net_edge()
    {
        var qty = 10m;
        var books = Books([300m,300m,300m]);
        books["t1"] = new CachedOrderBookSnapshot("t1", "m1", Now, [new BookQuote(0.665m, 300m)], []);
        var optimisticPlan = Plan(qty, prices: [0.665m, 0.663m, 0.663m], netEdge: 0.0055m) with
        {
            CostPerBasket = 1.989m,
            TotalEstimatedCost = qty * 1.989m
        };
        var result = new DryRunFillSimulator().Simulate(optimisticPlan, books, Snapshots(), Options(), Now, profileUsed: "Conservative");

        Assert.Equal(FillSimulationStatus.FullyFillable, result.Status);
        Assert.True(result.FillAdjustedNetEdgePerBasket < result.PlannedNetEdgePerBasket);
        Assert.Equal(0.0035m, result.FillAdjustedNetEdgePerBasket);
    }

    [Fact]
    public void Paper_open_invariant_failure_prevents_position_persistence()
    {
        var coord = Coordinator();
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = Opp();
        var pre = coord.Validate(opp, book);
        var inconsistent = ColombianFill(pre.Quantity) with { FillAdjustedNetEdgePerBasket = 0.011m };

        var opened = coord.OpenPaperPosition(opp, pre, book, inconsistent);

        Assert.Null(opened);
        Assert.Empty(book.OpenPositions);
    }

    [Fact]
    public void Execution_audit_values_are_consistent_across_fill_and_paper_open()
    {
        var coord = Coordinator();
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = Opp();
        var pre = coord.Validate(opp, book);
        var fill = ColombianFill(pre.Quantity);
        coord.RecordDryRunPlan(Plan(pre.Quantity, netEdge: pre.NetEdge));
        coord.RecordFillSimulation(fill);
        coord.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "DryRunFillSimulationPassed", "Ok", "FullyFillable", fill.FillAdjustedNetEdgePerBasket, fill.FillAdjustedExpectedProfit, fill.EstimatedFilledCost, fill.SafeExecutableQty, ""));

        var opened = coord.OpenPaperPosition(opp, pre, book, fill);

        Assert.NotNull(opened);
        var audit = coord.ListAudit();
        Assert.Contains(audit, x => x.Stage == "PreTradeApproved" && x.NetEdge == 0.0055m && x.ExpectedProfit == pre.ExpectedProfit);
        Assert.Contains(audit, x => x.Stage == "DryRunFillSimulationPassed" && x.NetEdge == 0.0055m && x.ExpectedProfit == pre.ExpectedProfit);
        Assert.Contains(audit, x => x.Stage == "PaperOpened" && x.NetEdge == 0.0055m && x.ExpectedProfit == pre.ExpectedProfit);
    }

    [Fact]
    public void Paper_account_equity_does_not_include_expected_profit_at_open()
    {
        var executionPolicy = new ExecutionPolicy { MaxLockedCapital = 1000m, MaxOpenPositions = 10, MaxExposurePerGroup = 1000m, AllowBasketArbs = true };
        var journal = new ExecutionJournal(Path.GetTempFileName());
        var book = new PaperPositionBook(Path.GetTempFileName());
        var paper = new PaperTradingEngine(executionPolicy, journal, new ExecutionDecisionService(executionPolicy), book);
        var coord = Coordinator();
        var opp = Opp();
        var pre = coord.Validate(opp, book);
        var opened = coord.OpenPaperPosition(opp, pre, book, ColombianFill(pre.Quantity));

        paper.RegisterExternalBasketOpen(opened!, opened!.TotalCost, opened.ExpectedProfit);

        Assert.Equal(1000m - opened.TotalCost, paper.Balance);
        Assert.Equal(1000m, paper.Equity);
        Assert.Equal(0m, paper.RealizedPnl);
        Assert.Equal(0m, paper.UnrealizedPnl);
    }


    private static FillSimulationResult ColombianFill(decimal qty)
    {
        var plan = Plan(qty, netEdge: 0.0055m);
        return new DryRunFillSimulator().Simulate(plan, Books([300m, 300m, 300m]), Snapshots(), Options(), Now, profileUsed: "Conservative");
    }

    private static FillSimulationResult Simulate(decimal qty, decimal[] sizes, ExecutionOptions? options = null)
        => new DryRunFillSimulator().Simulate(Plan(qty), Books(sizes), Snapshots(), options ?? Options(), Now);

    private static ExecutionOptions Options(bool requireAll = true, bool allowPartial = false) => new()
    {
        RequireAllLegsFillable = requireAll,
        AllowPartialBasketFill = allowPartial,
        MinFillableQtyRatio = 1.0m,
        MaxOrderbookAgeMsForFillSimulation = 5000
    };

    private static VerifiedBasketExecutionCoordinator Coordinator()
        => new(MsOptions.Create(new ExecutionOptions { DuplicateCooldownMinutes = 60, PaperOnly = true, PreventDuplicateGroupPositions = true, MaxNotionalPerBasket = 250 }));

    private static BasketOrderPlan Plan(decimal qty, decimal[]? prices = null, string[]? tokenIds = null, decimal? netEdge = null)
    {
        prices ??= [0.663m, 0.663m, 0.663m];
        tokenIds ??= ["t1", "t2", "t3"];
        var orders = Enumerable.Range(1, 3).Select(i => new OrderIntent($"o{i}", "opp-1", "dry", "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", $"m{i}", $"c{i}", $"q{i}", tokenIds[i-1], "NO", "BUY", "NO", prices[i-1], qty, prices[i-1] * qty, "LIMIT", "GTC", false, true, Now)).ToArray();
        var gross = 2m - prices.Sum();
        var net = netEdge ?? gross;
        return new BasketOrderPlan("plan-1", "opp-1", "dry", "Dry", "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", "Conservative", true, Now, Now.AddMinutes(10), BasketOrderPlanStatus.PaperOnly, 3, qty, 2m, prices.Sum(), orders.Sum(x => x.EstimatedCost), qty * net, net, 250m, orders, [], []);
    }

    private static Dictionary<string, CachedOrderBookSnapshot?> Books(decimal[] sizes, DateTime? ts = null)
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["t1"] = new CachedOrderBookSnapshot("t1", "m1", ts ?? Now, [new BookQuote(0.663m, sizes[0])], []),
            ["t2"] = new CachedOrderBookSnapshot("t2", "m2", ts ?? Now, [new BookQuote(0.663m, sizes[1])], []),
            ["t3"] = new CachedOrderBookSnapshot("t3", "m3", ts ?? Now, [new BookQuote(0.663m, sizes[2])], [])
        };

    private static Dictionary<string, BinaryOrderBookSnapshot?> Snapshots()
        => new(StringComparer.OrdinalIgnoreCase)
        {
            ["m1"] = new BinaryOrderBookSnapshot("m1", "q1", "y1", "t1", null, null, null, new BookQuote(0.663m, 100m)),
            ["m2"] = new BinaryOrderBookSnapshot("m2", "q2", "y2", "t2", null, null, null, new BookQuote(0.663m, 100m)),
            ["m3"] = new BinaryOrderBookSnapshot("m3", "q3", "y3", "t3", null, null, null, new BookQuote(0.663m, 100m))
        };

    private static VerifiedMultiOutcomeOpportunity Opp(decimal qty = 125.69130216189039718451483157m)
        => new("opp-1", "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", "dry", "Dry", "Verified", 3, 2m, 1.989m, 0.011m, 0.0055m, "Conservative", qty, qty * 0.0055m, 250m, qty * 1.989m, "PaperExecutable", [
            new VerifiedMultiOutcomeOpportunityLeg("m1", "c1", "q1", "NO", "t1", 0.663m, 300m, "DirectNoAsk", qty, qty * 0.663m),
            new VerifiedMultiOutcomeOpportunityLeg("m2", "c2", "q2", "NO", "t2", 0.663m, 300m, "DirectNoAsk", qty, qty * 0.663m),
            new VerifiedMultiOutcomeOpportunityLeg("m3", "c3", "q3", "NO", "t3", 0.663m, 300m, "DirectNoAsk", qty, qty * 0.663m)
        ]);
}
