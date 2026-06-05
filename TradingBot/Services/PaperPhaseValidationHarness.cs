using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record PaperPhaseValidationResult(
    DateTime TimestampUtc,
    bool Enabled,
    bool Passed,
    string FailureStage,
    string FailureReason,
    int PaperOpened,
    int DuplicateSuppressed,
    int LiveOrders,
    int SigningAttempts,
    decimal CashBefore,
    decimal CashAfter,
    decimal LockedBefore,
    decimal LockedAfter,
    decimal EquityBefore,
    decimal EquityAfter,
    decimal RealizedPnlAfter,
    decimal PaperExposure,
    int PaperOpenPositions,
    string? PositionStatus);

public sealed record PaperPhaseValidationConfigDiagnostics(
    string SectionPath,
    bool Enabled,
    bool InjectSyntheticOpportunity,
    string Environment,
    string ContentRoot,
    string LoadedConfigFiles,
    string CommandLineArgs);

public sealed class PaperPhaseValidationHarness
{
    private readonly object _gate = new();
    private bool _hasRun;

    public PaperPhaseValidationResult? TryRun(
        TradingBotOptions options,
        PaperTradingEngine paper,
        PaperPositionBook positionBook,
        BotRuntimeState state,
        string contentRootPath)
    {
        if (!options.PaperPhaseValidation.Enabled)
            return null;
        if (!options.PaperPhaseValidation.InjectSyntheticOpportunity)
        {
            var disabled = BuildDisabledResult(options, "InjectSyntheticOpportunityFalse");
            Export(contentRootPath, disabled);
            return disabled;
        }

        lock (_gate)
        {
            if (_hasRun && options.PaperPhaseValidation.RunOnce)
                return null;
            _hasRun = true;
        }

        var result = Run(options, paper, positionBook, state, contentRootPath);
        Export(contentRootPath, result);
        Console.WriteLine($"[PAPER_PHASE_VALIDATION_RESULT] Passed={result.Passed.ToString().ToLowerInvariant()} PaperOpened={result.PaperOpened} DuplicateSuppressed={result.DuplicateSuppressed} LiveOrders={result.LiveOrders} SigningAttempts={result.SigningAttempts}");
        return result;
    }

