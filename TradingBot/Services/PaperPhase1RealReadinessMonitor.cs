using System.Text.Json;
using TradingBot.Api;

namespace TradingBot.Services;

public sealed record PaperPhase1RealReadinessState(
    bool Enabled, DateTime StartedUtc, TimeSpan Uptime, bool ProfileActive,
    bool ReadinessStable, double ReadinessStableMinutes, double OrderbookStableMinutes,
    bool MemoryStable, bool LogVolumeStable, bool NoCounterMismatches,
    bool NoUnexpectedPaperOpens, bool NoSigningAttempts, bool NoLiveTradingBlocks,
    bool Consistent, string ConsistencyReason, int AlertLevel, string AlertName,
    string AlertReason, string CandidateId, string MarketId, decimal? AfterSafetyEdge,
    decimal? DistanceToMinEdge, string FirstBlockingReason, DateTime LastChangedUtc,
    bool AlertFresh, double? AlertAgeSeconds, DateTime? AlertLastCleanPositiveUtc,
    long AlertStaleSuppressedCount, string AlertDowngradeReason, int CleanPositiveAlertTtlSeconds,
    int CleanPositiveTtlCount, DateTime? CleanPositiveLastSeenUtc, double? CleanPositiveAgeSeconds,
    int CleanNearOpenCount, decimal CleanNearOpenThresholdDistance,
    string CleanNearOpenBestCandidateId, decimal? CleanNearOpenBestAfterSafetyEdge,
    decimal? CleanNearOpenBestDistanceToMinEdge, string CleanNearOpenFirstBlockingReason,
    string CleanNearOpenAllBlockingReasons)
{
    public static readonly PaperPhase1RealReadinessState Empty = new(false, DateTime.UtcNow, TimeSpan.Zero, false, false, 0, 0, false, false, true, true, true, true, false, "NotEvaluated", 0, "WaitingForEdge", "NotEvaluated", "None", "None", null, null, "None", DateTime.UtcNow, false, null, null, 0, "NotEvaluated", 600, 0, null, null, 0, .005m, "None", null, null, "None", "None");
}

public static class PaperPhase1RealReadinessMonitor
{
    private static readonly object Gate = new();
    private static DateTime? _readinessSince;
    private static DateTime? _orderbookSince;
    private static string _alertKey = "";
    private static int _cleanPositiveAlertTtlSeconds = 600;
    private static long _alertStaleSuppressedCount;
    public static PaperPhase1RealReadinessState Current { get; private set; } = PaperPhase1RealReadinessState.Empty;

    public static void Configure(int cleanPositiveAlertTtlSeconds)
    {
        lock (Gate) _cleanPositiveAlertTtlSeconds = Math.Max(1, cleanPositiveAlertTtlSeconds);
    }

