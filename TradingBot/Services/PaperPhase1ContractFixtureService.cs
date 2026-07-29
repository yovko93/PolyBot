using System.Text.Json;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record PaperPhase1ContractCandidate(
    string Strategy = "SingleMarketBuyBoth", string SourceKind = "RealScannerFixture",
    bool IsSyntheticCanary = false, bool TokenMappingVerified = true, bool HasBothBooks = true,
    bool HasYesAsk = true, bool HasNoAsk = true, bool SuspiciousYesNoAskSum = false,
    bool InvalidRawSpike = false, bool CandidateSnapshotMismatch = false, bool StaleOrderbook = false,
    decimal AfterSafetyEdge = .012m, decimal MinEdge = .01m, bool EdgeStable = true,
    bool DepthSufficient = true, bool FillPassed = true, bool RiskPassed = true,
    bool PaperDiagnosticsLimitedEligible = true, bool DuplicatePosition = false,
    bool OpenPositionsLimitPassed = true, bool ExposureLimitPassed = true,
    bool OpensPerHourLimitPassed = true);

public sealed record PaperPhase1ContractFixtureState(
    bool Enabled = false, bool DryReplayOnly = false, bool AllowPaperOpen = false,
    bool CleanPositive = false, bool RealWatchPositive = false, bool ExecutableLike = false,
    bool PaperEligible = false, int AlertLevel = 0, string AlertName = "WaitingForEdge",
    bool OpenAttempted = false, bool Opened = false, string PositionId = "None",
    string Reason = "Disabled", bool Consistent = true, string ConsistencyReason = "None",
    int RealScannerCountedExecutions = 0, int SyntheticCanaryCountedExecutions = 0,
    decimal Exposure = 0m, PaperPosition? Position = null)
{
    public string Classification => Reason is "MissingYesAsk" or "MissingNoAsk" or "MissingBook" or
        "TokenOutcomeMappingUnverified" or "SuspiciousYesNoAskSum" or "InvalidRawSpike" or
        "CandidateSnapshotMismatch" or "StaleOrderbook" ? "InvalidPositiveArtifact" :
        PaperEligible ? "PaperEligiblePositive" : "CleanCandidate";
}

/// <summary>
/// Explicit CLI/test-only contract harness. It never calls an exchange executor or signing path and is
/// deliberately separate from scanner orchestration. Replay evaluation can never create a position.
/// </summary>
public static class PaperPhase1ContractFixtureService
{
    public static PaperPhase1ContractFixtureState Current { get; private set; } = new();

    public static PaperPhase1ContractFixtureState Run(PaperPhase1ContractCandidate candidate, bool dryReplayOnly,
        bool allowPaperOpen, PaperPositionBook? positionBook = null)
    {
        var dataReasons = DataQualityReasons(candidate);
        var clean = dataReasons.Count == 0 && candidate.AfterSafetyEdge >= 0m;
        var watch = clean && candidate.TokenMappingVerified && candidate.HasBothBooks;
        var executable = watch && candidate.EdgeStable && candidate.DepthSufficient && candidate.FillPassed && candidate.RiskPassed;
        var eligible = executable && candidate.AfterSafetyEdge >= candidate.MinEdge && candidate.PaperDiagnosticsLimitedEligible
            && !candidate.DuplicatePosition && candidate.OpenPositionsLimitPassed && candidate.ExposureLimitPassed
            && candidate.OpensPerHourLimitPassed;
        var attempted = eligible && allowPaperOpen && !dryReplayOnly;
        PaperPosition? position = null;
        if (attempted)
            position = (positionBook ?? new PaperPositionBook(Path.Combine(Path.GetTempPath(), $"paper-phase1-fixture-{Guid.NewGuid():N}.csv")))
                .AddContractFixturePosition(candidate.AfterSafetyEdge, candidate.MinEdge, 4m);
        var opened = position is not null;
        var level = opened ? 5 : eligible ? 4 : 0;
        var reason = dryReplayOnly ? "DryReplayOnly" : opened ? "Opened" : dataReasons.FirstOrDefault() ?? (eligible ? "FixtureOpenNotAllowed" : "BelowMinEdge");
        var consistent = !candidate.IsSyntheticCanary && candidate.SourceKind == "RealScannerFixture"
            && (!dryReplayOnly || (!attempted && !opened)) && (!opened || (attempted && position!.TotalCost <= 5m && position.ExpectedProfit > 0m));
        Current = new(true, dryReplayOnly, allowPaperOpen, clean, watch, executable, eligible, level,
            opened ? "PaperOpened" : eligible ? "PaperEligibleDetected" : "WaitingForEdge", attempted, opened,
            position?.PositionId ?? "None", reason, consistent, consistent ? "None" : "FixtureContractMismatch",
            opened ? 1 : 0, 0, position?.TotalCost ?? 0m, position);
        return Current;
    }

