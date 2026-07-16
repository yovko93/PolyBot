using System.Text.Json;
using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record PaperSettlementValidationResult(
    DateTime TimestampUtc,
    bool Enabled,
    bool Passed,
    string FailureStage,
    string FailureReason,
    int Opened,
    int Closed,
    decimal CashBefore,
    decimal CashAfter,
    decimal LockedBefore,
    decimal LockedAfter,
    decimal EquityBefore,
    decimal EquityAfter,
    decimal RealizedPnl,
    int LiveOrders,
    int SigningAttempts,
    string? PositionId);

public sealed class PaperSettlementValidationHarness
{
    private readonly object _gate = new();
    private bool _hasRun;
    public const string SectionPath = $"{TradingBotOptions.SectionName}:PaperSettlementValidation";

    public PaperSettlementValidationResult? TryRun(TradingBotOptions options, PaperTradingEngine paper, PaperPositionBook book, BotRuntimeState state, string contentRootPath)
    {
        LogStartupConfig(options);
        if (!options.PaperSettlementValidation.Enabled) return null;
        lock (_gate)
        {
            if (_hasRun && options.PaperSettlementValidation.RunOnce) return null;
            _hasRun = true;
        }
        var result = Run(options, paper, book, state, contentRootPath);
        Export(contentRootPath, result);
        PaperAccountExporter.ExportLatest(Path.Combine(contentRootPath, "exports"), paper, book, state.SingleMarketExecutions(), paper.BlockedCountsByReason);
        Console.WriteLine($"[PAPER_SETTLEMENT_VALIDATION_RESULT] Passed={result.Passed.ToString().ToLowerInvariant()} Opened={result.Opened} Closed={result.Closed} RealizedPnl={result.RealizedPnl:0.####} LiveOrders={result.LiveOrders} SigningAttempts={result.SigningAttempts}");
        return result;
    }

    public static void LogStartupConfig(TradingBotOptions options)
    {
        var c = options.PaperSettlementValidation;
        Console.WriteLine($"[PAPER_SETTLEMENT_VALIDATION_CONFIG] Enabled={c.Enabled.ToString().ToLowerInvariant()} RunOnce={c.RunOnce.ToString().ToLowerInvariant()} RequireExistingOpenPosition={c.RequireExistingOpenPosition.ToString().ToLowerInvariant()} IfNoOpenPositionCreateSyntheticFirst={c.IfNoOpenPositionCreateSyntheticFirst.ToString().ToLowerInvariant()} SyntheticOpportunityType={c.SyntheticOpportunityType} SettlementMode={c.SettlementMode} RealizedPayout={c.RealizedPayout:0.####} ExpectedOpenCost={c.ExpectedOpenCost:0.####} MaxSyntheticSettlements={c.MaxSyntheticSettlements} RequireExplicitConfigFlag={c.RequireExplicitConfigFlag.ToString().ToLowerInvariant()}");
    }