    private static PaperPhaseValidationResult Run(
        TradingBotOptions options,
        PaperTradingEngine paper,
        PaperPositionBook positionBook,
        BotRuntimeState state,
        string contentRootPath)
    {
        var cashBefore = paper.Balance;
        var lockedBefore = paper.LockedCapital;
        var equityBefore = paper.Equity;
        const int liveOrders = 0;
        const int signingAttempts = 0;

        PaperPhaseValidationResult Fail(string stage, string reason)
        {
            Console.WriteLine($"[PAPER_PHASE_VALIDATION_FAILED] Stage={stage} Reason={reason}");
            return BuildResult(options, false, stage, reason, 0, 0, liveOrders, signingAttempts, cashBefore, paper.Balance, lockedBefore, paper.LockedCapital, equityBefore, paper.Equity, paper.RealizedPnl, positionBook, null);
        }

        if (options.PaperPhaseValidation.RequireExplicitConfigFlag && (!options.PaperPhaseValidation.Enabled || !options.PaperPhaseValidation.InjectSyntheticOpportunity))
            return Fail("ConfigBinding", "ExplicitValidationFlagsRequired");
        if (!options.PaperPhaseValidation.SyntheticOpportunityType.Equals("SingleMarketBuyBoth", StringComparison.OrdinalIgnoreCase))
            return Fail("SyntheticOpportunityCreation", "UnsupportedSyntheticOpportunityType");
        if (options.TradingMode.LiveTradingEnabled || options.EnableLiveExecution)
            return Fail("Safety", "LiveTradingEnabled");
        if (!options.TradingMode.PaperTradingEnabled || !options.EnablePaperTrading || !options.PaperOnly)
            return Fail("Safety", "PaperTradingNotEnabled");
        if (options.PaperPhaseValidation.MaxSyntheticPaperOpens < 1)
            return Fail("ConfigBinding", "MaxSyntheticPaperOpensBelowOne");

        var marketId = "__paper_phase_validation_single_market__";
        var question = "Paper Phase Validation Synthetic Market";
        var quantity = 12m;
        var yesPrice = 0.45m;
        var noPrice = 0.45m;
        var costPerShare = yesPrice + noPrice;
        var edgePerShare = 1m - costPerShare;
        var expectedProfit = quantity * edgePerShare;
        var opportunity = new ArbOpportunity(
            new ArbLeg(marketId, question, "YES", yesPrice, 100m),
            new ArbLeg(marketId, question, "NO", noPrice, 100m),
            quantity,
            costPerShare,
            edgePerShare,
            expectedProfit,
            1.0,
            "SingleMarketBuyBoth",
            "SingleMarketBuyBoth");

        Console.WriteLine($"[PAPER_VALIDATION_SYNTHETIC_OPPORTUNITY_CREATED] Type=SingleMarketBuyBoth MarketId={marketId} Qty={quantity:0.####} CostPerShare={costPerShare:0.####} EdgePerShare={edgePerShare:0.####} ExpectedProfit={expectedProfit:0.####}");

        var opened = paper.RecordArbitrage(opportunity);
        if (!opened)
            return Fail("PaperOpen", paper.BlockedCountsByReason.LastOrDefault().Key ?? "PaperOpenRejected");

        var openPosition = positionBook.GetOpenPositions().FirstOrDefault(p => p.GroupKey.Equals($"single-market:{marketId}", StringComparison.OrdinalIgnoreCase));
        if (openPosition is null)
            return Fail("PaperOpen", "OpenPositionMissing");

        var duplicateSuppressedBefore = paper.BlockedCountsByReason.TryGetValue("DuplicateOpenPosition", out var beforeDup) ? beforeDup : 0;
        var duplicateOpened = paper.RecordArbitrage(opportunity);
        var duplicateSuppressedAfter = paper.BlockedCountsByReason.TryGetValue("DuplicateOpenPosition", out var afterDup) ? afterDup : 0;
        var duplicateSuppressed = Math.Max(0, duplicateSuppressedAfter - duplicateSuppressedBefore);
        if (duplicateOpened)
            return Fail("DuplicateSuppression", "DuplicateOpenUnexpectedlyOpened");
        if (duplicateSuppressed < 1)
            return Fail("DuplicateSuppression", "DuplicateSuppressionNotRecorded");

        var accountConsistent = paper.Balance == cashBefore - openPosition.TotalCost
            && paper.LockedCapital == lockedBefore + openPosition.TotalCost
            && paper.Equity == equityBefore
            && paper.RealizedPnl == 0m
            && openPosition.Status == PaperPositionStatus.Open;
        if (!accountConsistent)
            return Fail("AccountingValidation", "PaperAccountInvariantFailed");

        state.ReplacePositions(positionBook.OpenPositions.Concat(positionBook.ClosedPositions).Take(200).Select(pz => new PaperPositionDto(pz.PositionId, pz.OpenedAtUtc, pz.ClosedAtUtc, pz.Strategy, pz.GroupKey, pz.Legs.Select(l => $"{l.Outcome}:{l.Question}").ToList(), pz.Quantity, pz.TotalCost, pz.CostPerBasket, pz.GuaranteedPayout, pz.Quantity * pz.Legs.Count, pz.GrossEdgeAtOpen, pz.NetEdgeAtOpen, pz.ExpectedProfit, pz.LockedCapital, pz.ActiveProfile, pz.Source, pz.CurrentNoAskSum, pz.CurrentExitValue, pz.UnrealizedPnl, pz.MtmStatus, pz.MissingExitPrices, pz.RealizedPayout, pz.RealizedProfit, pz.OpenedFromSimulatedFills, pz.FillSimulationId, pz.Status.ToString().ToUpperInvariant(), state.NextSeq())));
        state.AddSingleMarketExecution(new SingleMarketPaperExecutionDto(Guid.NewGuid().ToString("N"), DateTime.UtcNow, marketId, question, "SingleMarketBuyBoth", openPosition.Quantity, yesPrice, noPrice, openPosition.TotalCost, openPosition.EdgePerShare, openPosition.ExpectedProfit, paper.Balance, paper.LockedCapital, paper.Equity, "Opened", true));
        state.RecordPaperPretradeReject("DuplicateOpenPosition");
        state.SetPaperOpenCountLastHour(paper.HourlyOpenCount);
        state.SetStatus(new BotStatusDto("PAPER", true, "CONNECTED", paper.Balance, paper.LockedCapital, paper.Equity, paper.RealizedPnl, paper.ExpectedProfit, positionBook.OpenPositions.Count, state.Status.SignalCount, DateTime.UtcNow, DateTime.UtcNow));
        PaperAccountExporter.ExportLatest(Path.Combine(contentRootPath, "exports"), paper, positionBook, state.SingleMarketExecutions(), paper.BlockedCountsByReason);

        return BuildResult(options, true, "Complete", "Passed", 1, duplicateSuppressed, liveOrders, signingAttempts, cashBefore, paper.Balance, lockedBefore, paper.LockedCapital, equityBefore, paper.Equity, paper.RealizedPnl, positionBook, openPosition);
    }

