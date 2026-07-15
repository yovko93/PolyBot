using System.Text.Json;
using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed class PaperPhase1SyntheticCanaryService(TradingBotOptions options, PaperTradingEngine paper, string contentRootPath)
{
    public static PaperPhase1SyntheticCanaryState Current { get; private set; } = new();
    private bool _attempted;
    private DateTime? _openedAtUtc;

    public void Tick(BotRuntimeState state)
    {
        if (!Current.Configured)
        {
            Current = Current with { Configured = true, Enabled = options.PaperPhase1SyntheticCanary.Enabled, ProfileActive = IsProfileActive, RequireProfile = options.PaperPhase1SyntheticCanary.RequireProfile, RejectedReason = options.PaperPhase1SyntheticCanary.Enabled ? (IsProfileActive ? "None" : "ProfileNotActive") : "Disabled" };
        }
        var h = RuntimeHealthSnapshot.From(state, options);
        if (!_attempted && IsProfileActive && options.PaperPhase1SyntheticCanary.Enabled)
        {
            var reason = Validate(h);
            Current = Current with { Configured = true, Enabled = true, ProfileActive = true, RequireProfile = options.PaperPhase1SyntheticCanary.RequireProfile, RejectedReason = reason };
            if (reason != "None") { Block(reason); Export(null, false); return; }
            Console.WriteLine($"[PAPER_PHASE1_CANARY_READY] Enabled=true Profile={options.RuntimeProfile} SyntheticOnly=true LiveTradingDisabled=true SigningDisabled=true ProcessRunId={state.ProcessRunId}");
            _attempted = true;
            var cfg = options.PaperPhase1SyntheticCanary;
            var id = $"CANARY-PHASE1-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            var pos = paper.OpenSyntheticCanary(id, cfg.SyntheticMarketId, cfg.SyntheticYesTokenId, cfg.SyntheticNoTokenId, cfg.YesAsk, cfg.NoAsk, cfg.Quantity, cfg.Notional, cfg.ExpectedPayout, cfg.ExpectedProfit, cfg.RawEdge, cfg.AfterSafetyEdge, "CANARY-PHASE1-CANDIDATE", state.ProcessRunId, out var openReject);
            if (pos is null) { Block(openReject); Export(null, false); return; }
            _openedAtUtc = DateTime.UtcNow;
            state.RecordPaperExecution();
            Current = Current with { Enabled = true, ProfileActive = true, Attempted = true, Opened = true, Rejected = false, RejectedReason = "None", PositionId = pos.PositionId, ExpectedProfit = cfg.ExpectedProfit };
            Console.WriteLine($"[PAPER_PHASE1_CANARY_OPENED] PositionId={pos.PositionId} Notional={cfg.Notional:0.####} ExpectedProfit={cfg.ExpectedProfit:0.####} AfterSafetyEdge={cfg.AfterSafetyEdge:0.####} SyntheticOnly=true ProcessRunId={state.ProcessRunId}");
            Export(pos, false);
        }
        if (Current.Opened && !Current.Settled && options.PaperPhase1SyntheticCanary.SettleSyntheticPosition && _openedAtUtc.HasValue && DateTime.UtcNow - _openedAtUtc.Value >= TimeSpan.FromSeconds(Math.Max(0, options.PaperPhase1SyntheticCanary.SettlementDelaySeconds)))
        {
            var result = paper.SettlePositionDetailed(Current.PositionId, options.PaperPhase1SyntheticCanary.ExpectedPayout, "SyntheticCanary");
            if (result.Accepted)
            {
                Current = Current with { Settled = true, RealizedPnl = options.PaperPhase1SyntheticCanary.ExpectedProfit };
                Console.WriteLine($"[PAPER_PHASE1_CANARY_SETTLED] PositionId={Current.PositionId} RealizedPayout={options.PaperPhase1SyntheticCanary.ExpectedPayout:0.####} RealizedPnl={options.PaperPhase1SyntheticCanary.ExpectedProfit:0.####} ProcessRunId={state.ProcessRunId}");
                Export(result.Position, true);
            }
        }
    }

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
    private void Block(string reason) { _attempted = options.PaperPhase1SyntheticCanary.RunOnce; Current = Current with { Configured = true, Enabled = options.PaperPhase1SyntheticCanary.Enabled, ProfileActive = IsProfileActive, RequireProfile = options.PaperPhase1SyntheticCanary.RequireProfile, Attempted = true, Rejected = true, RejectedReason = reason }; Console.WriteLine($"[PAPER_PHASE1_CANARY_BLOCKED] Reason={reason} ProcessRunId={ProcessRunContext.ProcessRunId}"); }
    private void Export(PaperPosition? position, bool settled)
    {
        var cfg = options.PaperPhase1SyntheticCanary;
        var payload = new { generatedAtUtc = DateTime.UtcNow, processRunId = ProcessRunContext.ProcessRunId, enabled = Current.Enabled, profile = options.RuntimeProfile, attempted = Current.Attempted, opened = Current.Opened, settled = Current.Settled, rejected = Current.Rejected, rejectedReason = Current.RejectedReason, positionId = Current.PositionId, candidate = new { candidateId = "CANARY-PHASE1-CANDIDATE", marketId = cfg.SyntheticMarketId, isSyntheticCanary = true, strategy = "SingleMarketBuyBoth", yesAsk = cfg.YesAsk, noAsk = cfg.NoAsk, sumAsk = cfg.YesAsk + cfg.NoAsk, afterSafetyEdge = cfg.AfterSafetyEdge, notional = cfg.Notional, expectedProfit = cfg.ExpectedProfit }, position, settlement = new { scheduled = cfg.SettleSyntheticPosition, settlementDelaySeconds = cfg.SettlementDelaySeconds, settled = Current.Settled, realizedPnl = Current.RealizedPnl }, safety = new { liveTradingDisabled = true, signingDisabled = LiveTradingGuard.SigningAttempts == 0, realOrderSent = false, realMarketUsed = false, syntheticOnly = true } };
        var path = Path.IsPathRooted(cfg.ExportPath) ? cfg.ExportPath : Path.Combine(contentRootPath, cfg.ExportPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }
}

public sealed record PaperPhase1SyntheticCanaryState(bool Configured = false, bool Enabled = false, bool ProfileActive = false, string RequireProfile = "ReducedDiagnosticsPaperPhase1Canary", bool Attempted = false, bool Opened = false, bool Settled = false, bool Rejected = false, string RejectedReason = "Disabled", string PositionId = "None", decimal ExpectedProfit = 0m, decimal RealizedPnl = 0m)
{
    public bool SyntheticOnly => true;
    public bool RealOrderSent => false;
    public bool SigningAttempted => false;
}