    private static PaperSettlementValidationResult Run(TradingBotOptions options, PaperTradingEngine paper, PaperPositionBook book, BotRuntimeState state, string contentRootPath)
    {
        var cashBefore = paper.Balance;
        var lockedBefore = paper.LockedCapital;
        var equityBefore = paper.Equity;
        const int liveOrders = 0;
        var signingAttempts = (int)LiveTradingGuard.SigningAttempts;
        var opened = 0;

        PaperSettlementValidationResult Fail(string stage, string reason, PaperPosition? p = null)
        {
            Console.WriteLine($"[PAPER_SETTLEMENT_VALIDATION_FAILED] Stage={stage} Reason={reason}");
            return Build(options, false, stage, reason, opened, 0, cashBefore, paper.Balance, lockedBefore, paper.LockedCapital, equityBefore, paper.Equity, paper.RealizedPnl, liveOrders, signingAttempts, p?.PositionId);
        }

        var cfg = options.PaperSettlementValidation;
        if (cfg.RequireExplicitConfigFlag && !cfg.Enabled) return Fail("ConfigBinding", "ExplicitSettlementValidationFlagRequired");
        if (options.TradingMode.LiveTradingEnabled || options.EnableLiveExecution) return Fail("Safety", "LiveTradingEnabled");
        if (!options.TradingMode.PaperTradingEnabled || !options.EnablePaperTrading || !options.PaperOnly) return Fail("Safety", "PaperTradingNotEnabled");
        if (!cfg.SettlementMode.Equals("ManualPayout", StringComparison.OrdinalIgnoreCase)) return Fail("ConfigBinding", "UnsupportedSettlementMode");
        if (cfg.RealizedPayout < 0m) return Fail("ConfigBinding", "InvalidPayout");
        if (cfg.MaxSyntheticSettlements < 1) return Fail("ConfigBinding", "MaxSyntheticSettlementsBelowOne");

        var position = book.GetOpenPositions().FirstOrDefault();
        if (position is null)
        {
            if (!cfg.IfNoOpenPositionCreateSyntheticFirst) return Fail("PositionSelection", "OpenPositionMissing");
            if (!cfg.SyntheticOpportunityType.Equals("SingleMarketBuyBoth", StringComparison.OrdinalIgnoreCase)) return Fail("SyntheticOpportunityCreation", "UnsupportedSyntheticOpportunityType");
            var opportunity = BuildSyntheticOpportunity();
            if (!paper.RecordArbitrage(opportunity)) return Fail("PaperOpen", paper.BlockedCountsByReason.LastOrDefault().Key ?? "PaperOpenRejected");
            opened = 1;
            position = book.GetOpenPositions().FirstOrDefault(p => p.GroupKey.Equals("single-market:__paper_settlement_validation_single_market__", StringComparison.OrdinalIgnoreCase));
        }

        if (position is null) return Fail("PositionSelection", "OpenPositionMissing");
        Console.WriteLine($"[PAPER_SETTLEMENT_VALIDATION_POSITION_READY] PositionId={position.PositionId}");
        if (Math.Abs(position.TotalCost - cfg.ExpectedOpenCost) > 0.0001m) return Fail("AccountingValidation", "ExpectedOpenCostMismatch", position);

        var result = paper.SettlePositionDetailed(position.PositionId, cfg.RealizedPayout, cfg.SettlementMode, options.TradingMode.LiveTradingEnabled || options.EnableLiveExecution);
        if (!result.Accepted || result.Position is null) return Fail("Settlement", result.Reason, position);
        var closed = result.Position;
        var expectedPnl = cfg.RealizedPayout - closed.TotalCost;
        var ok = closed.Status == PaperPositionStatus.Closed
            && book.GetOpenPositions().All(p => !p.PositionId.Equals(closed.PositionId, StringComparison.OrdinalIgnoreCase))
            && paper.LockedCapital == lockedBefore + (opened == 1 ? closed.TotalCost : 0m) - closed.TotalCost
            && paper.Balance == cashBefore - (opened == 1 ? closed.TotalCost : 0m) + cfg.RealizedPayout
            && paper.RealizedPnl == expectedPnl
            && paper.Equity == paper.Balance + paper.LockedCapital + paper.UnrealizedPnl;
        if (!ok) return Fail("AccountingValidation", "PaperSettlementAccountInvariantFailed", closed);

        state.ReplacePositions(book.OpenPositions.Concat(book.ClosedPositions).Take(200).Select(p => PaperPositionDtoFactory.ToDto(p, state.NextSeq())));
        state.ReplacePaperSettlements(book.Settlements);
        state.SetPaperSettlementCounters(paper.SettlementRejects, paper.DuplicateSettlementSuppressions);
        state.SetPaperOpenCountLastHour(paper.HourlyOpenCount);
        state.SetStatus(new BotStatusDto("PAPER", true, "CONNECTED", paper.Balance, paper.LockedCapital, paper.Equity, paper.RealizedPnl, paper.ExpectedProfit, book.OpenPositions.Count, state.Status.SignalCount, DateTime.UtcNow, DateTime.UtcNow));
        Export(contentRootPath, Build(options, true, "Complete", "Passed", opened, 1, cashBefore, paper.Balance, lockedBefore, paper.LockedCapital, equityBefore, paper.Equity, paper.RealizedPnl, liveOrders, signingAttempts, closed.PositionId));
        return Build(options, true, "Complete", "Passed", opened, 1, cashBefore, paper.Balance, lockedBefore, paper.LockedCapital, equityBefore, paper.Equity, paper.RealizedPnl, liveOrders, signingAttempts, closed.PositionId);
    }

    public static ArbOpportunity BuildSyntheticOpportunity()
    {
        var marketId = "__paper_settlement_validation_single_market__";
        var question = "Paper Settlement Validation Synthetic Market";
        var quantity = 12m;
        var yesPrice = 0.45m;
        var noPrice = 0.45m;
        var costPerShare = yesPrice + noPrice;
        var edgePerShare = 1m - costPerShare;
        return new ArbOpportunity(new ArbLeg(marketId, question, "YES", yesPrice, 100m), new ArbLeg(marketId, question, "NO", noPrice, 100m), quantity, costPerShare, edgePerShare, quantity * edgePerShare, 1.0, "SingleMarketBuyBoth", "SingleMarketBuyBoth");
    }

    private static PaperSettlementValidationResult Build(TradingBotOptions options, bool passed, string stage, string reason, int opened, int closed, decimal cashBefore, decimal cashAfter, decimal lockedBefore, decimal lockedAfter, decimal equityBefore, decimal equityAfter, decimal realizedPnl, int liveOrders, int signingAttempts, string? positionId)
        => new(DateTime.UtcNow, options.PaperSettlementValidation.Enabled, passed, stage, reason, opened, closed, cashBefore, cashAfter, lockedBefore, lockedAfter, equityBefore, equityAfter, realizedPnl, liveOrders, signingAttempts, positionId);

    private static void Export(string contentRootPath, PaperSettlementValidationResult result)
    {
        var exports = Path.Combine(contentRootPath, "exports");
        Directory.CreateDirectory(exports);
        File.WriteAllText(Path.Combine(exports, "paper-settlement-validation-latest.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}

public static class PaperPositionDtoFactory
{
    public static PaperPositionDto ToDto(PaperPosition p, long seq)
        => new(p.PositionId, p.OpenedAtUtc, p.ClosedAtUtc, p.Strategy, p.GroupKey, p.Legs.Select(l => $"{l.Outcome}:{l.Question}").ToList(), p.Quantity, p.TotalCost, p.CostPerBasket, p.GuaranteedPayout, p.Quantity * p.Legs.Count, p.GrossEdgeAtOpen, p.NetEdgeAtOpen, p.ExpectedProfit, p.LockedCapital, p.ActiveProfile, p.Source, p.CurrentNoAskSum, p.CurrentExitValue, p.UnrealizedPnl, p.MtmStatus, p.MissingExitPrices, p.RealizedPayout, p.RealizedProfit, p.OpenedFromSimulatedFills, p.FillSimulationId, p.Status.ToString().ToUpperInvariant(), seq, p.IsSyntheticCanary, p.SourceCandidateId, p.ProcessRunId);
}
