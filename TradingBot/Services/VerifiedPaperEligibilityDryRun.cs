using TradingBot.Options;

namespace TradingBot.Services;

public enum VerifiedPaperBlockedReason { Stability, Risk, Fill, Depth, MissingNoAsk, CostProfile, DiagnosticsOnly, Unknown }

public sealed record VerifiedPaperEligibilityInput(
    string Group,
    decimal ActiveNet,
    decimal ConservativeNet,
    decimal RawNet,
    decimal PolymarketApproxNet,
    decimal Qty,
    decimal Notional,
    bool StabilityPassed,
    bool RiskPassed,
    bool FillPassed,
    bool DepthPassed,
    bool MissingNoAsk,
    bool OrderbookUnavailable,
    bool CostProfilePassed,
    StrategyMode Mode);

public sealed record VerifiedPaperEligibilityDecision(bool WouldOpenIfPaperEligible, VerifiedPaperBlockedReason BlockedReason)
{
    public string ToLogLine(VerifiedPaperEligibilityInput i)
        => $"[VERIFIED_PAPER_ELIGIBILITY_DRY_RUN] Group={i.Group} ActiveNet={i.ActiveNet:0.####} ConservativeNet={i.ConservativeNet:0.####} RawNet={i.RawNet:0.####} PolymarketApproxNet={i.PolymarketApproxNet:0.####} Qty={i.Qty:0.####} Notional={i.Notional:0.####} StabilityPassed={i.StabilityPassed.ToString().ToLowerInvariant()} RiskPassed={i.RiskPassed.ToString().ToLowerInvariant()} FillPassed={i.FillPassed.ToString().ToLowerInvariant()} DepthPassed={i.DepthPassed.ToString().ToLowerInvariant()} MissingNoAsk={i.MissingNoAsk.ToString().ToLowerInvariant()} OrderbookUnavailable={i.OrderbookUnavailable.ToString().ToLowerInvariant()} WouldOpenIfPaperEligible={WouldOpenIfPaperEligible.ToString().ToLowerInvariant()} BlockedReason={BlockedReason}";
}

public static class VerifiedPaperEligibilityDryRun
{
    public static VerifiedPaperEligibilityDecision Evaluate(VerifiedPaperEligibilityInput i)
    {
        if (!i.StabilityPassed) return new(false, VerifiedPaperBlockedReason.Stability);
        if (i.MissingNoAsk) return new(false, VerifiedPaperBlockedReason.MissingNoAsk);
        if (i.OrderbookUnavailable || !i.DepthPassed) return new(false, VerifiedPaperBlockedReason.Depth);
        if (!i.CostProfilePassed) return new(false, VerifiedPaperBlockedReason.CostProfile);
        if (!i.FillPassed) return new(false, VerifiedPaperBlockedReason.Fill);
        if (!i.RiskPassed) return new(false, VerifiedPaperBlockedReason.Risk);
        if (i.Mode == StrategyMode.DiagnosticsOnly) return new(true, VerifiedPaperBlockedReason.DiagnosticsOnly);
        return new(true, VerifiedPaperBlockedReason.Unknown);
    }

    public static bool CanOpenPaper(VerifiedPaperEligibilityDecision decision, OpportunityStrategyConfig config)
        => decision.WouldOpenIfPaperEligible && config.Mode == StrategyMode.PaperEligible && config.AllowPaperEligible;
}
