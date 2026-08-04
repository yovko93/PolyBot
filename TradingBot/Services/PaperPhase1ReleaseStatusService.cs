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
public sealed record PaperPhase1ReleaseOperatorRunbook(string Status, string Mode, string WhatToWatch,
    string NormalCommand, string DryReplayCommand, string FixtureOpenSettleCommand, string StopCondition,
    string ExpectedCurrentAlert, string ExpectedPaperOpened);
public sealed record PaperPhase1ReleaseStatus(DateTime GeneratedAtUtc, string ProcessRunId, string Profile,
    string ReleaseMode, bool ReadyForNormalRuntime, PaperPhase1ReleaseState State,
    PaperPhase1ReleasePositions Positions, PaperPhase1ReleaseSafety Safety, PaperPhase1ReleaseFixture Fixture,
    bool Consistent, string ConsistencyReason, string[] DashboardWarnings,
    bool FixtureCandidateInjected = false, bool FixtureAffectsRuntime = false, bool FixtureIsolationOk = true,
    bool ReleaseStatusEnabled = true, int ReleaseStatusLogIntervalSeconds = 60,
    long ReleaseStatusLogsWritten = 0, long ReleaseStatusLogsSuppressed = 0, DateTime? ReleaseStatusLastEmittedUtc = null,
    string ReleaseStatusLastChangeReason = "Startup", bool ReleaseStatusConsistent = true,
    bool ReleaseStatusExportWritten = false, string ReleaseStatusExportPath = "exports/paper-phase1-release-status-latest.json",
    string ReleaseStatusLastWriteError = "None", DateTime? ReleaseStatusLastExportUtc = null,
    PaperPhase1ReleaseOperatorRunbook? OperatorRunbook = null);

public static class PaperPhase1ReleaseStatusService
{
    private const string ExportRelativePath = "exports/paper-phase1-release-status-latest.json";
    private static readonly object Sync = new();
    private static long _logsWritten;
    private static long _logsSuppressed;
    private static DateTime? _lastEmittedUtc;
    private static DateTime? _lastRunbookUtc;
    private static string? _lastChangeSignature;
    private static string _lastChangeReason = "Startup";
    public static PaperPhase1ReleaseStatus Current { get; private set; } = Empty();

    public static void InitializeNormalRuntime(TradingBotOptions options, string root)
    {
        var health = RuntimeHealthSnapshot.From(new BotRuntimeState(options.RuntimeState, options.PaperCounterAudit), options);
        Update(health, root, options);
    }

    public static void Update(RuntimeHealthSnapshot h, string root, TradingBotOptions? options = null)
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
        var releaseOptions = options?.PaperPhase1 ?? new PaperPhase1Options();
        var enabled = releaseOptions.ReleaseStatusEnabled;
        var intervalSeconds = Math.Max(1, releaseOptions.ReleaseStatusLogIntervalSeconds);
        var now = DateTime.UtcNow;
        var signature = string.Join('|', h.PaperPhase1RealAlertLevel, ready, h.PaperPhase1ContractFixtureIsolationOk,
            h.PaperPhase1NormalRuntimeOpenPositions, h.PaperPhase1PositivePaperEligibleTotal, warnings.Count, h.SigningAttempts, h.LiveTradingBlockedCount);
        var changeReason = ChangeReason(h, ready, warnings.Count, signature);
        var intervalElapsed = !_lastEmittedUtc.HasValue || now - _lastEmittedUtc.Value >= TimeSpan.FromSeconds(intervalSeconds);
        var stateChanged = _lastChangeSignature is null || !string.Equals(_lastChangeSignature, signature, StringComparison.Ordinal);
        var shouldWrite = enabled && (intervalElapsed || (releaseOptions.ReleaseStatusEmitOnChange && stateChanged));
        var shouldLog = shouldWrite && (!releaseOptions.ReleaseStatusSuppressDuplicates || intervalElapsed || stateChanged);
        if (!shouldWrite && releaseOptions.ReleaseStatusSuppressDuplicates) _logsSuppressed++;
        if (stateChanged) _lastChangeReason = changeReason;

