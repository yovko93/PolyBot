using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TradingBot.Api;
using TradingBot.Options;

namespace TradingBot.Services;

/// <summary>Console presentation boundary. It never changes scanner, paper, or execution decisions.</summary>
public static class Phase1ConsoleLogging
{
    private static readonly object Sync = new();
    public const string AllowedEvents = "PHASE1_STARTUP|PHASE1_SUMMARY_5M|PHASE1_ALERT_CHANGE|PHASE1_PAPER_OPENED|PHASE1_PAPER_SETTLED|PHASE1_SAFETY_STOP|PHASE1_INVARIANT_FAILURE|ERROR|FATAL";
    private static readonly HashSet<string> ConsoleAllowlist = new(StringComparer.OrdinalIgnoreCase)
    {
        "PHASE1_STARTUP", "PHASE1_SUMMARY_5M", "PHASE1_ALERT_CHANGE", "PHASE1_PAPER_OPENED",
        "PHASE1_PAPER_SETTLED", "PHASE1_SAFETY_STOP", "PHASE1_INVARIANT_FAILURE", "ERROR", "FATAL"
    };
    private static ConsoleLoggingOptions _options = new();
    private static string _root = "";
    public static long SummaryLogsWritten { get; private set; }
    public static long VerboseEventsSuppressed { get; private set; }
    public static long VerboseEventsWritten { get; private set; }
    public static DateTime? LastSummaryUtc { get; private set; }
    public static DateTime? LastImmediateEventUtc { get; private set; }
    public static string LastWriteError { get; private set; } = "None";
    public static long UnexpectedVerboseEventsPrinted { get; private set; }
    public static long AlertNoopChangesSuppressed { get; private set; }
    public static string LastSuppressedAlertNoopReason { get; private set; } = "None";
    public static long StartupVerboseEventsSuppressed { get; private set; }
    public static string StartupVerboseEventNamesSuppressed { get { lock(Sync) return _startupSuppressedNames.Count == 0 ? "None" : string.Join('|', _startupSuppressedNames.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase)); } }
    public static bool RouterInitializedBeforeProfileLogging { get; private set; }
    public static bool RouterInitializedBeforeConfigLogging { get; private set; }
    public static long UnexpectedStartupEventsPrinted { get; private set; }
    public static int SummaryWindowSeconds => _options.SummaryIntervalSeconds;
    public static bool SummaryWindowCountersConsistent { get; private set; } = true;
    public static string SummaryWindowCountersReason { get; private set; } = "None";
    public static string UnexpectedVerboseEventNames => "None";
    public static bool StrictModeOk => UnexpectedVerboseEventsPrinted == 0 && UnexpectedStartupEventsPrinted == 0;
    private static AlertConsoleState? _lastAlert;
    private static bool _startupRouting;
    private static readonly HashSet<string> _startupSuppressedNames = new(StringComparer.OrdinalIgnoreCase);
    private static DateTime _summaryWindowStartUtc = DateTime.UtcNow;
    private static SummaryCounters _summaryBaseline = new();
    public static string Mode => _options.Mode;
    public static int SummaryIntervalSeconds => _options.SummaryIntervalSeconds;
    public static string VerboseLogPath => _options.VerboseLogPath;
    public static string SummaryLogPath => _options.SummaryLogPath;
    public static bool Consistent => StrictModeOk && SummaryWindowCountersConsistent && LastWriteError == "None" && (!IsSummary || !_options.SuppressVerboseEvents || !_options.WriteVerboseEventsToFile || VerboseEventsWritten == VerboseEventsSuppressed);
    public static string ConsistencyReason => Consistent ? "None" : !StrictModeOk ? "UnexpectedVerboseEventPrinted" : !SummaryWindowCountersConsistent ? SummaryWindowCountersReason : LastWriteError != "None" ? LastWriteError : "VerboseWriteCountMismatch";
    private static bool IsSummary => _options.Mode.Equals("Summary5Min", StringComparison.OrdinalIgnoreCase);

    public static TextWriter CreateWriter(TextWriter destination, TradingBotOptions options, string contentRoot)
    {
        _options = options.Console; _root = contentRoot; _lastAlert = null;
        _summaryWindowStartUtc=DateTime.UtcNow; _summaryBaseline=new();
        SummaryWindowCountersConsistent=true; SummaryWindowCountersReason="None";
        return new FilteringWriter(destination);
    }

    public static TextWriter CreateEarlyWriter(TextWriter destination, string[] args, IConfiguration configuration, string contentRoot)
    {
        var consoleOptions = new ConsoleLoggingOptions();
        configuration.GetSection($"{TradingBotOptions.SectionName}:Console").Bind(consoleOptions);
        var profile = Value(args, "--profile");
        if (string.Equals(profile, RuntimeProfileService.ReducedDiagnosticsPaperPhase1, StringComparison.OrdinalIgnoreCase)
            || string.Equals(profile, RuntimeProfileService.ReducedDiagnosticsPaperPhase1Canary, StringComparison.OrdinalIgnoreCase))
        { consoleOptions.Mode="Summary5Min"; consoleOptions.SummaryIntervalSeconds=300; }
        var mode = Value(args, "--console-mode");
        if (!string.IsNullOrWhiteSpace(mode)) consoleOptions.Mode=mode;
        var seconds = Value(args, "--console-summary-interval-seconds");
        if (!string.IsNullOrWhiteSpace(seconds) && int.TryParse(seconds,out var parsed) && parsed>0) consoleOptions.SummaryIntervalSeconds=parsed;
        _options=consoleOptions; _root=contentRoot; _startupRouting=true;
        RouterInitializedBeforeProfileLogging=true; RouterInitializedBeforeConfigLogging=true;
        return new FilteringWriter(destination);
    }

    public static void MarkStartupComplete() => _startupRouting=false;

    public static void EmitStartup(TextWriter destination, TradingBotOptions options)
    {
        if (!options.Console.EmitStartupBanner || options.Console.Mode.Equals("VerboseLegacy", StringComparison.OrdinalIgnoreCase)) return;
        LastImmediateEventUtc = DateTime.UtcNow;
        destination.WriteLine($"[PHASE1_STARTUP] Profile={options.RuntimeProfile} ConsoleMode={options.Console.Mode} SummaryIntervalSeconds={options.Console.SummaryIntervalSeconds} PaperOnly={options.PaperOnly.ToString().ToLowerInvariant()} ReadyForNormalRuntime=true LiveTradingDisabled={(!options.EnableLiveExecution&&!options.TradingMode.LiveTradingEnabled).ToString().ToLowerInvariant()} SigningDisabled=true ProcessRunId={ProcessRunContext.ProcessRunId}");
    }
    public static void EmitImmediate(TextWriter destination, string line) { LastImmediateEventUtc=DateTime.UtcNow; destination.WriteLine(line); }

    public static void ObserveAlert(TextWriter destination, int level, string name, string reason, string candidateId,
        decimal? edge, bool paperEligible, decimal minEdge, string processRunId)
    {
        var next = new AlertConsoleState(level, name, reason, candidateId, edge, paperEligible);
        var previous = _lastAlert;
        _lastAlert = next;
        if (previous is null) return; // Establish the startup baseline; startup is not an alert change.
        var stateChanged = previous.Level != next.Level
            || !string.Equals(previous.Name, next.Name, StringComparison.Ordinal)
            || !string.Equals(previous.Reason, next.Reason, StringComparison.Ordinal)
            || previous.PaperEligible != next.PaperEligible;
        var candidateThresholdCrossed = !string.Equals(previous.CandidateId, next.CandidateId, StringComparison.Ordinal)
            && CrossedThreshold(previous.Edge, next.Edge, 0m, minEdge);
        var waitingNoop = previous.Level == 0 && next.Level == 0 && !previous.PaperEligible && !next.PaperEligible
            && string.Equals(previous.Name,"WaitingForEdge",StringComparison.Ordinal)
            && string.Equals(next.Name,"WaitingForEdge",StringComparison.Ordinal)
            && string.Equals(previous.Reason,next.Reason,StringComparison.Ordinal)
            && (!next.Edge.HasValue || next.Edge.Value < minEdge);
        if (waitingNoop || (!stateChanged && !candidateThresholdCrossed))
        {
            AlertNoopChangesSuppressed++;
            LastSuppressedAlertNoopReason=waitingNoop ? "WaitingForEdgeUnchangedBelowMinEdge" : "AlertStateUnchanged";
            return;
        }
        EmitImmediate(destination, $"[PHASE1_ALERT_CHANGE] OldLevel={previous.Level} NewLevel={next.Level} Name={next.Name} Reason={next.Reason} CandidateId={next.CandidateId} Edge={next.Edge?.ToString("0.####") ?? "N/A"} PaperEligible={next.PaperEligible.ToString().ToLowerInvariant()} ProcessRunId={processRunId}");
    }

    public static void EmitSummary(TextWriter destination, RuntimeHealthSnapshot h, TradingBotOptions options)
    {
        if (!IsSummary) return;
        var now = DateTime.UtcNow;
        h.StrategyCounters.TryGetValue("SingleMarketBuyBoth",out var singleMarket);
        var totals = new SummaryCounters(singleMarket?.Candidates ?? h.PaperPhase1LadderSeen,
            singleMarket?.ExecutionReady ?? h.PaperPhase1LadderPaperEligible,
            singleMarket?.ValidPriced ?? h.PaperPhase1LadderValidPriced,
            singleMarket?.PositiveEdges ?? h.PaperPhase1LadderPositiveAfterSafety,
            PaperPhase1PositiveCaptureService.InvalidArtifactsObservedTotal);
        var reset = totals.GateCandidatesSeen < _summaryBaseline.GateCandidatesSeen
            || totals.GateEligible < _summaryBaseline.GateEligible
            || totals.LadderValidPriced < _summaryBaseline.LadderValidPriced
            || totals.LadderPositiveAfterSafety < _summaryBaseline.LadderPositiveAfterSafety
            || totals.InvalidArtifacts < _summaryBaseline.InvalidArtifacts;
        var window = reset ? totals : totals.Subtract(_summaryBaseline);
        var scopeMismatch = window.LadderValidPriced > window.GateCandidatesSeen;
        SummaryWindowCountersConsistent=!reset && !scopeMismatch;
        SummaryWindowCountersReason=reset ? "SourceCounterResetDetected" : scopeMismatch ? "LadderValidPricedExceedsGateCandidates" : "None";
        var windowStart = _summaryWindowStartUtc;
        var best = h.PaperPhase1RealWatchBestAfterSafetyEdge;
        var distance = h.PaperPhase1RealWatchBestDistanceToMinEdge;
        var normalStatus = h.PaperPhase1RealWatchWaitingForEdge ? "ReadyWaitingForEdge" : h.PaperPhase1RealWatchArmed ? "Ready" : "NotReady";
        var dashboardWarnings = DashboardWarningsService.Classify(h);
        var status = dashboardWarnings.Status(normalStatus);
        var dashboardWarningsExportOk = DashboardWarningsService.Export(_root, dashboardWarnings, now);
        var exportsOk = h.PaperPhase1ReleaseStatusExportWritten && h.PaperPhase1OperatorRunbookExportWritten;
        var noEdge = Phase1NoEdgeDiagnosticService.Current;
        var reconciliation = CleanPositiveToGateReconciliationService.Current;
        var alertFreshness = PaperPhase1RealReadinessMonitor.Current;
        var summary = new
        {
            tsUtc=now, processRunId=h.ProcessRunId, profile=options.RuntimeProfile, mode="PaperOnly", status,
            windowSeconds=options.Console.SummaryIntervalSeconds, windowStartUtc=windowStart, windowEndUtc=now,
            alert=new { level=h.PaperPhase1RealAlertLevel, name=h.PaperPhase1RealAlertName, reason=h.PaperPhase1RealAlertReason,
                fresh=alertFreshness.AlertFresh, ageSeconds=alertFreshness.AlertAgeSeconds, lastChangedUtc=alertFreshness.LastChangedUtc,
                lastCleanPositiveUtc=alertFreshness.AlertLastCleanPositiveUtc, staleSuppressedCount=alertFreshness.AlertStaleSuppressedCount,
                downgradeReason=alertFreshness.AlertDowngradeReason },
            edge=new { bestAfterSafety=best, distanceToMinEdge=distance, cleanNearOpen5m=reconciliation.CleanPositive5m,
                cleanNearOpenTtlCount=alertFreshness.CleanPositiveTtlCount, cleanNearOpenTotal=h.PaperPhase1PositiveCleanCapturesTotal,
                cleanPositiveTtlCount=alertFreshness.CleanPositiveTtlCount, cleanPositiveLastSeenUtc=alertFreshness.CleanPositiveLastSeenUtc,
                cleanPositiveAgeSeconds=alertFreshness.CleanPositiveAgeSeconds, cleanPositiveAlertFresh=alertFreshness.AlertFresh,
                cleanPositiveAlertTtlSeconds=alertFreshness.CleanPositiveAlertTtlSeconds,
                cleanPositiveTotal=h.PaperPhase1PositiveCleanCapturesTotal, paperEligible=h.PaperPhase1LadderPaperEligible },
            paper=new { opened=h.PaperPhase1PaperOpened, openPositions=h.PaperOpenPositions, realizedPnl=h.PaperRealizedPnl, normalRuntimeOpened=h.PaperPhase1NormalRuntimeOpened },
            safety=new { fixtureIsolationOk=h.PaperPhase1ContractFixtureIsolationOk, signingAttempts=h.SigningAttempts, liveTradingBlocked=h.LiveTradingBlockedCount, dashboardWarnings=dashboardWarnings.Total, dashboardWarningsBlocking=dashboardWarnings.Blocking, dashboardWarningsNonBlocking=dashboardWarnings.NonBlocking, dashboardWarningsLastReason=dashboardWarnings.LastReason, dashboardWarningsTopReason=dashboardWarnings.TopReason, dashboardWarningsByReason=dashboardWarnings.ByReason, dashboardWarningsAffectPaperPhase1=dashboardWarnings.AffectsPaperPhase1, dashboardWarningsAffectTradingSafety=dashboardWarnings.AffectsTradingSafety, dashboardWarningsConsistent=dashboardWarnings.Consistent },
            soak=new { stable=h.PaperPhase1RealSoakReadinessStable, orderbookStable=h.OrderbookStableNow, readinessStableMinutes=h.PaperPhase1RealSoakReadinessStableMinutes, orderbookStableMinutes=h.PaperPhase1RealSoakOrderbookStableMinutes },
            scanner5m=new { gateCandidatesSeen5m=window.GateCandidatesSeen, gateEligible5m=window.GateEligible,
                ladderValidPriced5m=window.LadderValidPriced, ladderPositiveAfterSafety5m=window.LadderPositiveAfterSafety,
                invalidArtifacts5m=window.InvalidArtifacts, candidatesSeen5m=window.GateCandidatesSeen,
                validPriced5m=window.LadderValidPriced, positiveAfterSafety5m=window.LadderPositiveAfterSafety,
                topReject=h.PaperPhase1LadderTopBlockingReason, topArtifact=h.PaperPhase1InvalidPositiveArtifactBestFirstReason,
                nearBreakEven5m=noEdge.NearBreakEven, shadowPositiveAtMinEdge0_5m=noEdge.ShadowPositiveAtMinEdge0, shadowPositiveAtMinEdge0_005_5m=noEdge.ShadowPositiveAtMinEdge005,
                bestRawEdge5m=noEdge.BestRawEdge, p99AfterSafety5m=noEdge.P99AfterSafetyEdge, noEdgeDiagnosis=noEdge.Diagnosis,
                reconciliation.CleanPositive5m, reconciliation.CleanPositiveToGate5m, reconciliation.CleanPositiveLostBeforeGate5m,
                reconciliation.CleanPositiveBelowMinAtGate5m, reconciliation.CleanPositiveStaleAtGate5m,
                reconciliation.CleanPositiveDepthRejected5m, reconciliation.CleanPositiveFillRejected5m,
                reconciliation.CleanPositiveRiskRejected5m, reconciliation.CleanPositivePaperEligible5m,
                reconciliation.CleanPositiveOpened5m, reconciliation.CleanPositiveBestLadderEdge5m,
                reconciliation.CleanPositiveBestGateEdge5m, reconciliation.CleanPositiveMaxEdgeDelta5m,
                reconciliation.CleanPositiveTopBlocker5m, reconciliation.P99AfterSafetySource,
                reconciliation.BestEdgeSource, reconciliation.P99CandidateId, reconciliation.BestGateCandidateId,
                reconciliation.P99CandidateReachedGate, reconciliation.P99CandidateGateBlocker,
                reconciliation.P99CandidateGateEdge, reconciliation.P99CandidateEdgeDeltaToGate },
            scannerTotals=new { gateCandidatesSeen=totals.GateCandidatesSeen, gateEligible=totals.GateEligible,
                ladderValidPriced=totals.LadderValidPriced, ladderPositiveAfterSafety=totals.LadderPositiveAfterSafety,
                invalidArtifacts=totals.InvalidArtifacts }, exportsOk
        };
        if (!AppendJson(options.Console.SummaryLogPath, summary)) return;
        SummaryLogsWritten++; LastSummaryUtc=now; _summaryBaseline=totals; _summaryWindowStartUtc=now;
        destination.WriteLine($"[PHASE1_SUMMARY_5M] TimeUtc={now:O} WindowSeconds={options.Console.SummaryIntervalSeconds} WindowStartUtc={windowStart:O} WindowEndUtc={now:O} Uptime={h.Uptime:c} Profile={options.RuntimeProfile} Mode=PaperOnly Status={status} Alert={h.PaperPhase1RealAlertLevel}/{h.PaperPhase1RealAlertName} AlertFresh={alertFreshness.AlertFresh.ToString().ToLowerInvariant()} AlertAgeSeconds={alertFreshness.AlertAgeSeconds?.ToString("0.##")??"N/A"} AlertLastChangedUtc={alertFreshness.LastChangedUtc:O} AlertLastCleanPositiveUtc={alertFreshness.AlertLastCleanPositiveUtc?.ToString("O")??"N/A"} AlertStaleSuppressedCount={alertFreshness.AlertStaleSuppressedCount} AlertDowngradeReason={alertFreshness.AlertDowngradeReason} BestEdge={best?.ToString("0.####")??"N/A"} DistanceToMinEdge={distance?.ToString("0.####")??"N/A"} CleanNearOpen5m={reconciliation.CleanPositive5m} CleanNearOpenTtlCount={alertFreshness.CleanPositiveTtlCount} CleanNearOpenTotal={h.PaperPhase1PositiveCleanCapturesTotal} CleanPositiveTtlCount={alertFreshness.CleanPositiveTtlCount} CleanPositiveLastSeenUtc={alertFreshness.CleanPositiveLastSeenUtc?.ToString("O")??"N/A"} CleanPositiveAgeSeconds={alertFreshness.CleanPositiveAgeSeconds?.ToString("0.##")??"N/A"} CleanPositiveAlertFresh={alertFreshness.AlertFresh.ToString().ToLowerInvariant()} CleanPositiveAlertTtlSeconds={alertFreshness.CleanPositiveAlertTtlSeconds} CleanPositiveTotal={h.PaperPhase1PositiveCleanCapturesTotal} PaperEligible={h.PaperPhase1LadderPaperEligible} PaperOpened={h.PaperPhase1PaperOpened} OpenPositions={h.PaperOpenPositions} RealizedPnl={h.PaperRealizedPnl:0.####} NormalRuntimeOpened={h.PaperPhase1NormalRuntimeOpened} FixtureIsolationOk={h.PaperPhase1ContractFixtureIsolationOk.ToString().ToLowerInvariant()} SoakStable={h.PaperPhase1RealSoakReadinessStable.ToString().ToLowerInvariant()} OrderbookStable={h.OrderbookStableNow.ToString().ToLowerInvariant()} DashboardWarnings={dashboardWarnings.Total} DashboardWarningsBlocking={dashboardWarnings.Blocking} DashboardWarningsNonBlocking={dashboardWarnings.NonBlocking} DashboardWarningsLastReason={Token(dashboardWarnings.LastReason)} DashboardWarningsTopReason={Token(dashboardWarnings.TopReason)} DashboardWarningsByReason={FormatReasons(dashboardWarnings.ByReason)} DashboardWarningsAffectPaperPhase1={dashboardWarnings.AffectsPaperPhase1.ToString().ToLowerInvariant()} DashboardWarningsAffectTradingSafety={dashboardWarnings.AffectsTradingSafety.ToString().ToLowerInvariant()} DashboardWarningsConsistent={dashboardWarnings.Consistent.ToString().ToLowerInvariant()} SigningAttempts={h.SigningAttempts} LiveTradingBlocked={h.LiveTradingBlockedCount} DiscoveryMode=ReducedUniverseDiagnosticsOnly ReducedUniverseMarkets={h.ReducedUniverseMarkets} GateCandidatesSeen5m={window.GateCandidatesSeen} GateEligible5m={window.GateEligible} LadderValidPriced5m={window.LadderValidPriced} LadderPositiveAfterSafety5m={window.LadderPositiveAfterSafety} InvalidArtifacts5m={window.InvalidArtifacts} CandidatesSeen5m={window.GateCandidatesSeen} ValidPriced5m={window.LadderValidPriced} PositiveAfterSafety5m={window.LadderPositiveAfterSafety} GateCandidatesSeenTotal={totals.GateCandidatesSeen} GateEligibleTotal={totals.GateEligible} LadderValidPricedTotal={totals.LadderValidPriced} LadderPositiveAfterSafetyTotal={totals.LadderPositiveAfterSafety} CandidatesSeenTotal={totals.GateCandidatesSeen} ValidPricedTotal={totals.LadderValidPriced} PositiveAfterSafetyTotal={totals.LadderPositiveAfterSafety} InvalidArtifactsTotal={totals.InvalidArtifacts} NearBreakEven5m={noEdge.NearBreakEven} ShadowPositiveAtMinEdge0_5m={noEdge.ShadowPositiveAtMinEdge0} ShadowPositiveAtMinEdge0_005_5m={noEdge.ShadowPositiveAtMinEdge005} BestRawEdge5m={noEdge.BestRawEdge?.ToString("0.####")??"N/A"} P99AfterSafety5m={noEdge.P99AfterSafetyEdge?.ToString("0.####")??"N/A"} NoEdgeDiagnosis={noEdge.Diagnosis} CleanPositive5m={reconciliation.CleanPositive5m} CleanPositiveToGate5m={reconciliation.CleanPositiveToGate5m} CleanPositiveLostBeforeGate5m={reconciliation.CleanPositiveLostBeforeGate5m} CleanPositiveBelowMinAtGate5m={reconciliation.CleanPositiveBelowMinAtGate5m} CleanPositiveStaleAtGate5m={reconciliation.CleanPositiveStaleAtGate5m} CleanPositiveDepthRejected5m={reconciliation.CleanPositiveDepthRejected5m} CleanPositiveFillRejected5m={reconciliation.CleanPositiveFillRejected5m} CleanPositiveRiskRejected5m={reconciliation.CleanPositiveRiskRejected5m} CleanPositivePaperEligible5m={reconciliation.CleanPositivePaperEligible5m} CleanPositiveOpened5m={reconciliation.CleanPositiveOpened5m} CleanPositiveBestLadderEdge5m={reconciliation.CleanPositiveBestLadderEdge5m?.ToString("0.####")??"N/A"} CleanPositiveBestGateEdge5m={reconciliation.CleanPositiveBestGateEdge5m?.ToString("0.####")??"N/A"} CleanPositiveMaxEdgeDelta5m={reconciliation.CleanPositiveMaxEdgeDelta5m?.ToString("0.####")??"N/A"} CleanPositiveTopBlocker5m={reconciliation.CleanPositiveTopBlocker5m} P99AfterSafetySource={reconciliation.P99AfterSafetySource} BestEdgeSource={reconciliation.BestEdgeSource} P99CandidateId={reconciliation.P99CandidateId} BestGateCandidateId={reconciliation.BestGateCandidateId} P99CandidateReachedGate={reconciliation.P99CandidateReachedGate.ToString().ToLowerInvariant()} P99CandidateGateBlocker={reconciliation.P99CandidateGateBlocker} P99CandidateGateEdge={reconciliation.P99CandidateGateEdge?.ToString("0.####")??"N/A"} P99CandidateEdgeDeltaToGate={reconciliation.P99CandidateEdgeDeltaToGate?.ToString("0.####")??"N/A"} TopReject={h.PaperPhase1LadderTopBlockingReason} TopArtifact={h.PaperPhase1InvalidPositiveArtifactBestFirstReason} ExportsOk={(exportsOk && dashboardWarningsExportOk).ToString().ToLowerInvariant()} ProcessRunId={h.ProcessRunId}");
    }

    public static string TelemetryFields() => $"ConsoleMode={Mode} ConsoleSummaryIntervalSeconds={SummaryIntervalSeconds} ConsoleSummaryWindowSeconds={SummaryWindowSeconds} ConsoleSummaryWindowCountersConsistent={SummaryWindowCountersConsistent.ToString().ToLowerInvariant()} ConsoleSummaryWindowCountersReason={SummaryWindowCountersReason} ConsoleSummaryLogsWritten={SummaryLogsWritten} ConsoleVerboseEventsSuppressed={VerboseEventsSuppressed} ConsoleVerboseEventsWritten={VerboseEventsWritten} ConsoleLastSummaryUtc={LastSummaryUtc:O} ConsoleLastImmediateEventUtc={LastImmediateEventUtc:O} ConsoleVerboseLogPath={VerboseLogPath} ConsoleSummaryLogPath={SummaryLogPath} ConsoleAllowedEvents={AllowedEvents} ConsoleUnexpectedVerboseEventsPrinted={UnexpectedVerboseEventsPrinted} ConsoleUnexpectedVerboseEventNames={UnexpectedVerboseEventNames} ConsoleAlertNoopChangesSuppressed={AlertNoopChangesSuppressed} ConsoleLastSuppressedAlertNoopReason={LastSuppressedAlertNoopReason} ConsoleStartupVerboseEventsSuppressed={StartupVerboseEventsSuppressed} ConsoleStartupVerboseEventNamesSuppressed={StartupVerboseEventNamesSuppressed} ConsoleRouterInitializedBeforeProfileLogging={RouterInitializedBeforeProfileLogging.ToString().ToLowerInvariant()} ConsoleRouterInitializedBeforeConfigLogging={RouterInitializedBeforeConfigLogging.ToString().ToLowerInvariant()} ConsoleUnexpectedStartupEventsPrinted={UnexpectedStartupEventsPrinted} ConsoleLoggingStrictModeOk={StrictModeOk.ToString().ToLowerInvariant()} ConsoleLoggingConsistent={Consistent.ToString().ToLowerInvariant()} ConsoleLoggingConsistencyReason={ConsistencyReason}";

    private static bool AppendJson(string relativePath, object value)
    {
        try { var path=Path.Combine(_root,relativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!); lock(Sync) File.AppendAllText(path,JsonSerializer.Serialize(value)+Environment.NewLine); LastWriteError="None"; return true; }
        catch(Exception ex) { LastWriteError=ex.GetType().Name+":"+ex.Message; return false; }
    }
    private static string Token(string value) => string.IsNullOrWhiteSpace(value) ? "None" : string.Concat(value.Select(c => char.IsWhiteSpace(c) ? '_' : c));
    private static string FormatReasons(IReadOnlyDictionary<string,long> reasons) => reasons.Count == 0 ? "None" : string.Join(',', reasons.OrderByDescending(x=>x.Value).ThenBy(x=>x.Key,StringComparer.OrdinalIgnoreCase).Select(x=>$"{Token(x.Key)}:{x.Value}"));
    private static string EventName(string line) { var start=line.IndexOf('['); var end=start<0?-1:line.IndexOf(']',start+1); return start>=0&&end>start?line[(start+1)..end]:"UNTAGGED"; }
    private static string? Value(string[] args,string name){for(var i=0;i<args.Length;i++){if(args[i].Equals(name,StringComparison.OrdinalIgnoreCase))return i+1<args.Length?args[i+1]:null;if(args[i].StartsWith(name+"=",StringComparison.OrdinalIgnoreCase))return args[i][(name.Length+1)..];}return null;}
    private static bool CrossedThreshold(decimal? oldEdge, decimal? newEdge, params decimal[] thresholds) => oldEdge.HasValue && newEdge.HasValue && thresholds.Any(t => (oldEdge.Value < t && newEdge.Value >= t) || (oldEdge.Value >= t && newEdge.Value < t));
    private static Dictionary<string,object?> Fields(string line)
    {
        var fields = new Dictionary<string,object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in line.Split(' ',StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(x=>x.Split('=',2)).Where(x=>x.Length==2)) fields[pair[0]]=pair[1];
        return fields;
    }

    private sealed class FilteringWriter(TextWriter destination) : TextWriter
    {
        public override Encoding Encoding => destination.Encoding;
        public override void WriteLine(string? value)
        {
            var line=value??string.Empty; var evt=EventName(line);
            if (_options.Mode.Equals("VerboseLegacy",StringComparison.OrdinalIgnoreCase)) { destination.WriteLine(line); return; }
            if (evt.Equals("PAPER_PHASE1_REAL_OPENED",StringComparison.OrdinalIgnoreCase)) { LastImmediateEventUtc=DateTime.UtcNow; destination.WriteLine(line.Replace("[PAPER_PHASE1_REAL_OPENED]","[PHASE1_PAPER_OPENED]").Replace("AfterSafetyEdge=","Edge=")); return; }
            if (evt.Equals("PAPER_PHASE1_REAL_SETTLED",StringComparison.OrdinalIgnoreCase)) { LastImmediateEventUtc=DateTime.UtcNow; destination.WriteLine(line.Replace("[PAPER_PHASE1_REAL_SETTLED]","[PHASE1_PAPER_SETTLED]").Replace("PaperOpenPositions=","OpenPositions=")); return; }
            var immediate=ConsoleAllowlist.Contains(evt) || evt.Contains("FATAL",StringComparison.OrdinalIgnoreCase)
                || evt.Contains("ERROR",StringComparison.OrdinalIgnoreCase) || line.StartsWith("fail:",StringComparison.OrdinalIgnoreCase)
                || line.StartsWith("crit:",StringComparison.OrdinalIgnoreCase);
            if (immediate)
            {
                if (_startupRouting && (evt is "PHASE1_SUMMARY_5M" or "PHASE1_ALERT_CHANGE" or "PHASE1_PAPER_OPENED" or "PHASE1_PAPER_SETTLED")) UnexpectedStartupEventsPrinted++;
                LastImmediateEventUtc=DateTime.UtcNow; destination.WriteLine(line); return;
            }
            VerboseEventsSuppressed++;
            if (_startupRouting) { StartupVerboseEventsSuppressed++; lock(Sync) _startupSuppressedNames.Add(evt); }
            if (_options.WriteVerboseEventsToFile && AppendJson(_options.VerboseLogPath,new { tsUtc=DateTime.UtcNow,processRunId=ProcessRunContext.ProcessRunId,@event=evt,level=line.Contains("WARN",StringComparison.OrdinalIgnoreCase)?"warning":"debug",fields=Fields(line) })) VerboseEventsWritten++;
        }
    }
    private sealed record AlertConsoleState(int Level, string Name, string Reason, string CandidateId, decimal? Edge, bool PaperEligible);
    private sealed record SummaryCounters(long GateCandidatesSeen=0, long GateEligible=0, long LadderValidPriced=0,
        long LadderPositiveAfterSafety=0, long InvalidArtifacts=0)
    {
        public SummaryCounters Subtract(SummaryCounters previous) => new(GateCandidatesSeen-previous.GateCandidatesSeen,
            GateEligible-previous.GateEligible, LadderValidPriced-previous.LadderValidPriced,
            LadderPositiveAfterSafety-previous.LadderPositiveAfterSafety, InvalidArtifacts-previous.InvalidArtifacts);
    }
}