    public static PaperPhase1RealReadinessState Evaluate(RuntimeHealthSnapshot h)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            var enabled = h.PaperPhase1ProfileActive;
            _readinessSince = h.PaperPhase1Readiness ? _readinessSince ?? now : null;
            _orderbookSince = h.ReducedUniverseOrderbookStable ? _orderbookSince ?? now : null;
            var noUnexpected = h.PaperPhase1PositiveOpenedTotal <= h.PaperPhase1PositivePaperEligibleTotal
                && h.PaperCounterRealScannerCountedExecutions == h.PaperPhase1PositiveOpenedTotal;
            var ttlStart = now.AddSeconds(-_cleanPositiveAlertTtlSeconds);
            var fresh = PaperPhase1PositiveCaptureService.Current.TopCaptures
                .Where(x => x.IsValidClean && x.YesOrderbookSnapshotTimestamp >= ttlStart).ToArray();
            var lastCleanPositiveUtc = PaperPhase1PositiveCaptureService.Current.TopCaptures
                .Where(x => x.IsValidClean).Select(x => (DateTime?)x.YesOrderbookSnapshotTimestamp).Max();
            var cleanAgeSeconds = lastCleanPositiveUtc.HasValue ? Math.Max(0, (now-lastCleanPositiveUtc.Value).TotalSeconds) : (double?)null;
            var alertFresh = fresh.Length > 0;
            var staleCumulativeCleanPositive = !alertFresh && h.PaperPhase1PositiveCleanCapturesTotal > 0;
            if (staleCumulativeCleanPositive && Current.AlertLevel > 0) _alertStaleSuppressedCount++;
            var level = fresh.Any(x => x.ActualOpened) ? 5 : fresh.Any(x => x.PaperEligible) ? 4
                : fresh.Any(x => x.ExecutableLike) ? 3 : fresh.Any(x => x.RealWatchAccepted) ? 2
                : alertFresh ? 1 : 0;
            var names = new[] { "WaitingForEdge", "CleanPositiveDetected", "RealWatchPositiveDetected", "ExecutableLikeDetected", "PaperEligibleDetected", "PaperOpened" };
            var reason = level switch
            {
                0 => staleCumulativeCleanPositive ? "NoFreshCleanPositive" : h.PaperPhase1RealWatchBestAfterSafetyEdge < h.PaperPhase1MinEdge ? "BestRealWatchBelowMinEdge" : "NoFreshCleanPositive",
                1 => h.PaperPhase1BestCleanPositiveExcludedFromRealWatchReason,
                2 => h.PaperPhase1RealWatchTopBlockingReason,
                3 => "AwaitingPaperEligibility",
                4 => "PaperOpenRequired",
                _ => "RealPaperPositionOpened"
            };
            var alertCapture = level switch { 5 => fresh.FirstOrDefault(x=>x.ActualOpened), 4 => fresh.FirstOrDefault(x=>x.PaperEligible),
                3 => fresh.FirstOrDefault(x=>x.ExecutableLike), 2 => fresh.FirstOrDefault(x=>x.RealWatchAccepted),
                1 => fresh.OrderByDescending(x=>x.AfterSafetyEdge).FirstOrDefault(), _ => null };
            var candidate = alertCapture?.CandidateId ?? h.PaperPhase1RealWatchBestCandidateId;
            var edge = alertCapture?.AfterSafetyEdge ?? (level == 0 ? h.PaperPhase1RealWatchBestAfterSafetyEdge : null);
            var distance = edge.HasValue ? Math.Max(0m, h.PaperPhase1MinEdge - edge.Value) : h.PaperPhase1RealWatchBestDistanceToMinEdge;
            var key = $"{level}|{names[level]}|{reason}|{candidate}";
            var changed = key != _alertKey;
            if (changed) _alertKey = key;

