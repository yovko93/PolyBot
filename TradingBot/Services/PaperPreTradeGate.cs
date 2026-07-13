using TradingBot.Options;

namespace TradingBot.Services;

public enum PaperStrategyKind { SingleMarket, VerifiedBasket, Experimental }

public sealed record PaperPreTradeOpportunity(
    string Strategy,
    string MarketOrGroup,
    PaperStrategyKind StrategyKind,
    bool PaperOnly,
    decimal Notional,
    decimal ExpectedProfit,
    bool StableEdgePassed,
    bool ExecutionReadinessStablePassed,
    bool FillSimulationPassed,
    bool DataQualityPassed,
    bool DuplicatePositionOpen = false,
    bool CooldownActive = false,
    bool RepairSuggestedGroup = false,
    bool UnresolvedGroup = false,
    bool LockedOrQuarantinedGroup = false,
    bool DiagnosticsOnlyProfile = false);

public sealed record PaperAccountSnapshotForGate(
    decimal Cash,
    decimal TotalExposure,
    int OpenPositionsTotal,
    IReadOnlyDictionary<string, int> OpenPositionsByStrategy,
    int HourlyOpenCount);

public sealed record PaperPreTradeGateResult(bool Approved, string Reason)
{
    public static PaperPreTradeGateResult Ok { get; } = new(true, "Approved");
}

public sealed class PaperPreTradeGate(TradingBotOptions options)
{
    private readonly TradingBotOptions _options = options;

    public PaperPreTradeGateResult Validate(PaperPreTradeOpportunity opportunity, PaperAccountSnapshotForGate account)
    {
        var result = ValidateCore(opportunity, account);
        if (result.Approved)
            Console.WriteLine($"[PAPER_PRETRADE_APPROVED] Strategy={opportunity.Strategy} MarketOrGroup={opportunity.MarketOrGroup} Notional={opportunity.Notional:0.####}");
        else
            Console.WriteLine($"[PAPER_PRETRADE_REJECTED] Strategy={opportunity.Strategy} Reason={result.Reason} MarketOrGroup={opportunity.MarketOrGroup} Notional={opportunity.Notional:0.####}");
        return result;
    }

