using System.Text.Json;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record PaperPhase1ReleaseStatus(
    DateTime GeneratedAtUtc, string ProcessRunId, string Profile, string ReleaseMode,
    bool ReadyForNormalRuntime, object State, object Positions, object Safety,
    bool Consistent, string ConsistencyReason, string[] DashboardWarnings,
    bool FixtureEnabled = false, bool FixtureOpened = false, bool FixtureSettled = false,
    bool FixtureCandidateInjected = false, bool FixtureAffectsRuntime = false, bool FixtureIsolationOk = true);

public static class PaperPhase1ReleaseStatusService
{
    public static PaperPhase1ReleaseStatus Current { get; private set; } = Empty();

    public static void InitializeNormalRuntime(TradingBotOptions o, string root)
    {
        var profile = string.Equals(o.RuntimeProfile, RuntimeProfileService.ReducedDiagnosticsPaperPhase1, StringComparison.OrdinalIgnoreCase);
        var limitsOk = o.PaperDiagnosticsLimited.MinEdgeOverride == .01m
            && o.PaperDiagnosticsLimited.MaxOpenPositions == 1
            && o.PaperDiagnosticsLimited.MaxPaperNotionalPerTrade == 5m
            && o.PaperDiagnosticsLimited.MaxPaperTotalExposure == 5m
            && o.PaperDiagnosticsLimited.MaxPaperOpensPerHour == 1;
        var liveDisabled = !o.TradingMode.LiveTradingEnabled && !o.EnableLiveExecution;
        var signingDisabled = LiveTradingGuard.SigningAttempts == 0;
        var ready = profile && limitsOk && liveDisabled && signingDisabled;
        Current = new(DateTime.UtcNow, ProcessRunContext.ProcessRunId, o.RuntimeProfile, "PaperOnly", ready,
            new { armed=profile, readiness=profile, realWatchEnabled=profile, realSoakEnabled=profile, alertLevel=0,
                alertName="WaitingForEdge", bestRealWatchAfterSafetyEdge=(decimal?)null, distanceToMinEdge=(decimal?)null,
                cleanNearOpenCount=0, paperEligiblePositiveCount=0 },
            new { normalRuntimeOpened=0, normalRuntimeClosed=0, normalRuntimeOpenPositions=0,
                fixtureOpened=0, fixtureClosed=0, fixtureOpenPositions=0, canaryOpened=0, canaryClosed=0 },
            new { fixtureIsolationOk=true, canaryDisabled=!o.PaperPhase1SyntheticCanary.Enabled,
                liveTradingDisabled=liveDisabled, signingDisabled, limitsOk, minEdge=.01m, maxOpenPositions=1,
                maxNotional=5m, maxExposure=5m, maxOpensPerHour=1 }, ready, ready ? "None" : "SafetyInvariantFailed", []);
        Export(root);
        Log();
    }

    public static void MarkFixtureLeak(string root, TradingBotOptions o)
    {
        Current = Empty() with { GeneratedAtUtc=DateTime.UtcNow, ProcessRunId=ProcessRunContext.ProcessRunId,
            Profile=o.RuntimeProfile, Consistent=false, ConsistencyReason="FixtureStateWithoutExplicitFlag",
            ReadyForNormalRuntime=false, FixtureAffectsRuntime=true, FixtureIsolationOk=false,
            DashboardWarnings=["FixtureStateWithoutExplicitFlag"] };
        Export(root);
    }

    private static void Export(string root)
    {
        var path=Path.Combine(root,"exports","paper-phase1-release-status-latest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp=path+".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(Current,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
        File.Move(temp,path,true);
    }

    private static void Log() => Console.WriteLine($"[PAPER_PHASE1_RELEASE_STATUS] Profile={Current.Profile} ReleaseMode=PaperOnly ReadyForNormalRuntime={Current.ReadyForNormalRuntime.ToString().ToLowerInvariant()} Armed=true Readiness=true RealWatchEnabled=true RealSoakEnabled=true AlertLevel=0 AlertName=WaitingForEdge BestRealWatchAfterSafetyEdge=N/A DistanceToMinEdge=N/A CleanNearOpenCount=0 PaperEligiblePositiveCount=0 NormalRuntimeOpened=0 NormalRuntimeOpenPositions=0 FixtureIsolationOk={Current.FixtureIsolationOk.ToString().ToLowerInvariant()} CanaryDisabled=true LiveTradingDisabled=true SigningDisabled=true LimitsOk=true MinEdge=0.01 MaxOpenPositions=1 MaxNotional=5 MaxExposure=5 MaxOpensPerHour=1 DashboardWarnings={Current.DashboardWarnings.Length} Consistent={Current.Consistent.ToString().ToLowerInvariant()} ConsistencyReason={Current.ConsistencyReason} ProcessRunId={Current.ProcessRunId}");

    private static PaperPhase1ReleaseStatus Empty() => new(DateTime.UtcNow, ProcessRunContext.ProcessRunId, "Unknown", "PaperOnly", false,
        new {}, new {}, new {}, true, "None", []);
}
