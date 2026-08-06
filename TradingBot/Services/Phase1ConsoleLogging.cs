using System.Text;
using System.Text.Json;
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
    public static string UnexpectedVerboseEventNames => "None";
    public static bool StrictModeOk => UnexpectedVerboseEventsPrinted == 0;
    private static AlertConsoleState? _lastAlert;
    public static string Mode => _options.Mode;
    public static int SummaryIntervalSeconds => _options.SummaryIntervalSeconds;
    public static string VerboseLogPath => _options.VerboseLogPath;
    public static string SummaryLogPath => _options.SummaryLogPath;
    public static bool Consistent => StrictModeOk && LastWriteError == "None" && (!IsSummary || !_options.SuppressVerboseEvents || !_options.WriteVerboseEventsToFile || VerboseEventsWritten == VerboseEventsSuppressed);
    public static string ConsistencyReason => Consistent ? "None" : !StrictModeOk ? "UnexpectedVerboseEventPrinted" : LastWriteError != "None" ? LastWriteError : "VerboseWriteCountMismatch";
    private static bool IsSummary => _options.Mode.Equals("Summary5Min", StringComparison.OrdinalIgnoreCase);

    public static TextWriter CreateWriter(TextWriter destination, TradingBotOptions options, string contentRoot)
    {
        _options = options.Console; _root = contentRoot; _lastAlert = null;
        return new FilteringWriter(destination);
    }

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
        if (!stateChanged && !candidateThresholdCrossed) { AlertNoopChangesSuppressed++; return; }
        EmitImmediate(destination, $"[PHASE1_ALERT_CHANGE] OldLevel={previous.Level} NewLevel={next.Level} Name={next.Name} Reason={next.Reason} CandidateId={next.CandidateId} Edge={next.Edge?.ToString("0.####") ?? "N/A"} PaperEligible={next.PaperEligible.ToString().ToLowerInvariant()} ProcessRunId={processRunId}");
    }

    public static void EmitSummary(TextWriter destination, RuntimeHealthSnapshot h, TradingBotOptions options)
    {
        if (!IsSummary) return;
        var now = DateTime.UtcNow;
        var best = h.PaperPhase1RealWatchBestAfterSafetyEdge;
        var distance = h.PaperPhase1RealWatchBestDistanceToMinEdge;
        var status = h.PaperPhase1RealWatchWaitingForEdge ? "ReadyWaitingForEdge" : h.PaperPhase1RealWatchArmed ? "Ready" : "NotReady";
        var exportsOk = h.PaperPhase1ReleaseStatusExportWritten && h.PaperPhase1OperatorRunbookExportWritten;
        var summary = new
        {
            tsUtc=now, processRunId=h.ProcessRunId, profile=options.RuntimeProfile, mode="PaperOnly", status,
            alert=new { level=h.PaperPhase1RealAlertLevel, name=h.PaperPhase1RealAlertName, reason=h.PaperPhase1RealAlertReason },
            edge=new { bestAfterSafety=best, distanceToMinEdge=distance, cleanNearOpen=h.PaperPhase1CleanNearOpenCount, paperEligible=h.PaperPhase1LadderPaperEligible },
            paper=new { opened=h.PaperPhase1PaperOpened, openPositions=h.PaperOpenPositions, realizedPnl=h.PaperRealizedPnl, normalRuntimeOpened=h.PaperPhase1NormalRuntimeOpened },
            safety=new { fixtureIsolationOk=h.PaperPhase1ContractFixtureIsolationOk, signingAttempts=h.SigningAttempts, liveTradingBlocked=h.LiveTradingBlockedCount, dashboardWarnings=h.DiagnosticsCounterMismatchCount },
            soak=new { stable=h.PaperPhase1RealSoakReadinessStable, orderbookStable=h.OrderbookStableNow, readinessStableMinutes=h.PaperPhase1RealSoakReadinessStableMinutes, orderbookStableMinutes=h.PaperPhase1RealSoakOrderbookStableMinutes },
            scanner5m=new { candidatesSeen=h.PaperPhase1CandidatesSeen, validPriced=h.PaperPhase1LadderValidPriced, positiveAfterSafety=h.PaperPhase1LadderPositiveAfterSafety, invalidArtifacts=h.PaperPhase1InvalidPositiveArtifactsTotal, topReject=h.PaperPhase1LadderTopBlockingReason, topArtifact=h.PaperPhase1InvalidPositiveArtifactBestFirstReason }, exportsOk
        };
        if (!AppendJson(options.Console.SummaryLogPath, summary)) return;
        SummaryLogsWritten++; LastSummaryUtc=now;
        destination.WriteLine($"[PHASE1_SUMMARY_5M] TimeUtc={now:O} Uptime={h.Uptime:c} Profile={options.RuntimeProfile} Mode=PaperOnly Status={status} Alert={h.PaperPhase1RealAlertLevel}/{h.PaperPhase1RealAlertName} BestEdge={best?.ToString("0.####")??"N/A"} DistanceToMinEdge={distance?.ToString("0.####")??"N/A"} CleanNearOpen={h.PaperPhase1CleanNearOpenCount} PaperEligible={h.PaperPhase1LadderPaperEligible} PaperOpened={h.PaperPhase1PaperOpened} OpenPositions={h.PaperOpenPositions} RealizedPnl={h.PaperRealizedPnl:0.####} NormalRuntimeOpened={h.PaperPhase1NormalRuntimeOpened} FixtureIsolationOk={h.PaperPhase1ContractFixtureIsolationOk.ToString().ToLowerInvariant()} SoakStable={h.PaperPhase1RealSoakReadinessStable.ToString().ToLowerInvariant()} OrderbookStable={h.OrderbookStableNow.ToString().ToLowerInvariant()} DashboardWarnings={h.DiagnosticsCounterMismatchCount} SigningAttempts={h.SigningAttempts} LiveTradingBlocked={h.LiveTradingBlockedCount} DiscoveryMode=ReducedUniverseDiagnosticsOnly ReducedUniverseMarkets={h.ReducedUniverseMarkets} CandidatesSeen5m={h.PaperPhase1CandidatesSeen} ValidPriced5m={h.PaperPhase1LadderValidPriced} PositiveAfterSafety5m={h.PaperPhase1LadderPositiveAfterSafety} InvalidArtifacts5m={h.PaperPhase1InvalidPositiveArtifactsTotal} TopReject={h.PaperPhase1LadderTopBlockingReason} TopArtifact={h.PaperPhase1InvalidPositiveArtifactBestFirstReason} ExportsOk={exportsOk.ToString().ToLowerInvariant()} ProcessRunId={h.ProcessRunId}");
    }

    public static string TelemetryFields() => $"ConsoleMode={Mode} ConsoleSummaryIntervalSeconds={SummaryIntervalSeconds} ConsoleSummaryLogsWritten={SummaryLogsWritten} ConsoleVerboseEventsSuppressed={VerboseEventsSuppressed} ConsoleVerboseEventsWritten={VerboseEventsWritten} ConsoleLastSummaryUtc={LastSummaryUtc:O} ConsoleLastImmediateEventUtc={LastImmediateEventUtc:O} ConsoleVerboseLogPath={VerboseLogPath} ConsoleSummaryLogPath={SummaryLogPath} ConsoleAllowedEvents={AllowedEvents} ConsoleUnexpectedVerboseEventsPrinted={UnexpectedVerboseEventsPrinted} ConsoleUnexpectedVerboseEventNames={UnexpectedVerboseEventNames} ConsoleAlertNoopChangesSuppressed={AlertNoopChangesSuppressed} ConsoleLoggingStrictModeOk={StrictModeOk.ToString().ToLowerInvariant()} ConsoleLoggingConsistent={Consistent.ToString().ToLowerInvariant()} ConsoleLoggingConsistencyReason={ConsistencyReason}";

    private static bool AppendJson(string relativePath, object value)
    {
        try { var path=Path.Combine(_root,relativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!); lock(Sync) File.AppendAllText(path,JsonSerializer.Serialize(value)+Environment.NewLine); LastWriteError="None"; return true; }
        catch(Exception ex) { LastWriteError=ex.GetType().Name+":"+ex.Message; return false; }
    }
    private static string EventName(string line) { var start=line.IndexOf('['); var end=start<0?-1:line.IndexOf(']',start+1); return start>=0&&end>start?line[(start+1)..end]:"UNTAGGED"; }
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
            if (immediate) { LastImmediateEventUtc=DateTime.UtcNow; destination.WriteLine(line); return; }
            VerboseEventsSuppressed++;
            if (_options.WriteVerboseEventsToFile && AppendJson(_options.VerboseLogPath,new { tsUtc=DateTime.UtcNow,processRunId=ProcessRunContext.ProcessRunId,@event=evt,level=line.Contains("WARN",StringComparison.OrdinalIgnoreCase)?"warning":"debug",fields=Fields(line) })) VerboseEventsWritten++;
        }
    }
    private sealed record AlertConsoleState(int Level, string Name, string Reason, string CandidateId, decimal? Edge, bool PaperEligible);
}
