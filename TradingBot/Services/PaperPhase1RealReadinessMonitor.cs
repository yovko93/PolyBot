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
    int CleanNearOpenCount, decimal CleanNearOpenThresholdDistance,
    string CleanNearOpenBestCandidateId, decimal? CleanNearOpenBestAfterSafetyEdge,
    decimal? CleanNearOpenBestDistanceToMinEdge, string CleanNearOpenFirstBlockingReason,
    string CleanNearOpenAllBlockingReasons)
{
    public static readonly PaperPhase1RealReadinessState Empty = new(false, DateTime.UtcNow, TimeSpan.Zero, false, false, 0, 0, false, false, true, true, true, true, false, "NotEvaluated", 0, "WaitingForEdge", "NotEvaluated", "None", "None", null, null, "None", DateTime.UtcNow, 0, .005m, "None", null, null, "None", "None");
}

public static class PaperPhase1RealReadinessMonitor
{
    private static readonly object Gate = new();
    private static DateTime? _readinessSince;
    private static DateTime? _orderbookSince;
    private static string _alertKey = "";
    public static PaperPhase1RealReadinessState Current { get; private set; } = PaperPhase1RealReadinessState.Empty;

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

            var level = h.PaperPhase1PositiveOpenedTotal > 0 ? 5 : h.PaperPhase1PositivePaperEligibleTotal > 0 ? 4
                : h.PaperPhase1PositiveExecutableLikeTotal > 0 ? 3 : h.PaperPhase1PositiveRealWatchTotal > 0 ? 2
                : h.PaperPhase1PositiveCleanCapturesTotal > 0 ? 1 : 0;
            var names = new[] { "WaitingForEdge", "CleanPositiveDetected", "RealWatchPositiveDetected", "ExecutableLikeDetected", "PaperEligibleDetected", "PaperOpened" };
            var reason = level switch
            {
                0 => h.PaperPhase1RealWatchBestAfterSafetyEdge < h.PaperPhase1MinEdge ? "BestRealWatchBelowMinEdge" : "NoCleanPositive",
                1 => h.PaperPhase1BestCleanPositiveExcludedFromRealWatchReason,
                2 => h.PaperPhase1RealWatchTopBlockingReason,
                3 => "AwaitingPaperEligibility",
                4 => "PaperOpenRequired",
                _ => "RealPaperPositionOpened"
            };
            var candidate = level >= 5 ? h.PaperPhase1RealWatchOpenedPositionId : level >= 4 ? h.PaperPhase1RealWatchLastEligibleCandidateId
                : level >= 3 ? h.BestExecutableLikeCandidateId : level >= 2 ? h.PaperPhase1PositiveRealWatchBestCandidateId
                : level == 1 ? h.PaperPhase1PositiveCleanBestCandidateId : h.PaperPhase1RealWatchBestCandidateId;
            var edge = level == 1 ? h.PaperPhase1PositiveCleanBestAfterSafetyEdge : h.PaperPhase1RealWatchBestAfterSafetyEdge;
            var distance = edge.HasValue ? Math.Max(0m, h.PaperPhase1MinEdge - edge.Value) : h.PaperPhase1RealWatchBestDistanceToMinEdge;
            var key = $"{level}|{names[level]}|{reason}|{candidate}";
            var changed = key != _alertKey;
            if (changed) _alertKey = key;

            var nearEdge = h.PaperPhase1PositiveCleanBestAfterSafetyEdge;
            decimal? nearDistance = nearEdge.HasValue ? Math.Max(0m, h.PaperPhase1MinEdge - nearEdge.Value) : null;
            var near = h.PaperPhase1PositiveCleanCapturesTotal > 0 || nearDistance <= .005m;
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
                changed ? now : Current.LastChangedUtc, near ? Math.Max(1, h.PaperPhase1PositiveCleanCapturesTotal) : 0, .005m,
                near ? h.PaperPhase1PositiveCleanBestCandidateId : "None", near ? nearEdge : null, near ? nearDistance : null,
                near ? h.PaperPhase1PositiveCleanBestFirstBlockingReason : "None", near ? h.PaperPhase1BestCleanPositiveExcludedFromRealWatchAllReasons : "None");
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
            alert = new { level=s.AlertLevel, name=s.AlertName, reason=s.AlertReason, candidateId=s.CandidateId, marketId=s.MarketId, afterSafetyEdge=s.AfterSafetyEdge, distanceToMinEdge=s.DistanceToMinEdge, firstBlockingReason=s.FirstBlockingReason, lastChangedUtc=s.LastChangedUtc },
            soak = new { enabled=s.Enabled, readinessStable=s.ReadinessStable, orderbookStableMinutes=s.OrderbookStableMinutes, memoryStable=s.MemoryStable, logVolumeStable=s.LogVolumeStable, noCounterMismatches=s.NoCounterMismatches, noUnexpectedPaperOpens=s.NoUnexpectedPaperOpens, noSigningAttempts=s.NoSigningAttempts, noLiveTradingBlocks=s.NoLiveTradingBlocks, consistent=s.Consistent },
            stageCounts = new { invalidArtifacts=h.PaperPhase1PositiveInvalidArtifactsTotal, cleanPositive=h.PaperPhase1PositiveCleanCapturesTotal, realWatchPositive=h.PaperPhase1PositiveRealWatchTotal, executableLike=h.PaperPhase1PositiveExecutableLikeTotal, paperEligible=h.PaperPhase1PositivePaperEligibleTotal, opened=h.PaperPhase1PositiveOpenedTotal },
            safety = new { liveTradingDisabled=h.PaperPhase1LiveTradingDisabled, signingDisabled=s.NoSigningAttempts, paperOnly=h.PaperPhase1Enabled && h.PaperPhase1LiveTradingDisabled } };
        var directory = Path.Combine(contentRoot, "exports"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "paper-phase1-real-alert-latest.json");
        var temp = path + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true })); File.Move(temp, path, true);
    }
}
