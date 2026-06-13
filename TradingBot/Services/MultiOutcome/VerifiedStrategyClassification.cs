using TradingBot.Options;

namespace TradingBot.Services.MultiOutcome;

public enum VerifiedStrategyClassificationKind
{
    ActiveConservativeExecutable,
    RawPositiveOnly,
    AlternateProfilePositive,
    ExperimentalProfileCandidate,
    NegativeEdge,
    Unresolved,
    MissingNoAsk
}

public sealed record VerifiedStrategyClassificationResult(
    string GroupKey,
    decimal ActiveNet,
    decimal RawNet,
    decimal AlternateProfileNet,
    decimal ConservativeNet,
    string CostProfile,
    VerifiedStrategyClassificationKind Classification,
    bool ActiveConservativePositive,
    bool ActiveConservativeExecutable,
    bool RawPositiveOnly,
    bool AlternateProfilePositive,
    bool ExperimentalProfileCandidate,
    bool WouldPassPaperRisk = false,
    bool WouldPassFill = false,
    bool WouldOpenIfPaperEligible = false,
    string Reason = "None")
{
    public bool DiagnosticsPositive => ActiveConservativePositive || RawPositiveOnly || AlternateProfilePositive || ExperimentalProfileCandidate;
    public bool DiagnosticsOnlyBlocked => ActiveConservativeExecutable && WouldOpenIfPaperEligible;
}

public static class VerifiedStrategyClassifier
{
    public static VerifiedStrategyClassificationResult MissingNoAsk(string groupKey, string reason = "MissingNoAsk")
        => new(groupKey, 0m, 0m, 0m, 0m, "Unknown", VerifiedStrategyClassificationKind.MissingNoAsk, false, false, false, false, false, Reason: reason);

    public static VerifiedStrategyClassificationResult Unresolved(string groupKey, string reason = "Unresolved")
        => new(groupKey, 0m, 0m, 0m, 0m, "Unknown", VerifiedStrategyClassificationKind.Unresolved, false, false, false, false, false, Reason: reason);

    public static VerifiedStrategyClassificationResult Classify(
        VerifiedBasketScreener.ScreenResult screen,
        MultiOutcomeArbitrageOptions options,
        bool stable = false,
        bool wouldPassPaperRisk = false,
        bool wouldPassFill = false,
        string? blockedReason = null)
    {
        var activeProfile = options.CostProfiles.ActiveProfile;
        var threshold = options.MinMultiOutcomeEdge;
        var rawNet = Net(screen, "RawOnly");
        var alternateNet = Net(screen, "PolymarketApprox");
        var conservativeNet = Net(screen, "Conservative");
        var activeNet = screen.ActiveProfileNetEdge;
        var activePositive = activeNet > threshold;
        var rawPositive = rawNet > threshold;
        var alternatePositive = alternateNet > threshold;
        var experimentalPositive = screen.ExperimentalProfileNetEdge > threshold;
        var activeExecutable = activePositive && screen.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.ExecutableUnderActiveProfile;

        VerifiedStrategyClassificationKind kind;
        var rawOnly = false;
        var alternateOnly = false;
        var experimental = false;
        var reason = blockedReason ?? "None";

        if (activeExecutable)
        {
            kind = VerifiedStrategyClassificationKind.ActiveConservativeExecutable;
            if (!stable) reason = "Stability";
            else if (!wouldPassPaperRisk) reason = "Risk";
            else if (!wouldPassFill) reason = "Fill";
        }
        else if (experimentalPositive && screen.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.ExperimentalPaperCandidate)
        {
            kind = VerifiedStrategyClassificationKind.ExperimentalProfileCandidate;
            experimental = true;
            reason = "ExperimentalProfile";
        }
        else if (alternatePositive)
        {
            kind = VerifiedStrategyClassificationKind.AlternateProfilePositive;
            alternateOnly = true;
            reason = "AlternateProfileOnly";
        }
        else if (rawPositive)
        {
            kind = VerifiedStrategyClassificationKind.RawPositiveOnly;
            rawOnly = true;
            reason = "RawOnly";
        }
        else
        {
            kind = VerifiedStrategyClassificationKind.NegativeEdge;
            reason = string.IsNullOrWhiteSpace(blockedReason) ? "NegativeEdge" : blockedReason!;
        }

        var wouldOpen = activeExecutable && stable && wouldPassPaperRisk && wouldPassFill;
        return new VerifiedStrategyClassificationResult(
            screen.GroupKey,
            activeNet,
            rawNet,
            alternateNet,
            conservativeNet,
            activeProfile,
            kind,
            activePositive,
            activeExecutable,
            rawOnly,
            alternateOnly,
            experimental,
            wouldPassPaperRisk,
            wouldPassFill,
            wouldOpen,
            reason);
    }

    private static decimal Net(VerifiedBasketScreener.ScreenResult screen, string profile)
        => screen.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals(profile, StringComparison.OrdinalIgnoreCase))?.NetEdge ?? decimal.MinValue;
}