    public static IReadOnlyList<string> DataQualityReasons(PaperPhase1ContractCandidate c)
    {
        var reasons = new List<string>();
        if (!c.HasYesAsk) reasons.Add("MissingYesAsk");
        if (!c.HasNoAsk) reasons.Add("MissingNoAsk");
        if (!c.HasBothBooks) reasons.Add("MissingBook");
        if (!c.TokenMappingVerified) reasons.Add("TokenOutcomeMappingUnverified");
        if (c.SuspiciousYesNoAskSum) reasons.Add("SuspiciousYesNoAskSum");
        if (c.InvalidRawSpike) reasons.Add("InvalidRawSpike");
        if (c.CandidateSnapshotMismatch) reasons.Add("CandidateSnapshotMismatch");
        if (c.StaleOrderbook) reasons.Add("StaleOrderbook");
        return reasons;
    }

    public static PaperPhase1ContractFixtureState RunFromCli(TradingBotOptions options, string root, bool dryReplayOnly, bool allowPaperOpen)
    {
        if (!string.Equals(options.RuntimeProfile, RuntimeProfileService.ReducedDiagnosticsPaperPhase1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Paper Phase 1 contract fixture requires ReducedDiagnosticsPaperPhase1.");
        if (options.EnableLiveExecution || options.TradingMode.LiveTradingEnabled)
            throw new InvalidOperationException("Paper Phase 1 contract fixture requires live trading disabled.");
        if (dryReplayOnly == allowPaperOpen)
            throw new InvalidOperationException("Select exactly one fixture mode: --dry-replay-only or --allow-fixture-paper-open.");
        var state = Run(new(), dryReplayOnly, allowPaperOpen, new PaperPositionBook(Path.Combine(root, "data/paper-phase1-contract-fixture.csv")));
        Export(root, state, new());
        Console.WriteLine($"[PAPER_PHASE1_CONTRACT_FIXTURE] PaperPhase1ContractFixtureEnabled=true PaperPhase1ContractFixtureDryReplayOnly={dryReplayOnly.ToString().ToLowerInvariant()} PaperPhase1ContractFixtureAllowPaperOpen={allowPaperOpen.ToString().ToLowerInvariant()} PaperPhase1PositiveCleanCapturesTotal=1 PaperPhase1PositiveRealWatchTotal=1 PaperPhase1PositiveExecutableLikeTotal=1 PaperPhase1PositivePaperEligibleTotal=1 PaperPhase1RealAlertLevel={state.AlertLevel} PaperPhase1RealAlertName={state.AlertName} PaperPhase1RealWatchOpenAttempts={(state.OpenAttempted ? 1 : 0)} PaperPhase1RealWatchOpenSucceeded={(state.Opened ? 1 : 0)} PaperPhase1RealWatchOpenedPositionId={state.PositionId} PaperPhase1OpenAttempts={(state.OpenAttempted ? 1 : 0)} PaperPhase1OpenSucceeded={(state.Opened ? 1 : 0)} PaperPhase1PaperOpened={(state.Opened ? 1 : 0)} PaperDiagnosticsLimitedPaperOpened={(state.Opened ? 1 : 0)} PaperOpened={(state.Opened ? 1 : 0)} PaperExecutions={(state.Opened ? 1 : 0)} PaperOpenPositions={(state.Opened ? 1 : 0)} PaperExposure={state.Exposure:0.####} PaperCounterRealScannerCountedExecutions={state.RealScannerCountedExecutions} PaperCounterSyntheticCanaryCountedExecutions=0 SigningAttempts=0 LiveTradingBlocked=0 ReplayOpened={state.Opened.ToString().ToLowerInvariant()} ReplayReason={state.Reason}");
        return state;
    }

    public static void Export(string root, PaperPhase1ContractFixtureState s, PaperPhase1ContractCandidate c)
    {
        var payload = new { enabled=s.Enabled, dryReplayOnly=s.DryReplayOnly, allowFixturePaperOpen=s.AllowPaperOpen,
            candidate=new { strategy=c.Strategy, sourceKind=c.SourceKind, isSyntheticCanary=c.IsSyntheticCanary, afterSafetyEdge=c.AfterSafetyEdge, minEdge=c.MinEdge, edgeStable=c.EdgeStable, depthSufficient=c.DepthSufficient, fillPassed=c.FillPassed, riskPassed=c.RiskPassed, paperEligible=s.PaperEligible },
            stageResult=new { classification=s.Classification, cleanPositive=s.CleanPositive, realWatchPositive=s.RealWatchPositive, executableLike=s.ExecutableLike, paperEligible=s.PaperEligible, alertLevel=s.AlertLevel, openAttempted=s.OpenAttempted, opened=s.Opened, positionId=s.PositionId, reason=s.Reason },
            safety=new { liveTradingDisabled=true, signingDisabled=true, liveOrderSent=false, signingAttempted=false }, consistent=s.Consistent };
        var directory=Path.Combine(root,"exports"); Directory.CreateDirectory(directory); var path=Path.Combine(directory,"paper-phase1-contract-fixture-latest.json"); var temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase})); File.Move(temp,path,true);
    }
}