        var status = new PaperPhase1ReleaseStatus(now, h.ProcessRunId, h.RuntimeProfile, "PaperOnly", ready,
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
            h.PaperPhase1ContractFixtureIsolationOk, enabled, intervalSeconds, _logsWritten, _logsSuppressed,
            _lastEmittedUtc, _lastChangeReason, consistent, Current.ReleaseStatusExportWritten, ExportRelativePath,
            Current.ReleaseStatusLastWriteError, Current.ReleaseStatusLastExportUtc, Runbook());
        lock (Sync)
        {
            Current = status;
            if (shouldWrite)
            {
                if (shouldLog)
                {
                    _logsWritten++;
                    _lastEmittedUtc = now;
                    _lastChangeSignature = signature;
                    Current = Current with { ReleaseStatusLogsWritten = _logsWritten, ReleaseStatusLastEmittedUtc = _lastEmittedUtc };
                }
                var result = Export(root);
                Current = Current with { ReleaseStatusExportWritten = result.Written, ReleaseStatusLastWriteError = result.Error, ReleaseStatusLastExportUtc = result.Written ? now : Current.ReleaseStatusLastExportUtc };
                LogExport(Current, result.Path, result.Written, result.Error);
                if (shouldLog) Log(Current);
            }
            if (!_lastRunbookUtc.HasValue || now - _lastRunbookUtc.Value >= TimeSpan.FromMinutes(10))
            {
                _lastRunbookUtc = now;
                LogRunbook(Current.OperatorRunbook ?? Runbook());
            }
        }
    }

    public static void MarkFixtureLeak(string root, TradingBotOptions options)
    {
        var status = Empty() with { GeneratedAtUtc=DateTime.UtcNow, ProcessRunId=ProcessRunContext.ProcessRunId,
            Profile=options.RuntimeProfile, Consistent=false, ConsistencyReason="FixtureStateWithoutExplicitFlag",
            ReadyForNormalRuntime=false, FixtureAffectsRuntime=true, FixtureIsolationOk=false,
            DashboardWarnings=["FixtureStateWithoutExplicitFlag"] };
        lock (Sync) { Current=status; var result=Export(root); Current=Current with { ReleaseStatusExportWritten=result.Written, ReleaseStatusLastWriteError=result.Error, ReleaseStatusLastExportUtc=result.Written ? DateTime.UtcNow : null }; LogExport(Current,result.Path,result.Written,result.Error); }
    }

    private static (bool Written, string Path, string Error) Export(string root)
    {
        var path=Path.Combine(root,ExportRelativePath);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temp=path+".tmp";
            File.WriteAllText(temp,JsonSerializer.Serialize(Current,new JsonSerializerOptions{WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}));
            File.Move(temp,path,true);
            return (true, ExportRelativePath, "None");
        }
        catch (Exception ex)
        {
            return (false, ExportRelativePath, ex.Message.Replace(' ', '_'));
        }
    }

    private static void Log(PaperPhase1ReleaseStatus x) => Console.WriteLine($"[PAPER_PHASE1_RELEASE_STATUS] Profile={x.Profile} ReleaseMode={x.ReleaseMode} ReadyForNormalRuntime={B(x.ReadyForNormalRuntime)} Armed={B(x.State.Armed)} Readiness={B(x.State.Readiness)} RealWatchEnabled={B(x.State.RealWatchEnabled)} RealSoakEnabled={B(x.State.RealSoakEnabled)} AlertLevel={x.State.AlertLevel} AlertName={x.State.AlertName} BestRealWatchAfterSafetyEdge={F(x.State.BestRealWatchAfterSafetyEdge)} DistanceToMinEdge={F(x.State.DistanceToMinEdge)} CleanNearOpenCount={x.State.CleanNearOpenCount} PaperEligiblePositiveCount={x.State.PaperEligiblePositiveCount} NormalRuntimeOpened={x.Positions.NormalRuntimeOpened} NormalRuntimeOpenPositions={x.Positions.NormalRuntimeOpenPositions} FixtureIsolationOk={B(x.Safety.FixtureIsolationOk)} CanaryDisabled={B(x.Safety.CanaryDisabled)} LiveTradingDisabled={B(x.Safety.LiveTradingDisabled)} SigningDisabled={B(x.Safety.SigningDisabled)} LimitsOk={B(x.Safety.LimitsOk)} MinEdge={x.Safety.MinEdge:0.####} MaxOpenPositions={x.Safety.MaxOpenPositions} MaxNotional={x.Safety.MaxNotional:0.####} MaxExposure={x.Safety.MaxExposure:0.####} MaxOpensPerHour={x.Safety.MaxOpensPerHour} DashboardWarnings={x.DashboardWarnings.Length} Consistent={B(x.Consistent)} ConsistencyReason={x.ConsistencyReason} ProcessRunId={x.ProcessRunId}");
    private static void LogExport(PaperPhase1ReleaseStatus x, string path, bool written, string error) => Console.WriteLine($"[PAPER_PHASE1_RELEASE_STATUS_EXPORT] Written={B(written)} Path={path} ProcessRunId={x.ProcessRunId} LastWriteError={error}");
    private static PaperPhase1ReleaseOperatorRunbook Runbook() => new("ReadyWaitingForEdge", "PaperOnly", "AlertLevel|PaperEligiblePositiveCount|PaperOpened|SigningAttempts|LiveTradingBlocked",
        "dotnet run --project TradingBot -- --profile ReducedDiagnosticsPaperPhase1",
        "dotnet run --project TradingBot -- --profile ReducedDiagnosticsPaperPhase1 --paper-phase1-contract-fixture --dry-replay-only",
        "dotnet run --project TradingBot -- --profile ReducedDiagnosticsPaperPhase1 --paper-phase1-contract-fixture --allow-fixture-paper-open --settle-fixture-paper-position",
        "SigningAttempts>0 or LiveTradingBlocked>0 or DashboardWarnings>0 or FixtureIsolationOk=false",
        "WaitingForEdge", "0 unless real PaperEligiblePositive appears");
    private static void LogRunbook(PaperPhase1ReleaseOperatorRunbook r) => Console.WriteLine($"[PAPER_PHASE1_OPERATOR_RUNBOOK] Status={r.Status} Mode={r.Mode} WhatToWatch={r.WhatToWatch} NormalCommand=\"{r.NormalCommand}\" DryReplayCommand=\"{r.DryReplayCommand}\" FixtureOpenSettleCommand=\"{r.FixtureOpenSettleCommand}\" StopCondition=\"{r.StopCondition}\" ExpectedCurrentAlert={r.ExpectedCurrentAlert} ExpectedPaperOpened=\"{r.ExpectedPaperOpened}\"");
    private static string ChangeReason(RuntimeHealthSnapshot h, bool ready, int dashboardWarnings, string signature) => _lastChangeSignature is null ? "Startup" :
        !string.Equals(_lastChangeSignature, signature, StringComparison.Ordinal) ? $"AlertLevel={h.PaperPhase1RealAlertLevel};ReadyForNormalRuntime={ready};FixtureIsolationOk={h.PaperPhase1ContractFixtureIsolationOk};NormalRuntimeOpenPositions={h.PaperPhase1NormalRuntimeOpenPositions};PaperEligiblePositiveCount={h.PaperPhase1PositivePaperEligibleTotal};DashboardWarnings={dashboardWarnings};SigningAttempts={h.SigningAttempts};LiveTradingBlocked={h.LiveTradingBlockedCount}" : "Interval";
    private static string B(bool value) => value.ToString().ToLowerInvariant();
    private static string F(decimal? value) => value?.ToString("0.####") ?? "N/A";
    private static PaperPhase1ReleaseStatus Empty() => new(DateTime.UtcNow, ProcessRunContext.ProcessRunId,
        "Unknown", "PaperOnly", false, new(false,false,false,false,0,"WaitingForEdge",null,null,0,0),
        new(0,0,0,0,0,0,0,0,0), new(true,true,true,true,true,.01m,1,5m,5m,1),
        new(false,true,true), true,"None",[], OperatorRunbook: Runbook());
}