            var bestFresh = fresh.OrderByDescending(x => x.AfterSafetyEdge).FirstOrDefault();
            var nearEdge = bestFresh?.AfterSafetyEdge;
            decimal? nearDistance = nearEdge.HasValue ? Math.Max(0m, h.PaperPhase1MinEdge - nearEdge.Value) : null;
            var near = alertFresh && (nearEdge > 0m || nearDistance <= .005m);
            var memory = h.PaperPhase1ReadinessUsedCurrentMemoryStable;
            var logs = h.PaperPhase1ReadinessUsedCurrentLogVolumeStable;
            var counters = h.DiagnosticsCounterMismatchCount == 0 && h.PaperCounterAuditConsistent;
            var consistent = enabled && h.PaperPhase1Readiness && h.ReducedUniverseOrderbookStable && memory && logs && counters && noUnexpected && h.SigningAttempts == 0 && h.LiveTradingBlockedCount == 0;
            var consistencyReason = consistent ? "None" : string.Join("|", new[] {
                !enabled ? "ProfileNotActive" : null, !h.PaperPhase1Readiness ? "ReadinessNotStable" : null,
                !h.ReducedUniverseOrderbookStable ? "OrderbookNotStable" : null, !memory ? "MemoryNotStable" : null,
                !logs ? "LogVolumeNotStable" : null, !counters ? "CounterMismatch" : null,
                !noUnexpected ? "UnexpectedPaperOpen" : null, h.SigningAttempts != 0 ? "SigningAttemptsDetected" : null,
                h.LiveTradingBlockedCount != 0 ? "LiveTradingBlocksDetected" : null }.Where(x => x is not null));
            Current = new(enabled, h.StartedAtUtc, h.Uptime, enabled, h.PaperPhase1Readiness,
                Minutes(_readinessSince, now), Minutes(_orderbookSince, now), memory, logs, counters, noUnexpected,
                h.SigningAttempts == 0, h.LiveTradingBlockedCount == 0, consistent, consistencyReason, level, names[level], reason,
                candidate ?? "None", h.PaperPhase1RealWatchBestMarketId, edge, distance, h.PaperPhase1RealWatchTopBlockingReason,
                changed ? now : Current.LastChangedUtc, alertFresh, cleanAgeSeconds, lastCleanPositiveUtc,
                _alertStaleSuppressedCount, staleCumulativeCleanPositive ? "NoFreshCleanPositive" : "None",
                _cleanPositiveAlertTtlSeconds, fresh.Length, lastCleanPositiveUtc, cleanAgeSeconds,
                near ? fresh.Length : 0, .005m, near ? bestFresh!.CandidateId : "None", near ? nearEdge : null, near ? nearDistance : null,
                near ? bestFresh!.FirstBlockingReason : "None", near ? string.Join("|", bestFresh!.AllBlockingReasons) : "None");
            return Current;
        }
    }

    private static double Minutes(DateTime? since, DateTime now) => since.HasValue ? Math.Round((now - since.Value).TotalMinutes, 2) : 0;

    public static string AlertLog(string processRunId) => $"[PAPER_PHASE1_REAL_ALERT] Level={Current.AlertLevel} Name={Current.AlertName} Reason={Current.AlertReason} CandidateId={Current.CandidateId} AfterSafetyEdge={Current.AfterSafetyEdge?.ToString("0.####") ?? "N/A"} DistanceToMinEdge={Current.DistanceToMinEdge?.ToString("0.####") ?? "N/A"} ProcessRunId={processRunId}";
    public static string SoakLog(string processRunId) => $"[PAPER_PHASE1_REAL_SOAK_SUMMARY] Uptime={Current.Uptime} ReadinessStable={Current.ReadinessStable.ToString().ToLowerInvariant()} OrderbookStableMinutes={Current.OrderbookStableMinutes:0.##} NoUnexpectedPaperOpens={Current.NoUnexpectedPaperOpens.ToString().ToLowerInvariant()} NoSigningAttempts={Current.NoSigningAttempts.ToString().ToLowerInvariant()} NoLiveTradingBlocks={Current.NoLiveTradingBlocks.ToString().ToLowerInvariant()} AlertLevel={Current.AlertLevel} Consistent={Current.Consistent.ToString().ToLowerInvariant()} ProcessRunId={processRunId}";

    public static void Export(RuntimeHealthSnapshot h, string contentRoot)
    {
        var s = Evaluate(h);
        var payload = new { generatedAtUtc = DateTime.UtcNow, processRunId = h.ProcessRunId, profile = h.RuntimeProfile,
            alert = new { level=s.AlertLevel, name=s.AlertName, reason=s.AlertReason, candidateId=s.CandidateId, marketId=s.MarketId, afterSafetyEdge=s.AfterSafetyEdge, distanceToMinEdge=s.DistanceToMinEdge, firstBlockingReason=s.FirstBlockingReason, lastChangedUtc=s.LastChangedUtc, fresh=s.AlertFresh, ageSeconds=s.AlertAgeSeconds, lastCleanPositiveUtc=s.AlertLastCleanPositiveUtc, staleSuppressedCount=s.AlertStaleSuppressedCount, downgradeReason=s.AlertDowngradeReason, cleanPositiveAlertTtlSeconds=s.CleanPositiveAlertTtlSeconds },
            soak = new { enabled=s.Enabled, readinessStable=s.ReadinessStable, orderbookStableMinutes=s.OrderbookStableMinutes, memoryStable=s.MemoryStable, logVolumeStable=s.LogVolumeStable, noCounterMismatches=s.NoCounterMismatches, noUnexpectedPaperOpens=s.NoUnexpectedPaperOpens, noSigningAttempts=s.NoSigningAttempts, noLiveTradingBlocks=s.NoLiveTradingBlocks, consistent=s.Consistent },
            stageCounts = new { invalidArtifacts=h.PaperPhase1PositiveInvalidArtifactsTotal, cleanPositive=h.PaperPhase1PositiveCleanCapturesTotal, realWatchPositive=h.PaperPhase1PositiveRealWatchTotal, executableLike=h.PaperPhase1PositiveExecutableLikeTotal, paperEligible=h.PaperPhase1PositivePaperEligibleTotal, opened=h.PaperPhase1PositiveOpenedTotal },
            safety = new { liveTradingDisabled=h.PaperPhase1LiveTradingDisabled, signingDisabled=s.NoSigningAttempts, paperOnly=h.PaperPhase1Enabled && h.PaperPhase1LiveTradingDisabled } };
        var directory = Path.Combine(contentRoot, "exports"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "paper-phase1-real-alert-latest.json");
        var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true);
    }
}