    private PaperPreTradeGateResult ValidateCore(PaperPreTradeOpportunity o, PaperAccountSnapshotForGate a)
    {
        if (!_options.TradingMode.PaperTradingEnabled || !_options.EnablePaperTrading) return Reject("PaperTradingDisabled");
        if (_options.TradingMode.LiveTradingEnabled || _options.EnableLiveExecution) return Reject("LiveTradingEnabled");
        if (_options.TradingMode.PaperPhase == 1)
        {
            var p = _options.PaperDiagnosticsLimited;
            if (!string.Equals(_options.RuntimeProfile, RuntimeProfileService.ReducedDiagnosticsPaperPhase1, StringComparison.OrdinalIgnoreCase)) return Reject("PaperPhase1ProfileNotActive");
            if (!p.Enabled) return Reject("PaperDiagnosticsLimitedDisabled");
            if (!string.Equals(o.Strategy, "SingleMarketBuyBoth", StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"[PAPER_PHASE1_CRITICAL_BLOCK] Reason=NonAllowedStrategyPaperAttempt Strategy={o.Strategy} ProcessRunId={TradingBot.Api.ProcessRunContext.ProcessRunId}"); return Reject("NonAllowedStrategyPaperAttempt"); }
            if (!string.Equals(p.AllowedStrategy, "SingleMarketBuyBoth", StringComparison.OrdinalIgnoreCase)) return Reject("AllowedStrategyMismatch");
            if (!_options.Strategies.TryGetValue("SingleMarketBuyBoth", out var singleMode) || singleMode.Mode != StrategyMode.PaperEligible) return Reject("SingleMarketModeNotPaperEligible");
            if (_options.Strategies.Where(x => !x.Key.Equals("SingleMarketBuyBoth", StringComparison.OrdinalIgnoreCase)).Any(x => x.Value.Mode == StrategyMode.PaperEligible)) return Reject("NonAllowedStrategyPaperEligible");
            if (a.OpenPositionsTotal >= p.MaxOpenPositions) return Reject("MaxOpenPositionsReached");
            if (a.TotalExposure + o.Notional > p.MaxPaperTotalExposure) return Reject("MaxPaperTotalExposureExceeded");
            if (o.Notional > p.MaxPaperNotionalPerTrade) return Reject("MaxPaperNotionalPerTradeExceeded");
            if (a.HourlyOpenCount >= p.MaxPaperOpensPerHour) return Reject("MaxPaperOpensPerHourReached");
            if (o.ExpectedProfit <= 0 || o.Notional <= 0 || (o.ExpectedProfit / o.Notional) < p.MinEdgeOverride) return Reject("BelowMinEdge");
            if (!o.StableEdgePassed) return Reject("EdgeUnstable");
            if (!o.FillSimulationPassed) return Reject("FillSimulationFailed");
            if (!o.DataQualityPassed) return Reject("DataQualityFailed");
            if (o.DuplicatePositionOpen) return Reject("DuplicateOpenPosition");
            if (o.CooldownActive) return Reject("CooldownActive");
        }
        if (!o.PaperOnly || !_options.PaperOnly) return Reject("PaperOnlyRequired");
        if (o.StrategyKind == PaperStrategyKind.SingleMarket && !_options.PaperRisk.AllowSingleMarketPaper) return Reject("StrategyNotAllowed");
        if (o.StrategyKind == PaperStrategyKind.VerifiedBasket && !_options.PaperRisk.AllowVerifiedBasketPaper) return Reject("StrategyNotAllowed");
        if (o.StrategyKind == PaperStrategyKind.Experimental && !_options.PaperRisk.AllowExperimentalPaper) return Reject("ExperimentalPaperDisabled");
        if (_options.PaperRisk.RequireStableEdge && !o.StableEdgePassed) return Reject("StableEdgeRequired");
        if (_options.PaperRisk.RequireExecutionReadinessStable && !o.ExecutionReadinessStablePassed) return Reject("ExecutionReadinessStableRequired");
        if (_options.PaperRisk.RequireFillSimulation && !o.FillSimulationPassed) return Reject("FillSimulationRequired");
        if (_options.PaperRisk.RequireDataQualityPass && !o.DataQualityPassed) return Reject("DataQualityFailed");
        if (o.DuplicatePositionOpen) return Reject("DuplicateOpenPosition");
        if (o.CooldownActive) return Reject("CooldownActive");
        if (a.HourlyOpenCount >= _options.PaperRisk.MaxPaperOpenPerHour) return Reject("MaxPaperOpenPerHourReached");
        if (a.OpenPositionsTotal >= _options.PaperRisk.MaxPaperPositionsTotal) return Reject("MaxPaperPositionsTotalReached");
        if (a.OpenPositionsByStrategy.TryGetValue(o.Strategy, out var byStrategy) && byStrategy >= _options.PaperRisk.MaxPaperPositionsPerStrategy) return Reject("MaxPaperPositionsPerStrategyReached");
        if (a.TotalExposure + o.Notional > _options.PaperRisk.MaxPaperTotalExposure) return Reject("MaxPaperTotalExposureExceeded");
        if (o.Notional > _options.PaperRisk.MaxPaperNotionalPerTrade) return Reject("MaxPaperNotionalPerTradeExceeded");
        if (a.Cash < o.Notional) return Reject("InsufficientPaperCash");
        if ((o.UnresolvedGroup || o.RepairSuggestedGroup) && !_options.PaperRisk.AllowRepairSuggestedGroups) return Reject("RepairSuggestedOrUnresolvedGroup");
        if (o.LockedOrQuarantinedGroup) return Reject("LockedOrQuarantinedGroup");
        if (o.DiagnosticsOnlyProfile) return Reject("DiagnosticsOnlyProfile");
        return PaperPreTradeGateResult.Ok;
    }

    private static PaperPreTradeGateResult Reject(string reason) => new(false, reason);
}
