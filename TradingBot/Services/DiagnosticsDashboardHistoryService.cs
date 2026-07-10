using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Api;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed class DiagnosticsDashboardHistoryService(TradingBotOptions options)
{
    private readonly object _lock = new();
    private readonly List<DiagnosticsDashboardHistorySample> _samples = new();
    private DateTime _lastSampleUtc = DateTime.MinValue;
    private DateTime _lastWarningUtc = DateTime.MinValue;
    private DateTime _lastWriteWarningUtc = DateTime.MinValue;
    public static bool DiagnosticsDashboardHistoryLastWriteOk { get; private set; } = true;
    public static string DiagnosticsDashboardHistoryLastWriteError { get; private set; } = "None";
    public static DiagnosticsDashboardHistoryTrend CurrentTrend { get; private set; } = new(false, 0, null, null, null, null, null, null, null, null, null, null, true);
    public object CurrentHistory => BuildHistory();

    public void TrySample(object? dashboard, string root)
    {
        var cfg = options.DiagnosticsDashboardHistory;
        if (!cfg.Enabled) { CurrentTrend = CurrentTrend with { Enabled = false }; return; }
        var now = DateTime.UtcNow;
        if (now - _lastSampleUtc < TimeSpan.FromSeconds(Math.Max(1, cfg.SampleIntervalSeconds))) return;
        if (cfg.RequireDiagnosticsDashboard && (!options.DiagnosticsDashboard.Enabled || dashboard is null)) { WarnMissing(now); return; }
        JsonNode? node = null;
        try { node = JsonSerializer.SerializeToNode(dashboard); } catch { }
        if (node is null) { WarnMissing(now); return; }
        _lastSampleUtc = now;
        var sample = CreateSample(node, now);
        lock (_lock)
        {
            _samples.Add(sample);
            var max = Math.Max(1, cfg.MaxInMemorySamples);
            if (_samples.Count > max) _samples.RemoveRange(0, _samples.Count - max);
            CurrentTrend = ComputeTrendLocked(cfg.Enabled);
        }
        if (cfg.ExportEnabled)
        {
            WriteLatest(root);
            if (cfg.JsonlEnabled) AppendJsonl(root, sample);
            if (cfg.CsvEnabled) AppendCsv(root, sample);
        }
        LogSummary(sample);
    }

    private void WarnMissing(DateTime now)
    {
        if (now - _lastWarningUtc < TimeSpan.FromMinutes(5)) return;
        _lastWarningUtc = now;
        Console.WriteLine("[DIAGNOSTICS_DASHBOARD_HISTORY_WARNING] Reason=DashboardSnapshotMissing");
    }

    private DiagnosticsDashboardHistorySample CreateSample(JsonNode j, DateTime now) => new(
        now, S(j,"processRunId"), D(j,"uptimeSeconds", TimeSpan.TryParse(S(j,"uptime"), out var ts) ? ts.TotalSeconds : 0), L(j,"snapshotSequence"),
        S(j,"runtimeProfile.profile"), B(j,"diagnostics.overallConsistent"), A(j,"diagnostics.warnings"),
        B(j,"readiness.warmupComplete", true), B(j,"runtimeHealth.memoryStable", true), B(j,"runtimeHealth.logVolumeStable", true), D(j,"runtimeHealth.processMb"), D(j,"runtimeHealth.deltaMb"), D(j,"runtimeHealth.slopeMbPerMin"),
        B(j,"orderbook.stableNow"), B(j,"orderbook.reducedUniverseStableNow"), L(j,"orderbook.batchBookRequests"), L(j,"orderbook.batchBookBadRequests"), L(j,"orderbook.batchBookInvalidTokens"), L(j,"orderbook.postBreakerBadRequestsDeltaWindow"),
        B(j,"safety.paperDiagnosticsLimitedEligible"), L(j,"safety.paperOpened"), L(j,"safety.paperOpenPositions"), D(j,"safety.paperExposure"), L(j,"safety.liveTradingBlocked"), L(j,"safety.signingAttempts"),
        N(j,"singleMarket.bestRawEdge"), N(j,"singleMarket.bestAfterCostEdge"), N(j,"singleMarket.bestAfterSafetyEdge"), L(j,"singleMarket.positiveAfterSafety"), L(j,"singleMarket.executionReady"),
        L(j,"strategies.summary.totalCandidates"), L(j,"strategies.summary.totalPositive"), L(j,"strategies.summary.totalExecutionReady"), L(j,"strategies.summary.totalPaperOpened"),
        L(j,"focusUniverse.watchlistSize"), L(j,"focusUniverse.admitted"), L(j,"focusUniverse.evicted"), L(j,"focusUniverse.refreshed"), N(j,"focusUniverse.bestAfterSafetyEdge"),
        L(j,"edgeTransition.tracked"), L(j,"edgeTransition.improving"), L(j,"edgeTransition.worsening"), L(j,"edgeTransition.stableNearBreakEven"), L(j,"edgeTransition.alertCandidates"), L(j,"edgeTransition.positiveCandidates"), N(j,"edgeTransition.bestCurrentEdge"),
        L(j,"edgeCompression.items"), L(j,"edgeCompression.nearBreakEven"), L(j,"edgeCompression.rawPositive"), L(j,"edgeCompression.afterCostPositive"), L(j,"edgeCompression.afterSafetyPositive"), N(j,"edgeCompression.bestDistanceToBreakEven"), S(j,"edgeCompression.dominantDragComponent"),
        L(j,"spreadMicrostructure.items"), L(j,"spreadMicrostructure.alreadyNearExecutable"), L(j,"spreadMicrostructure.thinTopBook"), L(j,"spreadMicrostructure.depthSufficient"), N(j,"spreadMicrostructure.bestMoveNeededToBreakEven"), N(j,"spreadMicrostructure.medianMoveNeededToBreakEven"), L(j,"spreadMicrostructure.minTicksToBreakEven"), S(j,"spreadMicrostructure.dominantCause"),
        L(j,"signalR.payloadTrimmedTotal"), L(j,"signalR.payloadTrimmedSuppressed"), L(j,"allowlist.healthy"), L(j,"allowlist.monitoringOnly"), L(j,"allowlist.reviewOnly"), B(j,"allowlist.refreshAutoApply"), L(j,"paperPhase1EligibilityLadder.validPriced"), L(j,"paperPhase1EligibilityLadder.nearBreakEven"), L(j,"paperPhase1EligibilityLadder.positiveAfterSafety"), L(j,"paperPhase1EligibilityLadder.paperEligible"), N(j,"paperPhase1EligibilityLadder.bestAfterSafetyEdge"), N(j,"paperPhase1EligibilityLadder.bestDistanceToMinEdge"), S(j,"paperPhase1EligibilityLadder.topBlockingReason"));

    private object BuildHistory() { lock(_lock) return new { generatedAtUtc=DateTime.UtcNow, processRunId=ProcessRunContext.ProcessRunId, enabled=options.DiagnosticsDashboardHistory.Enabled, diagnosticsOnly=options.DiagnosticsDashboardHistory.DiagnosticsOnly, sampleIntervalSeconds=options.DiagnosticsDashboardHistory.SampleIntervalSeconds, sampleCount=_samples.Count, oldestSampleUtc=_samples.FirstOrDefault()?.TimestampUtc, newestSampleUtc=_samples.LastOrDefault()?.TimestampUtc, overallConsistent=CurrentTrend.Consistent, warnings=Array.Empty<string>(), samples=_samples.ToArray() }; }
    private void WriteLatest(string root)
    {
        Exception? lastError = null;
        try
        {
            var path = PathFor(root, options.DiagnosticsDashboardHistory.HistoryPath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(BuildHistory(), JsonOpts(true));
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, path, true);
                    DiagnosticsDashboardHistoryLastWriteOk = true;
                    DiagnosticsDashboardHistoryLastWriteError = "None";
                    return;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    lastError = ex;
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    Thread.Sleep(TimeSpan.FromMilliseconds(25 * attempt));
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            lastError = ex;
        }

        DiagnosticsDashboardHistoryLastWriteOk = false;
        DiagnosticsDashboardHistoryLastWriteError = lastError?.Message ?? "Unknown";
        WarnLatestWriteFailed(DateTime.UtcNow, DiagnosticsDashboardHistoryLastWriteError);
    }
    private void WarnLatestWriteFailed(DateTime now, string error)
    {
        if (now - _lastWriteWarningUtc < TimeSpan.FromMinutes(5)) return;
        _lastWriteWarningUtc = now;
        Console.WriteLine($"[DIAGNOSTICS_DASHBOARD_HISTORY_WARNING] Reason=LatestWriteFailed Error={error}");
    }
    private void AppendJsonl(string root, DiagnosticsDashboardHistorySample s) { try { var p=PathFor(root, options.DiagnosticsDashboardHistory.JsonlPath); Directory.CreateDirectory(Path.GetDirectoryName(p)!); if (File.Exists(p) && new FileInfo(p).Length > Math.Max(1, options.DiagnosticsDashboardHistory.MaxJsonlFileMb)*1024L*1024L) File.Move(p, Path.Combine(Path.GetDirectoryName(p)!, $"diagnostics-dashboard-history-{DateTime.UtcNow:yyyyMMdd-HHmmss}.jsonl"), true); File.AppendAllText(p, JsonSerializer.Serialize(s, JsonOpts(false)) + Environment.NewLine); } catch(Exception ex) { Console.WriteLine($"[DIAGNOSTICS_DASHBOARD_HISTORY_WARNING] Reason=JsonlAppendFailed Error={ex.Message}"); } }
    private void AppendCsv(string root, DiagnosticsDashboardHistorySample s) { try { var p=PathFor(root, options.DiagnosticsDashboardHistory.CsvPath); Directory.CreateDirectory(Path.GetDirectoryName(p)!); var exists=File.Exists(p); using var w=new StreamWriter(p, true, Encoding.UTF8); if(!exists) w.WriteLine(string.Join(',', CsvFields.Select(x=>x.Name))); w.WriteLine(string.Join(',', CsvFields.Select(x=>Csv(x.GetValue(s))))); } catch(Exception ex) { Console.WriteLine($"[DIAGNOSTICS_DASHBOARD_HISTORY_WARNING] Reason=CsvAppendFailed Error={ex.Message}"); } }
    private DiagnosticsDashboardHistoryTrend ComputeTrendLocked(bool enabled) { var o=_samples.FirstOrDefault(); var p=_samples.Count>1?_samples[^2]:o; var n=_samples.LastOrDefault(); decimal? edge=n?.SingleMarketBestAfterSafetyEdge??n?.EdgeTransitionBestCurrentEdge; decimal? oe=o is null?null:o.SingleMarketBestAfterSafetyEdge??o.EdgeTransitionBestCurrentEdge; decimal? pe=p is null?null:p.SingleMarketBestAfterSafetyEdge??p.EdgeTransitionBestCurrentEdge; decimal? mv=n?.SpreadBestMoveNeededToBreakEven; return new(enabled,_samples.Count,o?.TimestampUtc,n?.TimestampUtc, edge-oe, edge-pe, mv-(o?.SpreadBestMoveNeededToBreakEven), mv-(p?.SpreadBestMoveNeededToBreakEven), n?.SlopeMbPerMin, (n?.SignalRPayloadTrimmedSuppressed??0)-(o?.SignalRPayloadTrimmedSuppressed??0), (n?.FocusWatchlistSize??0)-(o?.FocusWatchlistSize??0), (n?.BatchBookBadRequests??0)-(o?.BatchBookBadRequests??0), n?.OverallConsistent ?? true); }
    private void LogSummary(DiagnosticsDashboardHistorySample s) { var t=CurrentTrend; Console.WriteLine($"[DIAGNOSTICS_DASHBOARD_HISTORY_SUMMARY] Enabled={options.DiagnosticsDashboardHistory.Enabled.ToString().ToLowerInvariant()} Samples={t.Samples} LatestEdge={s.SingleMarketBestAfterSafetyEdge?.ToString("0.####",CultureInfo.InvariantCulture)??"N/A"} EdgeDeltaFromOldest={t.EdgeBestDeltaFromOldest?.ToString("0.####",CultureInfo.InvariantCulture)??"N/A"} EdgeDeltaFromPrevious={t.EdgeBestDeltaFromPrevious?.ToString("0.####",CultureInfo.InvariantCulture)??"N/A"} LatestMoveNeeded={s.SpreadBestMoveNeededToBreakEven?.ToString("0.####",CultureInfo.InvariantCulture)??"N/A"} MoveNeededDeltaFromOldest={t.MoveNeededDeltaFromOldest?.ToString("0.####",CultureInfo.InvariantCulture)??"N/A"} SignalRTrimSuppressedDelta={t.SignalRTrimSuppressedDelta} FocusWatchlistDelta={t.FocusWatchlistDelta} OrderbookBadRequestsDelta={t.OrderbookBadRequestsDelta} Consistent={t.Consistent.ToString().ToLowerInvariant()} ProcessRunId={s.ProcessRunId}"); }
    private static JsonSerializerOptions JsonOpts(bool indented)=>new(){WriteIndented=indented,PropertyNamingPolicy=JsonNamingPolicy.CamelCase}; private static string PathFor(string root,string p)=>Path.IsPathRooted(p)?p:Path.Combine(root,p);
    private static JsonNode? J(JsonNode j,string path)=>path.Split('.').Aggregate((JsonNode?)j,(n,k)=>n?[k]); private static string S(JsonNode j,string p)=>J(j,p)?.GetValue<string>()??""; private static long L(JsonNode j,string p)=>long.TryParse(J(j,p)?.ToString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var v)?v:0; private static double D(JsonNode j,string p,double d=0)=>double.TryParse(J(j,p)?.ToString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var v)?v:d; private static decimal? N(JsonNode j,string p)=>decimal.TryParse(J(j,p)?.ToString(),NumberStyles.Any,CultureInfo.InvariantCulture,out var v)?v:null; private static bool B(JsonNode j,string p,bool d=false)=>bool.TryParse(J(j,p)?.ToString(),out var v)?v:d; private static int A(JsonNode j,string p)=>J(j,p) is JsonArray a?a.Count:0; private static string Csv(object? v)=>v switch{null=>"",DateTime dt=>dt.ToString("O",CultureInfo.InvariantCulture),IFormattable f=>f.ToString(null,CultureInfo.InvariantCulture)??"",_=>('"'+v.ToString()!.Replace("\"","\"\"")+'"')};
    private static readonly System.Reflection.PropertyInfo[] CsvFields=typeof(DiagnosticsDashboardHistorySample).GetProperties();
}

public sealed record DiagnosticsDashboardHistoryTrend(bool Enabled,int Samples,DateTime? OldestSampleUtc,DateTime? NewestSampleUtc,decimal? EdgeBestDeltaFromOldest,decimal? EdgeBestDeltaFromPrevious,decimal? MoveNeededDeltaFromOldest,decimal? MoveNeededDeltaFromPrevious,double? MemorySlopeLatest,long? SignalRTrimSuppressedDelta,long? FocusWatchlistDelta,long? OrderbookBadRequestsDelta,bool Consistent);

public sealed record DiagnosticsDashboardHistorySample(DateTime TimestampUtc,string ProcessRunId,double UptimeSeconds,long SnapshotSequence,string RuntimeProfile,bool OverallConsistent,int Warnings,bool WarmupComplete,bool MemoryStable,bool LogVolumeStable,double ProcessMb,double DeltaMb,double SlopeMbPerMin,bool OrderbookStableNow,bool ReducedUniverseOrderbookStableNow,long BatchBookRequests,long BatchBookBadRequests,long BatchBookInvalidTokens,long PostBreakerBadRequestsDeltaWindow,bool PaperDiagnosticsLimitedEligible,long PaperOpened,long PaperOpenPositions,double PaperExposure,long LiveTradingBlocked,long SigningAttempts,decimal? SingleMarketBestRawEdge,decimal? SingleMarketBestAfterCostEdge,decimal? SingleMarketBestAfterSafetyEdge,long SingleMarketPositiveAfterSafety,long SingleMarketExecutionReady,long TotalCandidates,long TotalPositive,long TotalExecutionReady,long TotalPaperOpened,long FocusWatchlistSize,long FocusAdmitted,long FocusEvicted,long FocusRefreshed,decimal? FocusBestAfterSafetyEdge,long EdgeTransitionTracked,long EdgeTransitionImproving,long EdgeTransitionWorsening,long EdgeTransitionStableNearBreakEven,long EdgeTransitionAlertCandidates,long EdgeTransitionPositiveCandidates,decimal? EdgeTransitionBestCurrentEdge,long EdgeCompressionItems,long EdgeCompressionNearBreakEven,long EdgeCompressionRawPositive,long EdgeCompressionAfterCostPositive,long EdgeCompressionAfterSafetyPositive,decimal? EdgeCompressionBestDistanceToBreakEven,string EdgeCompressionDominantDragComponent,long SpreadMicrostructureItems,long SpreadAlreadyNearExecutable,long SpreadThinTopBook,long SpreadDepthSufficient,decimal? SpreadBestMoveNeededToBreakEven,decimal? SpreadMedianMoveNeededToBreakEven,long SpreadMinTicksToBreakEven,string SpreadDominantCause,long SignalRPayloadTrimmedTotal,long SignalRPayloadTrimmedSuppressed,long AllowlistHealthy,long AllowlistMonitoringOnly,long AllowlistReviewOnly,bool AllowlistRefreshAutoApply,long PaperPhase1LadderValidPriced,long PaperPhase1LadderNearBreakEven,long PaperPhase1LadderPositiveAfterSafety,long PaperPhase1LadderPaperEligible,decimal? PaperPhase1LadderBestAfterSafetyEdge,decimal? PaperPhase1LadderBestDistanceToMinEdge,string PaperPhase1LadderTopBlockingReason);
