using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Options;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class PaperSettlementValidationTests
{
    private static TradingBotOptions Options() => new()
    {
        EnableLiveExecution = false,
        EnablePaperTrading = true,
        PaperOnly = true,
        TradingMode = new TradingModeOptions { LiveTradingEnabled = false, PaperTradingEnabled = true, PaperPhase = 2 },
        PaperRisk = new PaperRiskOptions { MaxPaperPositionsTotal = 5, MaxPaperPositionsPerStrategy = 2, MaxPaperOpenPerHour = 2, MaxPaperNotionalPerTrade = 50m, MaxPaperTotalExposure = 200m },
        SingleMarketArb = new SingleMarketArbOptions { PaperOnly = true, MinNotional = 10m, MaxNotionalPerTrade = 50m, MaxOpenSingleMarketPositions = 2, MaxTotalSingleMarketExposure = 100m },
        PaperSettlementValidation = new PaperSettlementValidationOptions { Enabled = true, RunOnce = true, RequireExistingOpenPosition = true, IfNoOpenPositionCreateSyntheticFirst = true, SyntheticOpportunityType = "SingleMarketBuyBoth", SettlementMode = "ManualPayout", RealizedPayout = 12m, ExpectedOpenCost = 10.8m, MaxSyntheticSettlements = 1, RequireExplicitConfigFlag = true }
    };

    private static (TradingBotOptions Options, PaperTradingEngine Paper, PaperPositionBook Book, BotRuntimeState State, string Dir) Harness()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var options = Options();
        var book = new PaperPositionBook(Path.Combine(dir, "paper-positions.csv"));
        var paper = new PaperTradingEngine(positionBook: book, botOptions: options);
        return (options, paper, book, new BotRuntimeState(), dir);
    }

    [Fact]
    public void PaperSettlementValidation_disabled_by_default()
    {
        Assert.False(new TradingBotOptions().PaperSettlementValidation.Enabled);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var options = new TradingBotOptions();
        configuration.GetSection(TradingBotOptions.SectionName).Bind(options);
        Assert.False(options.PaperSettlementValidation.Enabled);
    }

    [Fact]
    public void Settlement_validation_creates_and_closes_synthetic_position_with_correct_accounting()
    {
        var h = Harness();
        var result = new PaperSettlementValidationHarness().TryRun(h.Options, h.Paper, h.Book, h.State, h.Dir);

        Assert.NotNull(result);
        Assert.True(result!.Passed);
        Assert.Equal(1, result.Opened);
        Assert.Equal(1, result.Closed);
        Assert.Empty(h.Book.OpenPositions);
        Assert.Single(h.Book.ClosedPositions);
        Assert.Equal(0m, h.Paper.LockedCapital);
        Assert.Equal(1001.2m, h.Paper.Balance);
        Assert.Equal(1.2m, h.Paper.RealizedPnl);
        Assert.Equal(1001.2m, h.Paper.Equity);
        Assert.Equal(0, result.LiveOrders);
        Assert.Equal(0, result.SigningAttempts);
    }

    [Fact]
    public void ExpectedProfit_is_not_double_counted_after_settlement()
    {
        var h = Harness();
        _ = new PaperSettlementValidationHarness().TryRun(h.Options, h.Paper, h.Book, h.State, h.Dir);
        Assert.Equal(1001.2m, h.Paper.Equity);
        Assert.Equal(h.Paper.Balance + h.Paper.LockedCapital + h.Paper.UnrealizedPnl, h.Paper.Equity);
    }

    [Fact]
    public void Cannot_close_unknown_position()
    {
        var h = Harness();
        var result = h.Paper.SettlePositionDetailed("missing", 12m);
        Assert.False(result.Accepted);
        Assert.Equal("PositionNotFound", result.Reason);
    }

    [Fact]
    public void Cannot_close_already_closed_and_duplicate_settlement_is_suppressed()
    {
        var h = Harness();
        var pos = OpenSynthetic(h.Paper, h.Book);
        Assert.True(h.Paper.SettlePositionDetailed(pos.PositionId, 12m).Accepted);
        var duplicate = h.Paper.SettlePositionDetailed(pos.PositionId, 12m);
        Assert.False(duplicate.Accepted);
        Assert.True(duplicate.DuplicateSuppressed);
        Assert.Equal("DuplicateSettlement", duplicate.Reason);
        Assert.Equal(1, h.Paper.DuplicateSettlementSuppressions);
    }

    [Fact]
    public void Negative_payout_rejected()
    {
        var h = Harness();
        var pos = OpenSynthetic(h.Paper, h.Book);
        var result = h.Paper.SettlePositionDetailed(pos.PositionId, -1m);
        Assert.False(result.Accepted);
        Assert.Equal("InvalidPayout", result.Reason);
        Assert.Single(h.Book.OpenPositions);
    }

    [Fact]
    public void Settlement_does_not_affect_unrelated_positions()
    {
        var h = Harness();
        var first = OpenSynthetic(h.Paper, h.Book);
        var other = new TradingBot.Models.ArbOpportunity(
            new TradingBot.Models.ArbLeg("__paper_settlement_validation_other__", "Other", "YES", 0.45m, 100m),
            new TradingBot.Models.ArbLeg("__paper_settlement_validation_other__", "Other", "NO", 0.45m, 100m),
            12m, 0.90m, 0.10m, 1.2m, 1.0, "SingleMarketBuyBoth", "OtherSynthetic");
        Assert.True(h.Paper.RecordArbitrage(other));
        var unrelatedBefore = h.Book.OpenPositions.First(p => p.PositionId != first.PositionId).TotalCost;
        Assert.True(h.Paper.SettlePositionDetailed(first.PositionId, 12m).Accepted);
        Assert.Single(h.Book.OpenPositions);
        Assert.Equal(unrelatedBefore, h.Book.OpenPositions.Single().TotalCost);
    }

    [Fact]
    public void Paper_settlement_exports_are_written_and_api_state_has_closed_record()
    {
        var h = Harness();
        var result = new PaperSettlementValidationHarness().TryRun(h.Options, h.Paper, h.Book, h.State, h.Dir);
        Assert.True(result!.Passed);
        Assert.True(File.Exists(Path.Combine(h.Dir, "exports", "paper-account-latest.json")));
        Assert.True(File.Exists(Path.Combine(h.Dir, "exports", "paper-positions-latest.json")));
        Assert.True(File.Exists(Path.Combine(h.Dir, "exports", "paper-executions-latest.json")));
        Assert.True(File.Exists(Path.Combine(h.Dir, "exports", "paper-settlements-latest.json")));
        Assert.True(File.Exists(Path.Combine(h.Dir, "exports", "paper-settlement-validation-latest.json")));
        Assert.Single(h.State.PaperSettlementsRecords());
        Assert.Equal("Closed", h.State.PaperSettlementsRecords().Single().Status);
    }

    [Fact]
    public void RuntimeHealth_includes_settlement_counters()
    {
        var h = Harness();
        _ = new PaperSettlementValidationHarness().TryRun(h.Options, h.Paper, h.Book, h.State, h.Dir);
        var health = RuntimeHealthSnapshot.From(h.State, h.Options);
        Assert.Equal(1, health.PaperClosedPositions);
        Assert.Equal(1, health.PaperSettlements);
        Assert.Equal(1.2m, health.PaperRealizedPnl);
        Assert.Equal(0m, health.PaperLocked);
        Assert.Equal(1001.2m, health.PaperCash);
        Assert.Equal(1001.2m, health.PaperEquity);
        Assert.Equal(1, health.PaperOpenEvents);
        Assert.Equal(1, health.PaperCloseEvents);
        Assert.Equal(2, health.PaperLifecycleEvents);
        Assert.Equal(1, health.PaperExecutionsCount);
    }

    [Fact]
    public void LiveTrading_remains_false_and_signing_attempts_zero()
    {
        var h = Harness();
        var before = LiveTradingGuard.SigningAttempts;
        _ = new PaperSettlementValidationHarness().TryRun(h.Options, h.Paper, h.Book, h.State, h.Dir);
        Assert.False(h.Options.TradingMode.LiveTradingEnabled);
        Assert.Equal(before, LiveTradingGuard.SigningAttempts);
    }

    private static TradingBot.Models.PaperPosition OpenSynthetic(PaperTradingEngine paper, PaperPositionBook book)
    {
        Assert.True(paper.RecordArbitrage(PaperSettlementValidationHarness.BuildSyntheticOpportunity()));
        return book.OpenPositions.Single();
    }
}
