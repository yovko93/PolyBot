using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class PaperPhaseSafetyTests
{
    private static TradingBotOptions Options(bool paper = true) => new()
    {
        EnableLiveExecution = false,
        EnablePaperTrading = paper,
        PaperOnly = true,
        TradingMode = new TradingModeOptions { LiveTradingEnabled = false, PaperTradingEnabled = paper, PaperPhase = 1 },
        PaperRisk = new PaperRiskOptions { MaxPaperPositionsTotal = 3, MaxPaperPositionsPerStrategy = 1, MaxPaperOpenPerHour = 1, MaxPaperNotionalPerTrade = 25m, MaxPaperTotalExposure = 75m },
        SingleMarketArb = new SingleMarketArbOptions { PaperOnly = true, MinNotional = 10m, MaxNotionalPerTrade = 25m, MaxOpenSingleMarketPositions = 1, MaxTotalSingleMarketExposure = 25m, CooldownSecondsPerMarket = 1800 },
        VerifiedBasketArb = new VerifiedBasketArbOptions { PaperOnly = true, MaxNotionalPerTrade = 25m, MaxOpenVerifiedBasketPositions = 1, MaxTotalVerifiedBasketExposure = 25m, CooldownSecondsPerGroup = 1800 }
    };

    private static PaperAccountSnapshotForGate Account(decimal cash = 1000m, decimal exposure = 0m, int open = 0, int hourly = 0, IReadOnlyDictionary<string,int>? byStrategy = null)
        => new(cash, exposure, open, byStrategy ?? new Dictionary<string,int>(), hourly);

    private static PaperPreTradeOpportunity Opp(
        bool stableEdge = true,
        bool ready = true,
        bool fill = true,
        bool dataQuality = true,
        bool duplicate = false,
        bool cooldown = false,
        bool locked = false,
        bool repair = false,
        bool diagnosticsOnly = false,
        PaperStrategyKind kind = PaperStrategyKind.SingleMarket,
        decimal notional = 10m)
        => new("SingleMarketBuyBoth", kind == PaperStrategyKind.SingleMarket ? "single-market:m1" : "group-1", kind, true, notional, 0.5m, stableEdge, ready, fill, dataQuality, duplicate, cooldown, repair, repair, locked, diagnosticsOnly);

    [Fact] public void Paper_open_blocked_when_PaperTradingEnabled_false()
    {
        var gate = new PaperPreTradeGate(Options(false));
        Assert.Equal("PaperTradingDisabled", gate.Validate(Opp(), Account()).Reason);
    }

    [Fact] public void Paper_open_allowed_when_enabled_and_all_gates_pass()
    {
        var gate = new PaperPreTradeGate(Options());
        Assert.True(gate.Validate(Opp(), Account()).Approved);
    }

    [Fact] public async Task Live_order_submit_blocked_when_LiveTradingEnabled_false()
    {
        var ex = new DisabledExchangeOrderExecutor();
        await Assert.ThrowsAsync<LiveTradingBlockedException>(() => ex.SubmitAsync(new OrderIntent("o1", "opp1", "g1", "s", "m1", "c1", "q", "t1", "YES", "BUY", "YES", 1m, 1m, 1m, "FOK", "IOC", false, false, DateTime.UtcNow), new ExecutionOptions { EnableLiveTrading = false, EnableLiveOrderSubmission = false, PaperOnly = true }));
    }

    [Fact] public void Order_signing_blocked_when_LiveTradingEnabled_false()
        => Assert.Throws<LiveTradingBlockedException>(() => LiveTradingGuard.AssertOrderSigningAllowed(false));

    [Fact] public void Live_cancellation_blocked_when_LiveTradingEnabled_false()
        => Assert.Throws<LiveTradingBlockedException>(() => LiveTradingGuard.AssertLiveCancellationAllowed(false));

    [Fact] public void Paper_open_allowed_by_guard()
    {
        var gate = new PaperPreTradeGate(Options());
        Assert.True(gate.Validate(Opp(), Account()).Approved);
    }

    [Fact] public void SingleMarket_paper_requires_stable_edge()
        => Assert.Equal("StableEdgeRequired", new PaperPreTradeGate(Options()).Validate(Opp(stableEdge: false), Account()).Reason);

    [Fact] public void SingleMarket_paper_requires_execution_readiness_stable()
        => Assert.Equal("ExecutionReadinessStableRequired", new PaperPreTradeGate(Options()).Validate(Opp(ready: false), Account()).Reason);

    [Fact] public void SingleMarket_paper_requires_fill_simulation()
        => Assert.Equal("FillSimulationRequired", new PaperPreTradeGate(Options()).Validate(Opp(fill: false), Account()).Reason);

    [Fact] public void SingleMarket_paper_rejects_data_quality_failure()
        => Assert.Equal("DataQualityFailed", new PaperPreTradeGate(Options()).Validate(Opp(dataQuality: false), Account()).Reason);

    [Fact] public void VerifiedBasket_paper_rejects_locked_or_quarantined_group()
        => Assert.Equal("LockedOrQuarantinedGroup", new PaperPreTradeGate(Options()).Validate(Opp(kind: PaperStrategyKind.VerifiedBasket, locked: true), Account()).Reason);

    [Fact] public void VerifiedBasket_paper_rejects_repair_suggested_diagnostics_only_group()
    {
        var gate = new PaperPreTradeGate(Options());
        Assert.Equal("RepairSuggestedOrUnresolvedGroup", gate.Validate(Opp(kind: PaperStrategyKind.VerifiedBasket, repair: true), Account()).Reason);
        Assert.Equal("DiagnosticsOnlyProfile", gate.Validate(Opp(kind: PaperStrategyKind.VerifiedBasket, diagnosticsOnly: true), Account()).Reason);
    }

    [Fact] public void Duplicate_paper_open_suppressed()
        => Assert.Equal("DuplicateOpenPosition", new PaperPreTradeGate(Options()).Validate(Opp(duplicate: true), Account()).Reason);

    [Fact] public void Cooldown_suppresses_repeated_open()
        => Assert.Equal("CooldownActive", new PaperPreTradeGate(Options()).Validate(Opp(cooldown: true), Account()).Reason);

    [Fact] public void MaxPaperOpenPerHour_enforced()
        => Assert.Equal("MaxPaperOpenPerHourReached", new PaperPreTradeGate(Options()).Validate(Opp(), Account(hourly: 1)).Reason);

    [Fact] public void MaxPaperTotalExposure_enforced()
        => Assert.Equal("MaxPaperTotalExposureExceeded", new PaperPreTradeGate(Options()).Validate(Opp(notional: 10m), Account(exposure: 70m)).Reason);

    [Fact] public void MaxPaperNotionalPerTrade_enforced()
        => Assert.Equal("MaxPaperNotionalPerTradeExceeded", new PaperPreTradeGate(Options()).Validate(Opp(notional: 26m), Account()).Reason);

    [Fact] public void ExpectedProfit_does_not_increase_equity_at_open()
    {
        var book = new PaperPositionBook(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv"));
        var paper = new PaperTradingEngine(new ExecutionPolicy { MaxNotionalPerTrade = 25m, MinNotionalPerTrade = 1m, MaxLockedCapital = 75m, MaxOpenPositions = 3, MaxExposurePerGroup = 75m }, positionBook: book, botOptions: Options());
        var before = paper.Equity;
        Assert.True(paper.RecordArbitrage(new ArbOpportunity(new ArbLeg("m1", "q", "YES", 0.45m, 100m), new ArbLeg("m1", "q", "NO", 0.45m, 100m), 10m, 0.90m, 0.10m, 1m, 1, "SingleMarketBuyBoth", "SingleMarketBuyBoth")));
        Assert.Equal(before, paper.Equity);
        Assert.True(paper.ExpectedProfit > 0m);
    }

    [Fact] public void Paper_account_export_is_written()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var book = new PaperPositionBook(Path.Combine(dir, "positions.csv"));
        var paper = new PaperTradingEngine(positionBook: book, botOptions: Options());
        PaperAccountExporter.ExportLatest(dir, paper, book);
        Assert.True(File.Exists(Path.Combine(dir, "paper-account-latest.json")));
        Assert.True(File.Exists(Path.Combine(dir, "paper-positions-latest.json")));
        Assert.True(File.Exists(Path.Combine(dir, "paper-executions-latest.json")));
    }

    [Fact] public void Paper_endpoints_return_open_positions_shape()
    {
        var book = new PaperPositionBook(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".csv"));
        var paper = new PaperTradingEngine(new ExecutionPolicy { MaxNotionalPerTrade = 25m, MinNotionalPerTrade = 1m, MaxLockedCapital = 75m, MaxOpenPositions = 3, MaxExposurePerGroup = 75m }, positionBook: book, botOptions: Options());
        Assert.True(paper.RecordArbitrage(new ArbOpportunity(new ArbLeg("m2", "q", "YES", 0.45m, 100m), new ArbLeg("m2", "q", "NO", 0.45m, 100m), 10m, 0.90m, 0.10m, 1m, 1, "SingleMarketBuyBoth", "SingleMarketBuyBoth")));
        Assert.Single(book.GetOpenPositions());
    }

    [Fact] public void RuntimeHealth_includes_paper_counters()
    {
        var state = new BotRuntimeState();
        state.SetStatus(state.Status with { LockedCapital = 25m, OpenPositions = 1 });
        state.RecordPaperPretradeReject("x");
        var h = RuntimeHealthSnapshot.From(state);
        Assert.Equal(1, h.PaperOpenPositions);
        Assert.Equal(25m, h.PaperTotalExposure);
        Assert.Equal(1, h.PaperPretradeRejects);
    }


    [Fact] public void PaperPhaseValidation_defaults_disabled()
    {
        var options = new TradingBotOptions().PaperPhaseValidation;
        Assert.False(options.Enabled);
        Assert.False(options.InjectSyntheticOpportunity);
        Assert.True(options.RunOnce);
        Assert.Equal(1, options.MaxSyntheticPaperOpens);
        Assert.True(options.RequireExplicitConfigFlag);
    }

    [Fact] public void PaperPhaseValidation_harness_opens_once_and_suppresses_duplicate()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var options = Options();
        options.PaperPhaseValidation = new PaperPhaseValidationOptions
        {
            Enabled = true,
            InjectSyntheticOpportunity = true,
            SyntheticOpportunityType = "SingleMarketBuyBoth",
            RunOnce = true,
            MaxSyntheticPaperOpens = 1,
            RequireExplicitConfigFlag = true
        };
        var book = new PaperPositionBook(Path.Combine(dir, "paper-positions.csv"));
        var paper = new PaperTradingEngine(new ExecutionPolicy { MaxNotionalPerTrade = 25m, MinNotionalPerTrade = 1m, MaxLockedCapital = 75m, MaxOpenPositions = 3, MaxExposurePerGroup = 75m }, positionBook: book, botOptions: options);
        var state = new BotRuntimeState();

        var result = new PaperPhaseValidationHarness().TryRun(options, paper, book, state, dir);

        Assert.NotNull(result);
        Assert.True(result!.Passed);
        Assert.Equal(1, result.PaperOpened);
        Assert.Equal(1, result.DuplicateSuppressed);
        Assert.Equal(0, result.LiveOrders);
        Assert.Equal(0, result.SigningAttempts);
        Assert.Equal(1, book.OpenPositions.Count);
        Assert.Equal(1, state.PaperExecutionsCount);
        Assert.True(result.PaperExposure > 0m);
        Assert.True(result.CashAfter < result.CashBefore);
        Assert.True(result.LockedAfter > result.LockedBefore);
        Assert.Equal(result.EquityBefore, result.EquityAfter);
        Assert.Equal(0m, result.RealizedPnlAfter);
        Assert.Equal("Open", result.PositionStatus);
        Assert.True(File.Exists(Path.Combine(dir, "exports", "paper-phase-validation-latest.json")));
        Assert.True(File.Exists(Path.Combine(dir, "exports", "paper-account-latest.json")));
        Assert.True(File.Exists(Path.Combine(dir, "exports", "paper-positions-latest.json")));
        Assert.True(File.Exists(Path.Combine(dir, "exports", "paper-executions-latest.json")));
    }

    [Fact] public void UI_shows_PaperOnly_and_LiveTrading_false()
    {
        var ui = File.ReadAllText(Path.Combine("..", "..", "..", "..", "TradingBot.UI", "src", "App.tsx"));
        Assert.Contains("PaperOnly", ui);
        Assert.Contains("LiveTrading=false", ui);
        Assert.Contains("No live orders submitted", ui);
    }
}
