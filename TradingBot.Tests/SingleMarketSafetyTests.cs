using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

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


    [Fact]
    public async Task BestEdge_ignores_rejected_markets_and_uses_best_valid_net_edge()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions());
        var valid = Market("valid-below");
        var rejected = Market("rejected-huge");
        var books = new Dictionary<string, BinaryOrderBookSnapshot>
        {
            [valid.id] = BookForEdge(valid, -0.003m),
            [rejected.id] = BookFor(rejected, yes: null, no: 0.001m)
        };
        var stats = await NewEngine(new MapProvider(books), state, Options(), quiet: true).ScanAsync(new List<Market> { valid, rejected }, NewPaper(), new SemaphoreSlim(2));
        Assert.Equal(0, state.SingleMarketSnapshot.Summary.PositiveEdge);
        Assert.Equal(0, stats.PositiveEdgeFound);
        Assert.Equal(-0.003m, state.SingleMarketSnapshot.Summary.BestEdgeSeen);
        Assert.Equal(-0.003m, stats.BestEdgeSeen);
        Assert.True(state.SingleMarketSnapshot.Summary.BestRejectedRawEdge > 0.9m);
    }

    [Fact]
    public async Task BestEdge_is_null_when_all_markets_are_data_quality_rejected()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions());
        var markets = new[] { Market("missing-yes"), Market("missing-no") }.ToList();
        var books = new Dictionary<string, BinaryOrderBookSnapshot>
        {
            ["missing-yes"] = BookFor(markets[0], yes: null, no: 0.95m),
            ["missing-no"] = BookFor(markets[1], yes: 0.05m, no: null)
        };
        var stats = await NewEngine(new MapProvider(books), state, Options(), quiet: true).ScanAsync(markets, NewPaper(), new SemaphoreSlim(2));
        Assert.Null(state.SingleMarketSnapshot.Summary.BestEdgeSeen);
        Assert.Null(stats.BestEdgeSeen);
    }


    [Fact]
    public async Task Suspicious_ask_sum_high_severity_uses_configured_distance()
    {
        var opts = Options();
        opts.AuditDataQualityRejectedEvents = false;
        opts.AuditHighSeverityDataQualityRejectedEvents = true;
        opts.MaxDataQualityAuditSamplesPerCycle = 3;
        opts.HighSeveritySuspiciousAskSumDistance = 0.10m;
        var low = Market("sum-1078");
        var high = Market("sum-151");
        var books = new Dictionary<string, BinaryOrderBookSnapshot>
        {
            [low.id] = BookFor(low, yes: 0.50m, no: 0.578m),
            [high.id] = BookFor(high, yes: 0.75m, no: 0.76m)
        };
        var audit = NewAudit();
        await NewEngine(new MapProvider(books), new BotRuntimeState(new RuntimeStateOptions()), opts, quiet: true, audit: audit).ScanAsync(new List<Market> { low, high }, NewPaper(), new SemaphoreSlim(2));
        var audited = audit.ListAudit(1000).Where(x => x.Stage == "SingleMarketDataQualityRejected").ToArray();
        Assert.Single(audited);
        Assert.Equal(high.id, audited[0].GroupKey);
    }

    [Fact]
    public async Task OperationalQuietMode_suppresses_below_min_console_and_detected_audit_spam()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions { MaxSingleMarketOpportunities = 200, MaxSingleMarketExecutions = 100 });
        var audit = NewAudit();
        var markets = Enumerable.Range(0, 50).Select(i => Market($"m{i}")).ToList();
        var provider = new MapProvider(markets.ToDictionary(m => m.id, m => BookFor(m, yes: 0.50m, no: 0.499m)));
        var output = await CaptureConsole(() => NewEngine(provider, state, Options(), quiet: true, audit: audit).ScanAsync(markets, NewPaper(), new SemaphoreSlim(8)));
        Assert.DoesNotContain("[SINGLE_MARKET_EDGE_PENDING]", output);
        Assert.DoesNotContain("Stage=SingleMarketDetected", output);
        Assert.Contains("[SINGLE_MARKET_SCAN_SUMMARY]", output);
        Assert.Equal(50, state.SingleMarketSnapshot.Summary.BelowMinEdge);
        Assert.DoesNotContain(audit.ListAudit(1000), x => x.Stage is "SingleMarketDetected" or "SingleMarketEdgePending");
    }

    [Fact]
    public async Task Data_quality_rejections_are_aggregated_by_reason_with_bounded_samples()
    {
        var runtime = new RuntimeStateOptions { MaxSingleMarketDataQualitySamples = 3, MaxSingleMarketNearMisses = 2 };
        var state = new BotRuntimeState(runtime);
        var markets = new[] { Market("missing-yes"), Market("missing-no"), Market("bad-sum"), Market("same-token") }.ToList();
        var books = new Dictionary<string, BinaryOrderBookSnapshot>
        {
            ["missing-yes"] = BookFor(markets[0], yes: null, no: 0.95m),
            ["missing-no"] = BookFor(markets[1], yes: 0.05m, no: null),
            ["bad-sum"] = BookFor(markets[2], yes: 0.39m, no: 0.43m),
            ["same-token"] = BookFor(markets[3], yes: 0.05m, no: 0.95m) with { NoTokenId = "yes-same-token" }
        };
        await NewEngine(new MapProvider(books), state, Options(), quiet: true).ScanAsync(markets, NewPaper(), new SemaphoreSlim(4));
        var summary = state.SingleMarketSnapshot.Summary;
        Assert.Equal(4, summary.DataQualityRejected);
        Assert.Equal(1, summary.DataQualityRejectedByReason["MissingYesAsk"]);
        Assert.Equal(1, summary.DataQualityRejectedByReason["MissingNoAsk"]);
        Assert.Equal(1, summary.DataQualityRejectedByReason["SuspiciousYesNoAskSum"]);
        Assert.True(state.SingleMarketSnapshot.DataQualityRejectSamples.Count <= runtime.MaxSingleMarketDataQualitySamples);
    }



    [Fact]
    public async Task Data_quality_audit_samples_are_limited_in_quiet_mode()
    {
        var opts = Options();
        opts.MaxDataQualityAuditSamplesPerCycle = 2;
        opts.AuditDataQualityRejectedEvents = true;
        var audit = NewAudit();
        var state = new BotRuntimeState(new RuntimeStateOptions());
        var markets = Enumerable.Range(0, 20).Select(i => Market($"dq{i}")).ToList();
        var books = markets.ToDictionary(m => m.id, m => BookFor(m, yes: null, no: 0.95m));
        await NewEngine(new MapProvider(books), state, opts, quiet: true, audit: audit).ScanAsync(markets, NewPaper(), new SemaphoreSlim(8));
        Assert.True(audit.ListAudit(1000).Count(x => x.Stage == "SingleMarketDataQualityRejected") <= 2);
    }



    [Fact]
    public async Task Data_quality_summary_logs_first_only_until_material_change_or_periodic()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions());
        var market = Market("missing-yes");
        var books = new Dictionary<string, BinaryOrderBookSnapshot> { [market.id] = BookFor(market, yes: null, no: 0.95m) };
        var engine = NewEngine(new MapProvider(books), state, Options(), quiet: true);
        var first = await CaptureConsole(() => engine.ScanAsync(new List<Market> { market }, NewPaper(), new SemaphoreSlim(1)));
        var second = await CaptureConsole(() => engine.ScanAsync(new List<Market> { market }, NewPaper(), new SemaphoreSlim(1)));
        Assert.Contains("[SINGLE_MARKET_DATA_QUALITY_SUMMARY]", first);
        Assert.DoesNotContain("[SINGLE_MARKET_DATA_QUALITY_SUMMARY]", second);
    }


    [Fact]
    public async Task Data_quality_summary_suppresses_noisy_rolling_batch_changes()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions());
        var firstMarkets = Enumerable.Range(0, 50).Select(i => Market($"noise-a-{i}")).ToList();
        var secondMarkets = firstMarkets.Take(27).ToList();
        var thirdMarkets = firstMarkets.Take(43).ToList();
        var books = firstMarkets.ToDictionary(m => m.id, m => BookFor(m, yes: null, no: 0.95m));
        var engine = NewEngine(new MapProvider(books), state, Options(), quiet: true);

        var first = await CaptureConsole(() => engine.ScanAsync(firstMarkets, NewPaper(), new SemaphoreSlim(8)));
        var second = await CaptureConsole(() => engine.ScanAsync(secondMarkets, NewPaper(), new SemaphoreSlim(8)));
        var third = await CaptureConsole(() => engine.ScanAsync(thirdMarkets, NewPaper(), new SemaphoreSlim(8)));

        Assert.Contains("[SINGLE_MARKET_DATA_QUALITY_SUMMARY]", first);
        Assert.DoesNotContain("[SINGLE_MARKET_DATA_QUALITY_SUMMARY]", second);
        Assert.DoesNotContain("[SINGLE_MARKET_DATA_QUALITY_SUMMARY]", third);
    }

    [Fact]
    public async Task Data_quality_summary_logs_material_reason_distribution_change()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions());
        var firstMarkets = Enumerable.Range(0, 50).Select(i => Market($"dist-a-{i}")).ToList();
        var secondMarkets = Enumerable.Range(0, 50).Select(i => Market($"dist-b-{i}")).ToList();
        var books = firstMarkets.ToDictionary(m => m.id, m => BookFor(m, yes: null, no: 0.95m));
        foreach (var market in secondMarkets) books[market.id] = BookFor(market, yes: 0.05m, no: null);
        var engine = NewEngine(new MapProvider(books), state, Options(), quiet: true);

        var first = await CaptureConsole(() => engine.ScanAsync(firstMarkets, NewPaper(), new SemaphoreSlim(8)));
        var second = await CaptureConsole(() => engine.ScanAsync(secondMarkets, NewPaper(), new SemaphoreSlim(8)));

        Assert.Contains("[SINGLE_MARKET_DATA_QUALITY_SUMMARY]", first);
        Assert.Contains("[SINGLE_MARKET_DATA_QUALITY_SUMMARY]", second);
    }

    [Fact]
    public async Task Single_market_scan_summary_logs_first_only_until_material_change_or_periodic()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions());
        var market = Market("below");
        var books = new Dictionary<string, BinaryOrderBookSnapshot> { [market.id] = BookForEdge(market, -0.003m) };
        var engine = NewEngine(new MapProvider(books), state, Options(), quiet: true);
        var first = await CaptureConsole(() => engine.ScanAsync(new List<Market> { market }, NewPaper(), new SemaphoreSlim(1)));
        var second = await CaptureConsole(() => engine.ScanAsync(new List<Market> { market }, NewPaper(), new SemaphoreSlim(1)));
        Assert.Contains("[SINGLE_MARKET_SCAN_SUMMARY]", first);
        Assert.DoesNotContain("[SINGLE_MARKET_SCAN_SUMMARY]", second);
    }

    [Fact]
    public async Task Top_near_misses_keep_only_configured_top_n_and_payload_is_compact()
    {
        var opts = Options();
        opts.TopNearMissCount = 2;
        opts.NearMissMinEdge = -0.01m;
        var state = new BotRuntimeState(new RuntimeStateOptions { MaxSingleMarketNearMisses = 2, MaxSingleMarketDataQualitySamples = 2 });
        var markets = Enumerable.Range(0, 5).Select(i => Market($"near{i}")).ToList();
        var books = markets.Select((m, i) => (m, edge: -0.001m * (i + 1))).ToDictionary(x => x.m.id, x => BookForEdge(x.m, x.edge));
        await NewEngine(new MapProvider(books), state, opts, quiet: true).ScanAsync(markets, NewPaper(), new SemaphoreSlim(4));
        Assert.Equal(2, state.SingleMarketSnapshot.TopNearMisses.Count);
        Assert.True(state.SingleMarketSnapshot.TopNearMisses[0].EdgePerShare >= state.SingleMarketSnapshot.TopNearMisses[1].EdgePerShare);
        Assert.Empty(state.SingleMarketSnapshot.PositiveCandidates);
        var output = await CaptureConsole(() => NewEngine(new MapProvider(books), state, opts, quiet: true).ScanAsync(markets, NewPaper(), new SemaphoreSlim(4)));
        Assert.DoesNotContain("[SINGLE_MARKET_TOP_NEAR_MISS]", output);
    }

    [Fact]
    public async Task Positive_edge_edge_stable_and_paper_open_still_log_per_market()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions());
        var engine = NewEngine(Book(), state, Options(), quiet: true);
        var output = await CaptureConsole(async () =>
        {
            for (var i = 0; i < 5; i++) await engine.ScanAsync(new List<Market> { Market() }, NewPaper(), new SemaphoreSlim(1));
        });
        Assert.Contains("[SINGLE_MARKET_POSITIVE_EDGE_DETECTED]", output);
        Assert.Contains("[SINGLE_MARKET_EDGE_STABLE]", output);
        Assert.Contains("[SINGLE_MARKET_FILL_SIMULATION_PASSED]", output);
        Assert.Contains("[PAPER POSITION OPENED]", output);
    }




    [Fact]
    public async Task RuntimeHealth_single_market_counters_reflect_bounded_snapshot_after_scan()
    {
        var runtime = new RuntimeStateOptions { MaxSingleMarketDataQualitySamples = 2, MaxSingleMarketNearMisses = 2 };
        var state = new BotRuntimeState(runtime);
        var markets = new[] { Market("dq-a"), Market("near-a"), Market("near-b") }.ToList();
        var books = new Dictionary<string, BinaryOrderBookSnapshot>
        {
            ["dq-a"] = BookFor(markets[0], yes: null, no: 0.95m),
            ["near-a"] = BookForEdge(markets[1], -0.003m),
            ["near-b"] = BookForEdge(markets[2], -0.004m)
        };
        await NewEngine(new MapProvider(books), state, Options(), quiet: true).ScanAsync(markets, NewPaper(), new SemaphoreSlim(3));
        var health = RuntimeHealthSnapshot.From(state);
        Assert.True(health.SingleMarketDataQualitySamplesCount > 0);
        Assert.True(health.SingleMarketDataQualitySamplesCount <= runtime.MaxSingleMarketDataQualitySamples);
        Assert.True(health.SingleMarketNearMissesCount > 0);
        Assert.True(health.SingleMarketNearMissesCount <= runtime.MaxSingleMarketNearMisses);
    }

    [Fact]
    public void Soak_readiness_log_literal_is_emitted_from_startup()
    {
        var text = File.ReadAllText(Path.Combine("..", "..", "..", "..", "TradingBot", "Program.cs"));
        Assert.Contains("[SOAK_READINESS]", text);
        Assert.Contains("PaperOnly=", text);
        Assert.Contains("LiveTrading=", text);
    }

    [Fact]
    public async Task Execution_audit_remains_bounded_after_1000_low_value_scanned_markets()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions { MaxExecutionAuditEvents = 500 });
        var audit = NewAudit();
        var markets = Enumerable.Range(0, 1000).Select(i => Market($"bulk{i}")).ToList();
        var books = markets.ToDictionary(m => m.id, m => BookFor(m, yes: 0.50m, no: 0.499m));
        await NewEngine(new MapProvider(books), state, Options(), quiet: true, audit: audit).ScanAsync(markets, NewPaper(), new SemaphoreSlim(32));
        Assert.True(audit.ListAudit(1000).Count <= 500);
        Assert.DoesNotContain(audit.ListAudit(1000), x => x.Stage is "SingleMarketDetected" or "SingleMarketEdgePending");
    }

    [Fact]
    public void Runtime_logs_and_execution_audit_are_bounded_after_many_events()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions { MaxRecentLogs = 10, MaxSignalREventBuffer = 5 });
        for (var i = 0; i < 1000; i++)
        {
            state.AddLog(new TerminalLogEntry($"l{i}", DateTime.UtcNow, "info", "test", "x", i));
            state.AddSignalREvent("singleMarketArbsUpdated");
        }
        Assert.Equal(10, state.Logs().Length);
        Assert.Equal(5, state.SignalREventBufferCount);
    }

    private static SingleMarketOrderBookArbEngine NewEngine(BinaryOrderBookSnapshot book, BotRuntimeState state, SingleMarketArbOptions? opts = null, bool quiet = false, VerifiedBasketExecutionCoordinator? audit = null) => NewEngine(new FakeProvider(book), state, opts, quiet, audit);
    private static SingleMarketOrderBookArbEngine NewEngine(IOrderBookProvider provider, BotRuntimeState state, SingleMarketArbOptions? opts = null, bool quiet = false, VerifiedBasketExecutionCoordinator? audit = null) => new(provider, 0.005m, 0.001m, 0.001m, null, new ExecutionSizingService(new ExecutionPolicy { MaxNotionalPerTrade = 100m, MinNotionalPerTrade = 25m }), opts ?? Options(), state, null, audit, quiet, new MultiOutcomeLoggingOptions { LogSingleMarketSummaryOnChangeOnly = true, LogSingleMarketDataQualityOnChangeOnly = true, LogSingleMarketSummaryEveryNCycles = 25, LogSingleMarketDataQualityEveryNCycles = 25, SingleMarketDataQualitySignificantDelta = 25, LogSingleMarketNearMissEveryNCycles = 50, LogSingleMarketNearMissOnChangeOnly = true });
    private static SingleMarketArbOptions Options() => new() { RequiredConsecutiveEdgeScans = 3, RequiredConsecutiveExecutionReadyScans = 3, MinEdgePerShare = 0.005m, MinExpectedProfit = 0.50m, MinNotional = 25m, MaxNotionalPerTrade = 100m, MaxOpenSingleMarketPositions = 3, MaxTotalSingleMarketExposure = 300m, MaxPositionsPerCycle = 1, CooldownSecondsPerMarket = 300, AuditBelowMinEdgeEvents = false, AuditDetectedEvents = false, MaxAuditSamplesPerCycle = 20, AuditDataQualityRejectedEvents = false, AuditHighSeverityDataQualityRejectedEvents = true, MaxDataQualityAuditSamplesPerCycle = 3, HighSeveritySuspiciousAskSumDistance = 0.10m };
    private static PaperTradingEngine NewPaper() => new(new ExecutionPolicy { MaxNotionalPerTrade = 100m, MinNotionalPerTrade = 25m, MaxOpenPositions = 100, MaxLockedCapital = 1000m, MaxExposurePerGroup = 300m }, null, null, new PaperPositionBook(Path.GetTempFileName()));
    private static Market Market(string id = "m1") => new() { id = id, question = $"Will {id} win?", conditionId = $"c-{id}", outcomes = new() { "Yes", "No" }, clobTokenIds = new() { $"yes-{id}", $"no-{id}" } };
    private static BinaryOrderBookSnapshot Book(decimal? yes = 0.235m, decimal? no = 0.733m, decimal yesSize = 200m, decimal noSize = 200m) => BookFor(Market(), yes, no, yesSize, noSize);
    private static BinaryOrderBookSnapshot BookFor(Market market, decimal? yes = 0.235m, decimal? no = 0.733m, decimal yesSize = 200m, decimal noSize = 200m) => new(market.id, market.question, market.clobTokenIds[0], market.clobTokenIds[1], null, yes.HasValue ? new BookQuote(yes.Value, yesSize) : null, null, no.HasValue ? new BookQuote(no.Value, noSize) : null, DateTime.UtcNow);
    private static BinaryOrderBookSnapshot BookForEdge(Market market, decimal edge)
    {
        var raw = 1m - edge - 0.002m;
        return BookFor(market, 0.5m, raw - 0.5m, 200m, 200m);
    }
    private static VerifiedBasketExecutionCoordinator NewAudit() => new(Microsoft.Extensions.Options.Options.Create(new ExecutionOptions()), Microsoft.Extensions.Options.Options.Create(new TradingBotOptions { RuntimeState = new RuntimeStateOptions { MaxExecutionAuditEvents = 500 } }));
    private static async Task<string> CaptureConsole(Func<Task> action)
    {
        var original = Console.Out;
        using var writer = new StringWriter();
        Console.SetOut(writer);
        try { await action(); }
        finally { Console.SetOut(original); }
        return writer.ToString();
    }
    private sealed class FakeProvider(BinaryOrderBookSnapshot book) : IOrderBookProvider { public Task<BinaryOrderBookSnapshot?> GetBinarySnapshotAsync(Market market, CancellationToken ct = default) => Task.FromResult<BinaryOrderBookSnapshot?>(book with { TimestampUtc = DateTime.UtcNow }); }
    private sealed class MapProvider(IReadOnlyDictionary<string, BinaryOrderBookSnapshot> books) : IOrderBookProvider { public Task<BinaryOrderBookSnapshot?> GetBinarySnapshotAsync(Market market, CancellationToken ct = default) => Task.FromResult<BinaryOrderBookSnapshot?>(books[market.id] with { TimestampUtc = DateTime.UtcNow }); }
}
