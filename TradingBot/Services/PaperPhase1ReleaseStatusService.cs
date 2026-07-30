using System.Text.Json;
using TradingBot.Api;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record PaperPhase1ReleaseState(bool Armed, bool Readiness, bool RealWatchEnabled, bool RealSoakEnabled,
    int AlertLevel, string AlertName, decimal? BestRealWatchAfterSafetyEdge, decimal? DistanceToMinEdge,
    long CleanNearOpenCount, long PaperEligiblePositiveCount);
public sealed record PaperPhase1ReleasePositions(int NormalRuntimeOpened, int NormalRuntimeClosed,
    int NormalRuntimeOpenPositions, int FixtureOpened, int FixtureClosed, int FixtureOpenPositions,
    int CanaryOpened, int CanaryClosed, int CanaryOpenPositions);
public sealed record PaperPhase1ReleaseSafety(bool FixtureIsolationOk, bool CanaryDisabled,
    bool LiveTradingDisabled, bool SigningDisabled, bool LimitsOk, decimal MinEdge,
    int MaxOpenPositions, decimal MaxNotional, decimal MaxExposure, int MaxOpensPerHour);
public sealed record PaperPhase1ReleaseFixture(bool Enabled, bool StaleFixtureResultIgnored, bool NormalRuntimeUnaffected);
public sealed record PaperPhase1ReleaseStatus(DateTime GeneratedAtUtc, string ProcessRunId, string Profile,
    string ReleaseMode, bool ReadyForNormalRuntime, PaperPhase1ReleaseState State,
    PaperPhase1ReleasePositions Positions, PaperPhase1ReleaseSafety Safety, PaperPhase1ReleaseFixture Fixture,
    bool Consistent, string ConsistencyReason, string[] DashboardWarnings,
    bool FixtureCandidateInjected = false, bool FixtureAffectsRuntime = false, bool FixtureIsolationOk = true);

public static class PaperPhase1ReleaseStatusService
{
    private static readonly object Sync = new();
    public static PaperPhase1ReleaseStatus Current { get; private set; } = Empty();

    public static void InitializeNormalRuntime(TradingBotOptions options, string root)
    {
        var health = RuntimeHealthSnapshot.From(new BotRuntimeState(options.RuntimeState, options.PaperCounterAudit), options);
        Update(health, root);
    }

    public static void Update(RuntimeHealthSnapshot h, string root)
    {
        var limitsOk = h.PaperPhase1MinEdge == .01m && h.PaperPhase1MaxOpenPositions == 1
            && h.PaperPhase1MaxNotionalPerTrade == 5m && h.PaperPhase1MaxTotalExposure == 5m
            && h.PaperPhase1MaxOpensPerHour == 1;
        var canaryDisabled = !h.PaperPhase1CanaryEnabled;
        var warnings = new List<string>();
        if (!h.PaperPhase1ContractFixtureIsolationOk) warnings.Add(h.PaperPhase1ContractFixtureIsolationReason);
        if (!canaryDisabled) warnings.Add("SyntheticCanaryEnabled");
        if (!h.PaperPhase1LiveTradingDisabled) warnings.Add("LiveTradingNotDisabled");
        if (!h.PaperPhase1SigningDisabled) warnings.Add("SigningAttemptsDetected");
        if (!limitsOk) warnings.Add("SafeLimitsMismatch");
        var consistent = warnings.Count == 0;
        var ready = h.PaperPhase1ProfileActive && h.PaperPhase1Armed && h.PaperPhase1Readiness
            && h.PaperPhase1RealWatchEnabled && h.PaperPhase1RealSoakEnabled && consistent;
        var status = new PaperPhase1ReleaseStatus(DateTime.UtcNow, h.ProcessRunId, h.RuntimeProfile, "PaperOnly", ready,
            new(h.PaperPhase1Armed, h.PaperPhase1Readiness, h.PaperPhase1RealWatchEnabled,
                h.PaperPhase1RealSoakEnabled, h.PaperPhase1RealAlertLevel, h.PaperPhase1RealAlertName,
                h.PaperPhase1RealAlertAfterSafetyEdge, h.PaperPhase1RealAlertDistanceToMinEdge,
                h.PaperPhase1CleanNearOpenCount, h.PaperPhase1PositivePaperEligibleTotal),
            new(h.PaperPhase1NormalRuntimeOpened, h.PaperPhase1NormalRuntimeClosed,
                h.PaperPhase1NormalRuntimeOpenPositions, h.PaperPhase1FixtureOpened,
                h.PaperPhase1FixtureClosed, h.PaperPhase1FixtureOpenPositions,
                h.PaperPhase1CanaryPaperOpened, h.PaperPhase1CanaryClosed, h.PaperPhase1CanaryOpenPositions),
            new(h.PaperPhase1ContractFixtureIsolationOk, canaryDisabled, h.PaperPhase1LiveTradingDisabled,
                h.PaperPhase1SigningDisabled, limitsOk, h.PaperPhase1MinEdge, h.PaperPhase1MaxOpenPositions,
                h.PaperPhase1MaxNotionalPerTrade, h.PaperPhase1MaxTotalExposure, h.PaperPhase1MaxOpensPerHour),
            new(h.PaperPhase1ContractFixtureEnabled, !h.PaperPhase1ContractFixtureEnabled, !h.PaperPhase1ContractFixtureAffectsRuntime),
            consistent, consistent ? "None" : string.Join('|', warnings), warnings.ToArray(),
            h.PaperPhase1ContractFixtureCandidateInjected, h.PaperPhase1ContractFixtureAffectsRuntime,
            h.PaperPhase1ContractFixtureIsolationOk);
        lock (Sync) { Current = status; Export(root); }
        Log(status);
    }

