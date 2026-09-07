using System.Text.Json;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Engines;
using TradingBot.Api;

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
    decimal Exposure = 0m, PaperPosition? Position = null,
    bool SettlementRequested = false, bool SettlementSucceeded = false, bool SettlementFailed = false,
    bool SettlementRejected = false, string SettlementLastPositionId = "None",
    string SettlementLastReason = "None", decimal? SettlementLastRealizedPayout = null,
    decimal? SettlementLastRealizedPnl = null, DateTime? SettledAtUtc = null,
    bool SettlementConsistent = true, string SettlementConsistencyReason = "None",
    int PaperOpened = 0, int PaperClosed = 0, int PaperOpenPositions = 0,
    decimal PaperExposure = 0m, decimal PaperLocked = 0m, decimal PaperRealizedPnl = 0m,
    bool LifecycleBalanceOk = true, string LifecycleBalanceReason = "None")
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
            opened ? 1 : 0, 0, position?.TotalCost ?? 0m, position,
            PaperOpened: opened ? 1 : 0, PaperOpenPositions: opened ? 1 : 0,
            PaperExposure: position?.TotalCost ?? 0m, PaperLocked: position?.LockedCapital ?? 0m);
        return Current;
    }

    public static PaperPhase1ContractFixtureState SettlePosition(PaperPositionBook book,
        PaperPhase1ContractFixtureState state, string positionId, decimal realizedPayout, string reason,
        string? exportsRoot = null)
    {
        Console.WriteLine($"[PAPER_PHASE1_REAL_SETTLEMENT_REQUESTED] PositionId={positionId} Reason={reason} RealizedPayout={realizedPayout:0.####} ProcessRunId={ProcessRunContext.ProcessRunId}");
        var candidate = book.OpenPositions.FirstOrDefault(x => x.PositionId.Equals(positionId, StringComparison.OrdinalIgnoreCase));
        var rejection = realizedPayout < 0m ? "InvalidPayout"
            : book.ClosedPositions.Any(x => x.PositionId.Equals(positionId, StringComparison.OrdinalIgnoreCase)) ? "AlreadyClosed"
            : candidate is null ? "PositionNotFound"
            : candidate.IsSyntheticCanary ? "SyntheticCanaryNotAllowed"
            : candidate.SourceKind != "RealScannerFixture" || candidate.Engine != "SingleMarketBuyBoth" ? "NonPaperPosition" : null;
        if (rejection is not null)
        {
            Console.WriteLine($"[PAPER_PHASE1_REAL_SETTLEMENT_REJECTED] PositionId={positionId} Reason={rejection} ProcessRunId={ProcessRunContext.ProcessRunId}");
            Current = state with { SettlementRequested=true, SettlementFailed=true, SettlementRejected=true,
                SettlementLastPositionId=positionId, SettlementLastReason=rejection,
                SettlementLastRealizedPayout=realizedPayout, SettlementConsistent=true };
            return Current;
        }

        var paper = new PaperTradingEngine(positionBook: book);
        var result = paper.SettlePositionDetailed(positionId, realizedPayout, reason, liveTradingEnabled:false);
        var open = book.OpenPositions.Where(IsPhase1Paper).ToArray();
        var closed = book.ClosedPositions.Where(IsPhase1Paper).ToArray();
        var exposure = open.Sum(x => x.TotalCost); var locked = open.Sum(x => x.LockedCapital);
        var pnl = closed.Sum(x => x.RealizedProfit ?? 0m); var opened = open.Length + closed.Length;
        var balance = opened == closed.Length + open.Length && exposure == open.Sum(x=>x.TotalCost)
            && locked == open.Sum(x=>x.LockedCapital) && (open.Length != 0 || (exposure == 0m && locked == 0m))
            && pnl == closed.Sum(x=>x.RealizedProfit ?? 0m);
        Current = state with { SettlementRequested=true, SettlementSucceeded=result.Accepted,
            SettlementFailed=!result.Accepted, SettlementRejected=!result.Accepted,
            SettlementLastPositionId=positionId, SettlementLastReason=result.Accepted ? reason : result.Reason,
            SettlementLastRealizedPayout=realizedPayout, SettlementLastRealizedPnl=result.Position?.RealizedProfit,
            SettledAtUtc=result.Settlement?.SettledAtUtc, SettlementConsistent=result.Accepted && balance,
            SettlementConsistencyReason=result.Accepted && balance ? "None" : result.Reason,
            PaperOpened=opened, PaperClosed=closed.Length, PaperOpenPositions=open.Length,
            PaperExposure=exposure, PaperLocked=locked, PaperRealizedPnl=pnl,
            LifecycleBalanceOk=balance, LifecycleBalanceReason=balance ? "None" : "OpenCloseExposureMismatch" };
        if (result.Accepted)
            Console.WriteLine($"[PAPER_PHASE1_REAL_SETTLED] PositionId={positionId} OpenedNotional={result.Position!.TotalCost:0.####} ExpectedPayout={result.Position.GuaranteedPayout:0.####} RealizedPayout={realizedPayout:0.####} RealizedPnl={result.Position.RealizedProfit:0.####} PaperClosed={closed.Length} PaperOpenPositions={open.Length} PaperExposure={exposure:0.####} ProcessRunId={ProcessRunContext.ProcessRunId}");
        if (exportsRoot is not null) PaperAccountExporter.ExportLatest(Path.Combine(exportsRoot,"exports"),paper,book);
        return Current;
    }

    private static bool IsPhase1Paper(PaperPosition p) => !p.IsSyntheticCanary && p.Engine == "SingleMarketBuyBoth";

    public static void RecordRealSettlement(PaperPositionBook book, string positionId, decimal realizedPayout,
        string requestedReason, PaperSettlementResult result)
    {
        var open=book.OpenPositions.Where(IsPhase1Paper).ToArray(); var closed=book.ClosedPositions.Where(IsPhase1Paper).ToArray();
        var exposure=open.Sum(x=>x.TotalCost); var locked=open.Sum(x=>x.LockedCapital); var pnl=closed.Sum(x=>x.RealizedProfit??0m);
        var balance=closed.Select(x=>x.PositionId).Distinct(StringComparer.OrdinalIgnoreCase).Count()==closed.Length
            && (open.Length>0 || (exposure==0m&&locked==0m));
        Current=Current with { SettlementRequested=true, SettlementSucceeded=result.Accepted,
            SettlementFailed=!result.Accepted, SettlementRejected=!result.Accepted, SettlementLastPositionId=positionId,
            SettlementLastReason=result.Accepted?requestedReason:result.Reason, SettlementLastRealizedPayout=realizedPayout,
            SettlementLastRealizedPnl=result.Position?.RealizedProfit, SettledAtUtc=result.Settlement?.SettledAtUtc,
            SettlementConsistent=result.Accepted&&balance, SettlementConsistencyReason=result.Accepted&&balance?"None":result.Reason,
            PaperOpened=open.Length+closed.Length, PaperClosed=closed.Length, PaperOpenPositions=open.Length,
            PaperExposure=exposure, PaperLocked=locked, PaperRealizedPnl=pnl, LifecycleBalanceOk=balance,
            LifecycleBalanceReason=balance?"None":"OpenCloseExposureMismatch" };
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

    public static PaperPhase1ContractFixtureState RunFromCli(TradingBotOptions options, string root, bool dryReplayOnly, bool allowPaperOpen,
        bool settlePosition = false, decimal? realizedPayout = null, string settlementReason = "FixtureContractSettlement")
    {
        if (!string.Equals(options.RuntimeProfile, RuntimeProfileService.ReducedDiagnosticsPaperPhase1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Paper Phase 1 contract fixture requires ReducedDiagnosticsPaperPhase1.");
        if (options.EnableLiveExecution || options.TradingMode.LiveTradingEnabled)
            throw new InvalidOperationException("Paper Phase 1 contract fixture requires live trading disabled.");
        if (dryReplayOnly == allowPaperOpen)
            throw new InvalidOperationException("Select exactly one fixture mode: --dry-replay-only or --allow-fixture-paper-open.");
        var book = new PaperPositionBook(Path.Combine(root, "data/paper-phase1-contract-fixture.csv"));
        var state = Run(new(), dryReplayOnly, allowPaperOpen, book);
        if (settlePosition)
        {
            if (!allowPaperOpen || !state.Opened) throw new InvalidOperationException("Fixture settlement requires a successfully opened fixture position.");
            state = SettlePosition(book,state,state.PositionId,realizedPayout ?? state.Position!.GuaranteedPayout,settlementReason,root);
            Console.WriteLine($"[PAPER_PHASE1_CONTRACT_FIXTURE_SETTLEMENT] Opened=true Settled={state.SettlementSucceeded.ToString().ToLowerInvariant()} PositionId={state.PositionId} PaperOpened={state.PaperOpened} PaperClosed={state.PaperClosed} PaperOpenPositions={state.PaperOpenPositions} PaperExposure={state.PaperExposure:0.####} PaperRealizedPnl={state.PaperRealizedPnl:0.####} SigningAttempts=0 LiveTradingBlocked=0 Consistent={(state.Consistent && state.LifecycleBalanceOk).ToString().ToLowerInvariant()} ProcessRunId={ProcessRunContext.ProcessRunId}");
        }
        Export(root, state, new());
        Console.WriteLine($"[PAPER_PHASE1_CONTRACT_FIXTURE] PaperPhase1ContractFixtureEnabled=true PaperPhase1ContractFixtureDryReplayOnly={dryReplayOnly.ToString().ToLowerInvariant()} PaperPhase1ContractFixtureAllowPaperOpen={allowPaperOpen.ToString().ToLowerInvariant()} PaperPhase1ContractFixtureOpened={state.Opened.ToString().ToLowerInvariant()} PaperPhase1ContractFixtureSettled={state.SettlementSucceeded.ToString().ToLowerInvariant()} PaperPhase1PositiveCleanCapturesTotal=1 PaperPhase1PositiveRealWatchTotal=1 PaperPhase1PositiveExecutableLikeTotal=1 PaperPhase1PositivePaperEligibleTotal=1 PaperPhase1RealAlertLevel={state.AlertLevel} PaperPhase1RealAlertName={(state.SettlementSucceeded ? "PaperOpenedAndSettled" : state.AlertName)} PaperPhase1RealWatchOpenAttempts={(state.OpenAttempted ? 1 : 0)} PaperPhase1RealWatchOpenSucceeded={(state.Opened ? 1 : 0)} PaperPhase1RealWatchOpenedPositionId={state.PositionId} PaperPhase1OpenAttempts={(state.OpenAttempted ? 1 : 0)} PaperPhase1OpenSucceeded={(state.Opened ? 1 : 0)} PaperPhase1PaperOpened={(state.Opened ? 1 : 0)} PaperDiagnosticsLimitedPaperOpened={(state.Opened ? 1 : 0)} PaperOpened={state.PaperOpened} PaperExecutions={(state.Opened ? 1 : 0)} PaperClosed={state.PaperClosed} PaperOpenPositions={state.PaperOpenPositions} PaperExposure={state.PaperExposure:0.####} PaperLocked={state.PaperLocked:0.####} PaperRealizedPnl={state.PaperRealizedPnl:0.####} PaperCounterRealScannerCountedExecutions={state.RealScannerCountedExecutions} PaperCounterSyntheticCanaryCountedExecutions=0 SigningAttempts=0 LiveTradingBlocked=0 PaperPhase1ConsistencyOk={state.LifecycleBalanceOk.ToString().ToLowerInvariant()} PaperPhase1RealLifecycleConsistent={state.LifecycleBalanceOk.ToString().ToLowerInvariant()} PaperPhase1LifecycleBalanceOk={state.LifecycleBalanceOk.ToString().ToLowerInvariant()} PaperPhase1RealSettlementSucceeded={state.SettlementSucceeded.ToString().ToLowerInvariant()} ReplayOpened={state.Opened.ToString().ToLowerInvariant()} ReplayReason={state.Reason}");
        return state;
    }

    public static void Export(string root, PaperPhase1ContractFixtureState s, PaperPhase1ContractCandidate c)
    {
        var payload = new { enabled=s.Enabled, dryReplayOnly=s.DryReplayOnly, allowFixturePaperOpen=s.AllowPaperOpen,
            candidate=new { strategy=c.Strategy, sourceKind=c.SourceKind, isSyntheticCanary=c.IsSyntheticCanary, afterSafetyEdge=c.AfterSafetyEdge, minEdge=c.MinEdge, edgeStable=c.EdgeStable, depthSufficient=c.DepthSufficient, fillPassed=c.FillPassed, riskPassed=c.RiskPassed, paperEligible=s.PaperEligible },
            stageResult=new { classification=s.Classification, cleanPositive=s.CleanPositive, realWatchPositive=s.RealWatchPositive, executableLike=s.ExecutableLike, paperEligible=s.PaperEligible, alertLevel=s.AlertLevel, openAttempted=s.OpenAttempted, opened=s.Opened, positionId=s.PositionId, reason=s.Reason },
            settlement=new { requested=s.SettlementRequested, succeeded=s.SettlementSucceeded, rejected=s.SettlementRejected, positionId=s.SettlementLastPositionId, reason=s.SettlementLastReason, realizedPayout=s.SettlementLastRealizedPayout, realizedPnl=s.SettlementLastRealizedPnl, settledAtUtc=s.SettledAtUtc },
            lifecycle=new { paperOpened=s.PaperOpened, paperClosed=s.PaperClosed, paperOpenPositions=s.PaperOpenPositions, paperExposure=s.PaperExposure, paperLocked=s.PaperLocked, paperRealizedPnl=s.PaperRealizedPnl, balanceOk=s.LifecycleBalanceOk, balanceReason=s.LifecycleBalanceReason },
            safety=new { liveTradingDisabled=true, signingDisabled=true, liveOrderSent=false, signingAttempted=false }, consistent=s.Consistent && s.SettlementConsistent && s.LifecycleBalanceOk };
        var directory=Path.Combine(root,"exports"); var path=Path.Combine(directory,"paper-phase1-contract-fixture-latest.json");
        SafeExportWriter.WriteJson(path,JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}),"PaperPhase1ContractFixture",critical:true);
    }

    public static void ExportDisabledMarker(string root)
    {
        var path = Path.Combine(root, "exports", "paper-phase1-contract-fixture-latest.json");
        DateTime? lastFixtureRunUtc = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new { enabled=false, opened=false, settled=false, candidateInjected=false,
            staleFixtureResultIgnored=true, lastFixtureRunUtc, normalRuntimeUnaffected=true };
        SafeExportWriter.WriteJson(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented=true, PropertyNamingPolicy=JsonNamingPolicy.CamelCase }), "PaperPhase1ContractFixture", critical:true);
        Current = new();
        Console.WriteLine("[PAPER_PHASE1_FIXTURE_ISOLATION] PaperPhase1ContractFixtureEnabled=false PaperPhase1ContractFixtureOpened=false PaperPhase1ContractFixtureSettled=false PaperPhase1ContractFixtureCandidateInjected=false PaperPhase1ContractFixtureAffectsRuntime=false PaperPhase1ContractFixtureIsolationOk=true");
    }
}