    private static PaperPhaseValidationResult BuildResult(
        TradingBotOptions options,
        bool passed,
        string stage,
        string reason,
        int paperOpened,
        int duplicateSuppressed,
        int liveOrders,
        int signingAttempts,
        decimal cashBefore,
        decimal cashAfter,
        decimal lockedBefore,
        decimal lockedAfter,
        decimal equityBefore,
        decimal equityAfter,
        decimal realizedPnlAfter,
        PaperPositionBook positionBook,
        PaperPosition? position)
        => new(
            DateTime.UtcNow,
            options.PaperPhaseValidation.Enabled,
            passed,
            stage,
            reason,
            paperOpened,
            duplicateSuppressed,
            liveOrders,
            signingAttempts,
            cashBefore,
            cashAfter,
            lockedBefore,
            lockedAfter,
            equityBefore,
            equityAfter,
            realizedPnlAfter,
            positionBook.OpenTotalCost,
            positionBook.OpenPositions.Count,
            position?.Status.ToString());

    private static void Export(string contentRootPath, PaperPhaseValidationResult result)
    {
        var exports = Path.Combine(contentRootPath, "exports");
        Directory.CreateDirectory(exports);
        File.WriteAllText(Path.Combine(exports, "paper-phase-validation-latest.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    public const string SectionPath = $"{TradingBotOptions.SectionName}:PaperPhaseValidation";

    public static PaperPhaseValidationConfigDiagnostics BuildConfigDiagnostics(
        TradingBotOptions options,
        string environmentName,
        string contentRootPath,
        IEnumerable<object>? configurationSources = null,
        IEnumerable<string>? commandLineArgs = null)
    {
        var cfg = options.PaperPhaseValidation;
        return new PaperPhaseValidationConfigDiagnostics(
            SectionPath,
            cfg.Enabled,
            cfg.InjectSyntheticOpportunity,
            string.IsNullOrWhiteSpace(environmentName) ? "Unknown" : environmentName,
            string.IsNullOrWhiteSpace(contentRootPath) ? "Unknown" : contentRootPath,
            FormatLoadedConfigFiles(configurationSources),
            FormatCommandLineArgs(commandLineArgs));
    }

    public static void LogStartupConfig(
        TradingBotOptions options,
        string environmentName = "Unknown",
        string contentRootPath = "Unknown",
        IEnumerable<object>? configurationSources = null,
        IEnumerable<string>? commandLineArgs = null,
        bool failOnValidationConfigError = true)
    {
        var cfg = options.PaperPhaseValidation;
        var diagnostics = BuildConfigDiagnostics(options, environmentName, contentRootPath, configurationSources, commandLineArgs);
        Console.WriteLine($"[PAPER_PHASE_VALIDATION_CONFIG_SOURCE] SectionPath={diagnostics.SectionPath} Enabled={diagnostics.Enabled.ToString().ToLowerInvariant()} InjectSyntheticOpportunity={diagnostics.InjectSyntheticOpportunity.ToString().ToLowerInvariant()} Environment={diagnostics.Environment} ContentRoot={diagnostics.ContentRoot} LoadedConfigFiles={diagnostics.LoadedConfigFiles} CommandLineArgs={diagnostics.CommandLineArgs}");
        Console.WriteLine($"[PAPER_PHASE_VALIDATION_CONFIG] Enabled={cfg.Enabled.ToString().ToLowerInvariant()} InjectSyntheticOpportunity={cfg.InjectSyntheticOpportunity.ToString().ToLowerInvariant()} RunOnce={cfg.RunOnce.ToString().ToLowerInvariant()} MaxSyntheticPaperOpens={cfg.MaxSyntheticPaperOpens} RequireExplicitConfigFlag={cfg.RequireExplicitConfigFlag.ToString().ToLowerInvariant()}");
        if (string.Equals(environmentName, "Validation", StringComparison.OrdinalIgnoreCase) && !cfg.Enabled)
        {
            Console.WriteLine($"[PAPER_PHASE_VALIDATION_CONFIG_ERROR] Environment={environmentName} Reason=ValidationEnvironmentButFeatureDisabled SectionPath={SectionPath}");
            if (failOnValidationConfigError)
                throw new InvalidOperationException($"Paper phase validation environment is active but {SectionPath}:Enabled is false.");
        }
        if (!cfg.Enabled) Console.WriteLine("[PAPER_PHASE_VALIDATION_DISABLED] Reason=ConfigDisabled");
        else if (!cfg.InjectSyntheticOpportunity) Console.WriteLine("[PAPER_PHASE_VALIDATION_DISABLED] Reason=InjectSyntheticOpportunityFalse");
    }

    public const string TradingModeSectionPath = $"{TradingBotOptions.SectionName}:TradingMode";
    public const string PaperRiskSectionPath = $"{TradingBotOptions.SectionName}:PaperRisk";

    public static void LogPaperModeStartup(
        TradingBotOptions options,
        string environmentName = "Unknown",
        IEnumerable<object>? configurationSources = null,
        IEnumerable<string>? commandLineArgs = null)
    {
        var environment = string.IsNullOrWhiteSpace(environmentName) ? "Unknown" : environmentName;
        Console.WriteLine($"[PAPER_MODE_CONFIG_SOURCE] SectionPath={TradingModeSectionPath} PaperRiskPath={PaperRiskSectionPath} Environment={environment} LoadedConfigFiles={FormatLoadedConfigFiles(configurationSources)} CommandLineArgs={FormatCommandLineArgs(commandLineArgs)}");
        if (string.Equals(environment, "PaperPhase2", StringComparison.OrdinalIgnoreCase) && options.TradingMode.PaperPhase != 2)
        {
            Console.WriteLine($"[PAPER_MODE_CONFIG_ERROR] Environment=PaperPhase2 Reason=ExpectedPaperPhase2ButEffectivePhaseWas{options.TradingMode.PaperPhase}");
        }
        Console.WriteLine($"[PAPER_MODE] PaperTradingEnabled={options.TradingMode.PaperTradingEnabled.ToString().ToLowerInvariant()} PaperPhase={options.TradingMode.PaperPhase} LiveTrading={options.TradingMode.LiveTradingEnabled.ToString().ToLowerInvariant()} Validation={options.PaperPhaseValidation.Enabled.ToString().ToLowerInvariant()} MaxPaperNotionalPerTrade={options.PaperRisk.MaxPaperNotionalPerTrade:0.####} MaxPaperTotalExposure={options.PaperRisk.MaxPaperTotalExposure:0.####} MaxPaperOpenPerHour={options.PaperRisk.MaxPaperOpenPerHour}");
    }

    private static string FormatLoadedConfigFiles(IEnumerable<object>? configurationSources)
    {
        if (configurationSources is null) return "Unknown";
        var labels = configurationSources.Select(ConfigSourceLabel).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return labels.Length == 0 ? "None" : string.Join(",", labels);
    }

    private static string ConfigSourceLabel(object source)
    {
        if (source is string label) return label;
        var typeName = source.GetType().Name;
        var path = source.GetType().GetProperty("Path")?.GetValue(source)?.ToString();
        if (!string.IsNullOrWhiteSpace(path)) return path!;
        return typeName;
    }

    private static string FormatCommandLineArgs(IEnumerable<string>? commandLineArgs)
    {
        if (commandLineArgs is null) return "None";
        var args = commandLineArgs.ToArray();
        return args.Length == 0 ? "None" : string.Join(" ", args.Select(a => a.Replace(" ", "_", StringComparison.Ordinal)));
    }

    private static PaperPhaseValidationResult BuildDisabledResult(TradingBotOptions options, string reason)
        => new(DateTime.UtcNow, options.PaperPhaseValidation.Enabled, false, "ConfigBinding", reason, 0, 0, 0, 0, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m, 0, null);
}
