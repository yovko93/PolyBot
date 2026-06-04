using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class SingleMarketSafetyTests
{
    [Fact]
    public async Task Single_market_edge_detected_does_not_paper_open_before_stability_and_requires_three_scans()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions { MaxSingleMarketOpportunities = 200, MaxSingleMarketExecutions = 100 });
        var paper = NewPaper();
        var engine = NewEngine(Book(), state);
        var sem = new SemaphoreSlim(4);
        await engine.ScanAsync(new List<Market> { Market() }, paper, sem);
        Assert.Empty(state.SingleMarketExecutions());
        Assert.Contains(state.SingleMarketOpportunities(), x => x.State == SingleMarketArbState.EdgePending);
        await engine.ScanAsync(new List<Market> { Market() }, paper, sem);
        Assert.Empty(state.SingleMarketExecutions());
        await engine.ScanAsync(new List<Market> { Market() }, paper, sem);
        Assert.Contains(state.SingleMarketOpportunities(), x => x.State == SingleMarketArbState.EdgeStable);
        Assert.Empty(state.SingleMarketExecutions());
        await engine.ScanAsync(new List<Market> { Market() }, paper, sem);
        await engine.ScanAsync(new List<Market> { Market() }, paper, sem);
        Assert.Single(state.SingleMarketExecutions());
        Assert.Contains(state.SingleMarketOpportunities(), x => x.State == SingleMarketArbState.PaperOpened);
    }

    [Fact]
    public void Data_quality_rejects_suspicious_sum_missing_asks_and_same_token()
    {
        var v = new SingleMarketDataQualityValidator(new SingleMarketArbOptions());
        var m = Market();
        Assert.Equal("SuspiciousYesNoAskSum", v.Validate(m, Book(0.39m, 0.43m), DateTime.UtcNow).Reason);
        Assert.Equal("MissingYesAsk", v.Validate(m, Book(null, 0.95m), DateTime.UtcNow).Reason);
        Assert.Equal("MissingNoAsk", v.Validate(m, Book(0.05m, null), DateTime.UtcNow).Reason);
        Assert.Equal("SameYesNoTokenId", v.Validate(m, Book(0.05m, 0.95m) with { NoTokenId = "yes-token" }, DateTime.UtcNow).Reason);
    }

    [Fact]
    public void Fill_simulation_passes_full_depth_rejects_partial_and_cost_is_used()
    {
        var sim = new SingleMarketFillSimulator();
        var pass = sim.Simulate(Book(0.235m, 0.733m, yesSize: 200m, noSize: 200m), 75.38m, 0.001m, 0.001m);
        Assert.True(pass.Passed);
        Assert.Equal(75.38m * 0.970m, pass.SimulatedCost);
        var partial = sim.Simulate(Book(0.235m, 0.733m, yesSize: 10m, noSize: 200m), 75.38m, 0.001m, 0.001m);
        Assert.False(partial.Passed);
        Assert.Equal("PartialFillRisk", partial.Reason);
    }

    [Fact]
    public async Task Duplicate_cooldown_and_risk_caps_are_enforced()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions { MaxSingleMarketOpportunities = 200, MaxSingleMarketExecutions = 100 });
        var opts = Options();
        opts.MaxOpenSingleMarketPositions = 1;
        var paper = NewPaper();
        var engine = NewEngine(Book(), state, opts);
        var sem = new SemaphoreSlim(4);
        for (var i = 0; i < 5; i++) await engine.ScanAsync(new List<Market> { Market() }, paper, sem);
        Assert.Single(state.SingleMarketExecutions());
        for (var i = 0; i < 5; i++) await engine.ScanAsync(new List<Market> { Market() }, paper, sem);
        Assert.Contains(state.SingleMarketOpportunities(), x => x.Reason is "DuplicateOpenPosition" or "CooldownActive");
    }

    [Fact]
    public void Expected_profit_does_not_affect_equity_at_open_and_runtime_is_bounded()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions { MaxSingleMarketOpportunities = 2, MaxSingleMarketExecutions = 1 });
        for (var i = 0; i < 5; i++) state.AddSingleMarketOpportunity(new SingleMarketArbOpportunityDto($"o{i}", DateTime.UtcNow, "m", "c", "q", "BUY_YES_AND_BUY_NO", SingleMarketArbState.EdgePending, 0.1m, 0.89m, 0.99m, 0.008m, 1m, 10m, 9.9m, "Passed", "NotRun", "NotOpened", null, 1, 0, true));
        for (var i = 0; i < 5; i++) state.AddSingleMarketExecution(new SingleMarketPaperExecutionDto($"e{i}", DateTime.UtcNow, "m", "q", "BUY_YES_AND_BUY_NO", 10m, 0.1m, 0.89m, 9.9m, 0.008m, 1m, 990m, 10m, 1000m, "Opened", true));
        Assert.Equal(2, state.SingleMarketOpportunities().Length);
        Assert.Single(state.SingleMarketExecutions());
        var paper = NewPaper();
        var equity = paper.Equity;
        var ok = paper.RecordArbitrage(new ArbOpportunity(new ArbLeg("m", "q", "YES", 0.1m, 100m), new ArbLeg("m", "q", "NO", 0.89m, 100m), 50m, 0.99m, 0.01m, 0.5m, 1.0, "SingleMarketBuyBoth", "BUY_YES_AND_BUY_NO"));
        Assert.True(ok);
        Assert.Equal(equity, paper.Equity);
    }

    [Fact]
    public void Log_literal_no_longer_contains_dry_run_live_order_plan_created()
    {
        var text = File.ReadAllText(Path.Combine("..", "..", "..", "..", "TradingBot", "Services", "OpportunityMonitor.cs"));
        Assert.DoesNotContain("[DRY-RUN LIVE ORDER PLAN CREATED]", text);
        Assert.Contains("[DRY_RUN_ORDER_PLAN_CREATED]", text);
    }

    private static SingleMarketOrderBookArbEngine NewEngine(BinaryOrderBookSnapshot book, BotRuntimeState state, SingleMarketArbOptions? opts = null) => new(new FakeProvider(book), 0.005m, 0.001m, 0.001m, null, new ExecutionSizingService(new ExecutionPolicy { MaxNotionalPerTrade = 100m, MinNotionalPerTrade = 25m }), opts ?? Options(), state, null);
    private static SingleMarketArbOptions Options() => new() { RequiredConsecutiveEdgeScans = 3, RequiredConsecutiveExecutionReadyScans = 3, MinEdgePerShare = 0.005m, MinExpectedProfit = 0.50m, MinNotional = 25m, MaxNotionalPerTrade = 100m, MaxOpenSingleMarketPositions = 3, MaxTotalSingleMarketExposure = 300m, MaxPositionsPerCycle = 1, CooldownSecondsPerMarket = 300 };
    private static PaperTradingEngine NewPaper() => new(new ExecutionPolicy { MaxNotionalPerTrade = 100m, MinNotionalPerTrade = 25m, MaxOpenPositions = 100, MaxLockedCapital = 1000m, MaxExposurePerGroup = 300m }, null, null, new PaperPositionBook(Path.GetTempFileName()));
    private static Market Market() => new() { id = "m1", question = "Will X win?", conditionId = "c1", outcomes = new() { "Yes", "No" }, clobTokenIds = new() { "yes-token", "no-token" } };
    private static BinaryOrderBookSnapshot Book(decimal? yes = 0.235m, decimal? no = 0.733m, decimal yesSize = 200m, decimal noSize = 200m) => new("m1", "Will X win?", "yes-token", "no-token", null, yes.HasValue ? new BookQuote(yes.Value, yesSize) : null, null, no.HasValue ? new BookQuote(no.Value, noSize) : null, DateTime.UtcNow);
    private sealed class FakeProvider(BinaryOrderBookSnapshot book) : IOrderBookProvider { public Task<BinaryOrderBookSnapshot?> GetBinarySnapshotAsync(Market market, CancellationToken ct = default) => Task.FromResult<BinaryOrderBookSnapshot?>(book with { TimestampUtc = DateTime.UtcNow }); }
}
