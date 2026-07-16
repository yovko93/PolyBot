using System.Text.Json;
using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed class PaperPhase1SyntheticCanaryService(TradingBotOptions options, PaperTradingEngine paper, PaperPositionBook book, string contentRootPath)
{
    private readonly object _gate = new();
    public static PaperPhase1SyntheticCanaryState Current { get; private set; } = new();
    private bool _summaryLogged;

    public void Tick(BotRuntimeState state)
    {
        lock (_gate)
        {
            EnsureConfigured();
            var cfg = options.PaperPhase1SyntheticCanary;
            if (Current.Opened && !Current.Settled && cfg.SettleSyntheticPosition && Current.OpenAttemptUtc.HasValue && DateTime.UtcNow - Current.OpenAttemptUtc.Value >= TimeSpan.FromSeconds(Math.Max(0, cfg.SettlementDelaySeconds)))
            {
                Settle(state);
            }

            if (cfg.RunOnce && Current.Attempted)
            {
                Export(state, CurrentPosition(), Current.Settled);
                LogSummaryIfComplete(state);
                return;
            }

            if (!IsProfileActive || !cfg.Enabled)
            {
                Current = Current with { RejectedReason = cfg.Enabled ? "ProfileNotActive" : "Disabled" };
                Export(state, null, false);
                return;
            }

            var h = RuntimeHealthSnapshot.From(state, options);
            var reason = Validate(h);
            Current = Current with { RejectedReason = reason };
            if (reason != "None")
            {
                Block(reason);
                Export(state, null, false);
                return;
            }

            var duplicateReason = DuplicateReason(state.ProcessRunId);
            if (duplicateReason != "None")
            {
                SuppressDuplicate(duplicateReason, state.ProcessRunId);
                Export(state, CurrentPosition(), Current.Settled);
                return;
            }

            Open(state);
        }
    }

    private void Open(BotRuntimeState state)
    {
        var cfg = options.PaperPhase1SyntheticCanary;
        Console.WriteLine($"[PAPER_PHASE1_CANARY_READY] Enabled=true Profile={options.RuntimeProfile} SyntheticOnly=true LiveTradingDisabled=true SigningDisabled=true ProcessRunId={state.ProcessRunId}");
        var id = $"CANARY-PHASE1-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        Current = Current with { Attempted = true, OpenAttempts = 1, CanaryOpenAttemptUtc = DateTime.UtcNow, OpenAttemptUtc = DateTime.UtcNow };
        var pos = paper.OpenSyntheticCanary(id, cfg.SyntheticMarketId, cfg.SyntheticYesTokenId, cfg.SyntheticNoTokenId, cfg.YesAsk, cfg.NoAsk, cfg.Quantity, cfg.Notional, cfg.ExpectedPayout, cfg.ExpectedProfit, cfg.RawEdge, cfg.AfterSafetyEdge, "CANARY-PHASE1-CANDIDATE", state.ProcessRunId, out var openReject);
        if (pos is null)
        {
            Block(openReject);
            Export(state, null, false);
            return;
        }

        Current = Current with { Opened = true, OpenSucceeded = 1, PaperOpened = 1, Rejected = false, RejectedReason = "None", PositionId = pos.PositionId, ExpectedProfit = cfg.ExpectedProfit, OpenPositions = book.OpenPositions.Count, Exposure = book.OpenPositions.Sum(x => x.TotalCost) };
        Console.WriteLine($"[PAPER_PHASE1_CANARY_OPENED] PositionId={pos.PositionId} RunOnce={cfg.RunOnce.ToString().ToLowerInvariant()} OpenAttempts={Current.OpenAttempts} Notional={cfg.Notional:0.####} ExpectedProfit={cfg.ExpectedProfit:0.####} AfterSafetyEdge={cfg.AfterSafetyEdge:0.####} SyntheticOnly=true ProcessRunId={state.ProcessRunId}");
        Export(state, pos, false);
    }

    private void Settle(BotRuntimeState state)
    {
        var cfg = options.PaperPhase1SyntheticCanary;
        var result = paper.SettlePositionDetailed(Current.PositionId, cfg.ExpectedPayout, "SyntheticCanary");
        if (!result.Accepted) return;
        Current = Current with { Settled = true, CanarySettledUtc = DateTime.UtcNow, PaperClosed = 1, OpenPositions = book.OpenPositions.Count, Exposure = book.OpenPositions.Sum(x => x.TotalCost), RealizedPnl = cfg.ExpectedProfit };
        Console.WriteLine($"[PAPER_PHASE1_CANARY_SETTLED] PositionId={Current.PositionId} PaperOpened={Current.PaperOpened} PaperClosed={Current.PaperClosed} OpenPositions={Current.OpenPositions} Exposure={Current.Exposure:0.####} RealizedPayout={cfg.ExpectedPayout:0.####} RealizedPnl={cfg.ExpectedProfit:0.####} ProcessRunId={state.ProcessRunId}");
        Export(state, result.Position, true);
        LogSummaryIfComplete(state);
    }

    private void EnsureConfigured()
    {
        if (Current.Configured && string.Equals(Current.ProcessRunId, ProcessRunContext.ProcessRunId, StringComparison.OrdinalIgnoreCase)) return;
        Current = new PaperPhase1SyntheticCanaryState(Configured: true, Enabled: options.PaperPhase1SyntheticCanary.Enabled, ProfileActive: IsProfileActive, RequireProfile: options.PaperPhase1SyntheticCanary.RequireProfile, RunOnce: options.PaperPhase1SyntheticCanary.RunOnce, RejectedReason: options.PaperPhase1SyntheticCanary.Enabled ? (IsProfileActive ? "None" : "ProfileNotActive") : "Disabled", ProcessRunId: ProcessRunContext.ProcessRunId);
        _summaryLogged = false;
    }

    private string DuplicateReason(string processRunId)
    {
        if (Current.Opened) return "CanaryRunOnceAlreadyOpened";
        if (!string.Equals(Current.PositionId, "None", StringComparison.OrdinalIgnoreCase)) return "CanaryPositionAlreadyExists";
        if (book.OpenPositions.Concat(book.ClosedPositions).Any(p => p.IsSyntheticCanary && string.Equals(p.ProcessRunId, processRunId, StringComparison.OrdinalIgnoreCase))) return "CanaryPositionAlreadyExists";
        if (Current.Attempted) return "CanaryRunOnceAlreadyAttempted";
        return "None";
    }

    private PaperPosition? CurrentPosition() => book.OpenPositions.Concat(book.ClosedPositions).FirstOrDefault(p => p.IsSyntheticCanary && string.Equals(p.PositionId, Current.PositionId, StringComparison.OrdinalIgnoreCase));
    private bool IsProfileActive => string.Equals(options.RuntimeProfile, RuntimeProfileService.ReducedDiagnosticsPaperPhase1Canary, StringComparison.OrdinalIgnoreCase);

    private string Validate(RuntimeHealthSnapshot h)
    {
        var cfg = options.PaperPhase1SyntheticCanary;
        if (!IsProfileActive) return "ProfileMismatch";
        if (!cfg.Enabled) return "Disabled";
        if (!cfg.RequireExplicitFlag) return "ExplicitFlagRequired";
        if (!h.PaperPhase1Armed || !h.PaperPhase1Readiness) return "PaperPhase1NotReady";
        if (options.TradingMode.LiveTradingEnabled || options.EnableLiveExecution) return "LiveTradingEnabled";
        if (LiveTradingGuard.SigningAttempts != 0) return "SigningAttemptsDetected";
        if (!string.Equals(options.PaperDiagnosticsLimited.AllowedStrategy, "SingleMarketBuyBoth", StringComparison.OrdinalIgnoreCase)) return "AllowedStrategyMismatch";
        if (!h.PaperDiagnosticsLimitedEligible) return "PaperDiagnosticsLimitedNotEligible";
        if (h.PaperOpenPositions != 0) return "PaperOpenPositionsNotZero";
        if (h.PaperTotalExposure != 0) return "PaperExposureNotZero";
        if (options.PaperDiagnosticsLimited.MaxOpenPositions != 1 || options.PaperDiagnosticsLimited.MaxPaperNotionalPerTrade != 5m || options.PaperDiagnosticsLimited.MaxPaperTotalExposure != 5m || options.PaperDiagnosticsLimited.MaxPaperOpensPerHour != 1) return "SafeLimitsMismatch";
        return "None";
    }

    private void Block(string reason)
    {
        Current = Current with { Attempted = true, Rejected = true, RejectedReason = reason };
        Console.WriteLine($"[PAPER_PHASE1_CANARY_BLOCKED] Reason={reason} ProcessRunId={ProcessRunContext.ProcessRunId}");
    }

    private void SuppressDuplicate(string reason, string processRunId)
    {
        Current = Current with { DuplicateSuppressions = Current.DuplicateSuppressions + 1, LastDuplicateReason = reason };
        Console.WriteLine($"[PAPER_PHASE1_CANARY_DUPLICATE_SUPPRESSED] Reason={reason} PositionId={Current.PositionId} ProcessRunId={processRunId}");
    }

    private void LogSummaryIfComplete(BotRuntimeState state)
    {
        if (_summaryLogged || !Current.Settled) return;
        _summaryLogged = true;
        Console.WriteLine($"[PAPER_PHASE1_CANARY_SUMMARY] Enabled={Current.Enabled.ToString().ToLowerInvariant()} ProfileActive={Current.ProfileActive.ToString().ToLowerInvariant()} RunOnce={Current.RunOnce.ToString().ToLowerInvariant()} Attempted={Current.Attempted.ToString().ToLowerInvariant()} Opened={Current.Opened.ToString().ToLowerInvariant()} Settled={Current.Settled.ToString().ToLowerInvariant()} DuplicateSuppressions={Current.DuplicateSuppressions} OpenAttempts={Current.OpenAttempts} OpenSucceeded={Current.OpenSucceeded} PaperOpened={Current.PaperOpened} PaperClosed={Current.PaperClosed} OpenPositions={book.OpenPositions.Count} Exposure={book.OpenPositions.Sum(x => x.TotalCost):0.####} RealizedPnl={Current.RealizedPnl:0.####} Consistent={Current.Consistent.ToString().ToLowerInvariant()} GlobalOpenAttempts={state.PaperExecutionsCount} GlobalOpenSucceeded={state.PaperExecutionsCount} GlobalPaperOpened={state.PaperExecutionsCount} GlobalPaperExecutions={state.PaperExecutionsCount} GlobalPaperClosed={state.PaperClosedPositions} GlobalOpenPositions={state.PaperOpenPositions} GlobalExposure={state.PaperTotalExposure:0.####} GlobalRealizedPnl={state.PaperRealizedPnl:0.####} ProcessRunId={state.ProcessRunId}");
    }

    private void Export(BotRuntimeState state, PaperPosition? position, bool settled)
    {
        var cfg = options.PaperPhase1SyntheticCanary;
        var openPositions = book.OpenPositions.Count;
        var closedPositions = book.ClosedPositions.Count;
        var exposure = book.OpenPositions.Sum(x => x.TotalCost);
        var globalConsistent = state.PaperExecutionsCount == state.PaperClosedPositions + state.PaperOpenPositions;
        var canary = new { openAttempts = Current.OpenAttempts, openSucceeded = Current.OpenSucceeded, paperOpened = Current.PaperOpened, paperClosed = Current.PaperClosed, openPositions, exposure, realizedPnl = Current.RealizedPnl, consistent = Current.Consistent };
        var globalPaperCounters = new { paperPhase1OpenAttempts = state.PaperExecutionsCount, paperPhase1OpenSucceeded = state.PaperExecutionsCount, paperPhase1PaperOpened = state.PaperExecutionsCount, paperDiagnosticsLimitedPaperOpened = state.PaperExecutionsCount, paperExecutions = state.PaperExecutionsCount, paperOpened = state.PaperExecutionsCount, paperClosed = closedPositions, paperOpenPositions = openPositions, paperExposure = exposure, paperRealizedPnl = paper.RealizedPnl, consistent = globalConsistent };
        var counterDedupe = new { sourceOfTruth = state.PaperCounterSourceOfTruth, duplicateSuppressions = state.PaperCounterDuplicateSuppressions, lastDuplicateExecutionId = state.PaperCounterLastDuplicateExecutionId, countedExecutionIds = state.PaperCounterCountedExecutionIds, syntheticCanaryCountedExecutions = state.PaperCounterSyntheticCanaryCountedExecutions, realScannerCountedExecutions = state.PaperCounterRealScannerCountedExecutions };
        var payload = new { generatedAtUtc = DateTime.UtcNow, processRunId = ProcessRunContext.ProcessRunId, enabled = Current.Enabled, profile = options.RuntimeProfile, attempted = Current.Attempted, opened = Current.Opened, settled = Current.Settled, rejected = Current.Rejected, rejectedReason = Current.RejectedReason, positionId = Current.PositionId, runOnce = cfg.RunOnce, attemptedThisRun = Current.Attempted, openedThisRun = Current.Opened, settledThisRun = Current.Settled, duplicateSuppressions = Current.DuplicateSuppressions, lastDuplicateReason = Current.LastDuplicateReason, openAttempts = Current.OpenAttempts, openSucceeded = Current.OpenSucceeded, paperOpened = Current.PaperOpened, paperClosed = Current.PaperClosed, openPositions, exposure, realizedPnl = Current.RealizedPnl, consistent = Current.Consistent && globalConsistent, consistencyReason = Current.ConsistencyReason, canary, globalPaperCounters, counterDedupe, candidate = new { candidateId = "CANARY-PHASE1-CANDIDATE", dedupeKey = $"CANARY-PHASE1-CANDIDATE|{ProcessRunContext.ProcessRunId}", marketId = cfg.SyntheticMarketId, isSyntheticCanary = true, strategy = "SingleMarketBuyBoth", yesAsk = cfg.YesAsk, noAsk = cfg.NoAsk, sumAsk = cfg.YesAsk + cfg.NoAsk, afterSafetyEdge = cfg.AfterSafetyEdge, notional = cfg.Notional, expectedProfit = cfg.ExpectedProfit }, position, settlement = new { scheduled = cfg.SettleSyntheticPosition, settlementDelaySeconds = cfg.SettlementDelaySeconds, settled = Current.Settled, realizedPnl = Current.RealizedPnl }, safety = new { liveTradingDisabled = true, signingDisabled = LiveTradingGuard.SigningAttempts == 0, realOrderSent = false, realMarketUsed = false, syntheticOnly = true } };
        var path = Path.IsPathRooted(cfg.ExportPath) ? cfg.ExportPath : Path.Combine(contentRootPath, cfg.ExportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}

public sealed record PaperPhase1SyntheticCanaryState(bool Configured = false, bool Enabled = false, bool ProfileActive = false, string RequireProfile = "ReducedDiagnosticsPaperPhase1Canary", bool RunOnce = true, bool Attempted = false, bool Opened = false, bool Settled = false, bool Rejected = false, string RejectedReason = "Disabled", string PositionId = "None", decimal ExpectedProfit = 0m, decimal RealizedPnl = 0m, int DuplicateSuppressions = 0, string LastDuplicateReason = "None", int OpenAttempts = 0, int OpenSucceeded = 0, int PaperOpened = 0, int PaperClosed = 0, int OpenPositions = 0, decimal Exposure = 0m, DateTime? CanaryOpenAttemptUtc = null, DateTime? CanarySettledUtc = null, DateTime? OpenAttemptUtc = null, string ProcessRunId = "")
{
    public bool SyntheticOnly => true;
    public bool RealOrderSent => false;
    public bool SigningAttempted => false;
    public int CanaryLadderOpenAttempted => OpenAttempts;
    public int CanaryLadderOpened => PaperOpened;
    public int CanaryLadderSettled => Settled ? 1 : 0;
    public bool Consistent => ConsistencyReason == "None";
    public string ConsistencyReason => DuplicateSuppressions > 0 && RunOnce ? "RunOnceViolation" : Opened && string.IsNullOrWhiteSpace(PositionId) ? "MissingPositionId" : Settled && (PaperClosed < 1 || OpenPositions != 0 || Exposure != 0m) ? "SettlementAccountingMismatch" : "None";
}