    public static void MarkFixtureLeak(string root, TradingBotOptions options)
    {
        var status = Empty() with { GeneratedAtUtc=DateTime.UtcNow, ProcessRunId=ProcessRunContext.ProcessRunId,
            Profile=options.RuntimeProfile, Consistent=false, ConsistencyReason="FixtureStateWithoutExplicitFlag",
            ReadyForNormalRuntime=false, FixtureAffectsRuntime=true, FixtureIsolationOk=false,
            DashboardWarnings=["FixtureStateWithoutExplicitFlag"] };
        lock (Sync) { Current=status; Export(root); }
    }

    private static void Export(string root)
    {
        var path=Path.Combine(root,"exports","paper-phase1-release-status-latest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp=path+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(Current,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
        File.Move(temp,path,true);
    }

    private static void Log(PaperPhase1ReleaseStatus x) => Console.WriteLine($"[PAPER_PHASE1_RELEASE_STATUS] Profile={x.Profile} ReleaseMode={x.ReleaseMode} ReadyForNormalRuntime={B(x.ReadyForNormalRuntime)} Armed={B(x.State.Armed)} Readiness={B(x.State.Readiness)} RealWatchEnabled={B(x.State.RealWatchEnabled)} RealSoakEnabled={B(x.State.RealSoakEnabled)} AlertLevel={x.State.AlertLevel} AlertName={x.State.AlertName} BestRealWatchAfterSafetyEdge={F(x.State.BestRealWatchAfterSafetyEdge)} DistanceToMinEdge={F(x.State.DistanceToMinEdge)} CleanNearOpenCount={x.State.CleanNearOpenCount} PaperEligiblePositiveCount={x.State.PaperEligiblePositiveCount} NormalRuntimeOpened={x.Positions.NormalRuntimeOpened} NormalRuntimeOpenPositions={x.Positions.NormalRuntimeOpenPositions} FixtureIsolationOk={B(x.Safety.FixtureIsolationOk)} CanaryDisabled={B(x.Safety.CanaryDisabled)} LiveTradingDisabled={B(x.Safety.LiveTradingDisabled)} SigningDisabled={B(x.Safety.SigningDisabled)} LimitsOk={B(x.Safety.LimitsOk)} MinEdge={x.Safety.MinEdge:0.####} MaxOpenPositions={x.Safety.MaxOpenPositions} MaxNotional={x.Safety.MaxNotional:0.####} MaxExposure={x.Safety.MaxExposure:0.####} MaxOpensPerHour={x.Safety.MaxOpensPerHour} DashboardWarnings={x.DashboardWarnings.Length} Consistent={B(x.Consistent)} ConsistencyReason={x.ConsistencyReason} ProcessRunId={x.ProcessRunId}");
    private static string B(bool value) => value.ToString().ToLowerInvariant();
    private static string F(decimal? value) => value?.ToString("0.####") ?? "N/A";
    private static PaperPhase1ReleaseStatus Empty() => new(DateTime.UtcNow, ProcessRunContext.ProcessRunId,
        "Unknown", "PaperOnly", false, new(false,false,false,false,0,"WaitingForEdge",null,null,0,0),
        new(0,0,0,0,0,0,0,0,0), new(true,true,true,true,true,.01m,1,5m,5m,1),
        new(false,true,true), true,"None",[]);
}
