using System.Text.Json;
using TradingBot.Api;

namespace TradingBot.Services;

public sealed record CleanPositiveGateCandidate(
    string CandidateId, string MarketId, string Strategy, DateTime WindowStartUtc, DateTime WindowEndUtc,
    decimal? RawEdge, decimal? AfterCostEdge, decimal AfterSafetyEdge, decimal? RealGateEdge,
    decimal? EdgeDeltaFromLadderToGate, decimal MinEdge, decimal? DistanceToMinEdge,
    bool LadderPositiveAfterSafety, bool CleanPositive, bool RealWatchCandidate, bool GateCandidate,
    bool EdgeStable, bool DepthSufficient, bool FillPassed, bool RiskPassed,
    bool PaperDiagnosticsLimitedEligible, bool PaperEligible, bool PaperOpened,
    string FirstBlockingStage, string FirstBlockingReason, IReadOnlyList<string> AllBlockingReasons,
    DateTime LastSeenUtc, double SnapshotAgeMs, string? OrderbookSequenceOrTimestamp);

public sealed record CleanPositiveGateReconciliation(
    int CleanPositive5m = 0, int CleanPositiveToGate5m = 0, int CleanPositiveLostBeforeGate5m = 0,
    int CleanPositiveBelowMinAtGate5m = 0, int CleanPositiveStaleAtGate5m = 0,
    int CleanPositiveDepthRejected5m = 0, int CleanPositiveFillRejected5m = 0,
    int CleanPositiveRiskRejected5m = 0, int CleanPositivePaperEligible5m = 0,
    int CleanPositiveOpened5m = 0, decimal? CleanPositiveBestLadderEdge5m = null,
    decimal? CleanPositiveBestGateEdge5m = null, decimal? CleanPositiveMaxEdgeDelta5m = null,
    string CleanPositiveTopBlocker5m = "None", string P99AfterSafetySource = "FocusUniverseAfterSafetyEdge",
    string BestEdgeSource = "RealWatchGateAfterSafetyEdge", string P99CandidateId = "None",
    string BestGateCandidateId = "None", bool P99CandidateReachedGate = false,
    string P99CandidateGateBlocker = "NoPositiveCandidate", decimal? P99CandidateGateEdge = null,
    decimal? P99CandidateEdgeDeltaToGate = null, IReadOnlyList<CleanPositiveGateCandidate>? Candidates = null)
{
    public static CleanPositiveGateReconciliation Empty { get; } = new(Candidates: []);
}

/// <summary>Read-only reconciliation. It cannot alter eligibility or submit, sign, or open orders.</summary>
public static class CleanPositiveToGateReconciliationService
{
    private static readonly object Sync = new();
    private static DateTime _lastHistoryUtc = DateTime.MinValue;
    public static CleanPositiveGateReconciliation Current { get; private set; } = CleanPositiveGateReconciliation.Empty;

    public static void Update(IReadOnlyList<FocusUniverseItem> focusRows, decimal minEdge, string root)
    {
        lock (Sync)
        {
            var now = DateTime.UtcNow;
            var start = now.AddMinutes(-5);
            var captures = PaperPhase1PositiveCaptureService.Current.TopCaptures
                .Where(x => x.AfterSafetyEdge > 0m && x.YesOrderbookSnapshotTimestamp >= start)
                .ToArray();
            var ladder = focusRows.Where(x => x.ValidPriced && x.CurrentAfterSafetyEdge > 0m && x.LastSeenUtc >= start)
                .OrderByDescending(x => x.CurrentAfterSafetyEdge).ToArray();
            var candidates = ladder.Select(row => Build(row, captures
                .Where(x => x.MarketId == row.MarketIdOrGroupKey || x.CandidateId == row.WatchlistId)
                .OrderByDescending(x => x.YesOrderbookSnapshotTimestamp).FirstOrDefault(), start, now, minEdge)).ToArray();

            // A clean scanner capture may not be retained by the focus-universe view. Preserve it in the audit.
            var extra = captures.Where(c => !candidates.Any(x => x.MarketId == c.MarketId))
                .Select(c => Build(c, start, now)).ToArray();
            candidates = candidates.Concat(extra).OrderByDescending(x => x.AfterSafetyEdge).ToArray();
            var p99 = Percentile(ladder, .99);
            var p99Row = ladder.FirstOrDefault(x => x.WatchlistId == p99?.WatchlistId);
            var p99Candidate = p99Row is null ? null : candidates.FirstOrDefault(x => x.CandidateId == p99Row.WatchlistId);
            var blockers = candidates.GroupBy(x => x.FirstBlockingReason).OrderByDescending(x => x.Count()).ThenBy(x => x.Key).FirstOrDefault();
            var bestGate = candidates.Where(x => x.GateCandidate && x.RealGateEdge.HasValue).OrderByDescending(x => x.RealGateEdge).FirstOrDefault();
            Current = new(
                candidates.Length, candidates.Count(x => x.GateCandidate), candidates.Count(x => !x.GateCandidate),
                candidates.Count(x => x.GateCandidate && x.RealGateEdge < minEdge),
                candidates.Count(x => x.FirstBlockingReason.Contains("Stale", StringComparison.OrdinalIgnoreCase)),
                candidates.Count(x => x.GateCandidate && !x.DepthSufficient), candidates.Count(x => x.GateCandidate && !x.FillPassed),
                candidates.Count(x => x.GateCandidate && !x.RiskPassed), candidates.Count(x => x.PaperEligible),
                candidates.Count(x => x.PaperOpened), candidates.Select(x => (decimal?)x.AfterSafetyEdge).Max(),
                candidates.Where(x => x.GateCandidate).Select(x => x.RealGateEdge).Max(),
                candidates.Select(x => x.EdgeDeltaFromLadderToGate).Where(x => x.HasValue).Select(x => (decimal?)Math.Abs(x!.Value)).Max(),
                blockers?.Key ?? "None", "FocusUniverseAfterSafetyEdge", "RealWatchGateAfterSafetyEdge",
                p99Row?.WatchlistId ?? "None", bestGate?.CandidateId ?? "None", p99Candidate?.GateCandidate ?? false,
                P99Blocker(p99Candidate), p99Candidate?.RealGateEdge, p99Candidate?.EdgeDeltaFromLadderToGate, candidates);
            var payload = new { generatedAtUtc = now, processRunId = ProcessRunContext.ProcessRunId, windowStartUtc = start,
                windowEndUtc = now, diagnosticsOnly = true, productionMinEdgeUnchanged = minEdge,
                summary = Current with { Candidates = null }, candidates };
            Write(Path.Combine(root, "exports/phase1-clean-positive-reconciliation-latest.json"), payload);
            if (now - _lastHistoryUtc >= TimeSpan.FromMinutes(5))
            {
                Append(Path.Combine(root, "exports/phase1-clean-positive-reconciliation-history.jsonl"), payload);
                _lastHistoryUtc = now;
            }
        }
    }

    private static CleanPositiveGateCandidate Build(FocusUniverseItem row, PaperPhase1PositiveCapture? gate, DateTime start, DateTime now, decimal min)
    {
        var reasons = gate?.AllBlockingReasons ?? Split(row.LastRejectedReason);
        var stage = gate is null ? "GateCandidate" : Stage(gate);
        var reason = gate is null ? (row.LastRejectedReason.Contains("Stale", StringComparison.OrdinalIgnoreCase) ? "P99CandidateStaleBeforeGate" : "P99CandidateNotGateCandidate") : reasons.FirstOrDefault() ?? "None";
        if (gate is null && reasons.Count == 0) reasons = [reason];
        return new(row.WatchlistId, row.MarketIdOrGroupKey, row.Strategy, start, now, row.CurrentRawEdge,
            row.CurrentAfterCostEdge, row.CurrentAfterSafetyEdge, gate?.AfterSafetyEdge,
            gate is null ? null : row.CurrentAfterSafetyEdge - gate.AfterSafetyEdge, min,
            gate is null ? null : gate.AfterSafetyEdge - min, true, true, gate?.RealWatchAccepted ?? false, gate is not null,
            gate?.EdgeStable ?? false, gate?.DepthSufficient ?? false, gate?.FillPassed ?? false, gate?.RiskPassed ?? false,
            gate?.PaperDiagnosticsLimitedEligible ?? false, gate?.PaperEligible ?? false, gate?.ActualOpened ?? false,
            stage, reason, reasons, row.LastSeenUtc, Math.Max(0, (now - row.LastSeenUtc).TotalMilliseconds),
            gate?.YesOrderbookSnapshotTimestamp.ToString("O"));
    }

    private static CleanPositiveGateCandidate Build(PaperPhase1PositiveCapture c, DateTime start, DateTime now) =>
        new(c.CandidateId, c.MarketId, c.Strategy, start, now, c.RawEdge, c.AfterCostEdge, c.AfterSafetyEdge,
            c.AfterSafetyEdge, 0m, c.MinEdge, c.AfterSafetyEdge-c.MinEdge, true, true, c.RealWatchAccepted, true,
            c.EdgeStable, c.DepthSufficient, c.FillPassed, c.RiskPassed, c.PaperDiagnosticsLimitedEligible,
            c.PaperEligible, c.ActualOpened, Stage(c), c.FirstBlockingReason, c.AllBlockingReasons,
            c.YesOrderbookSnapshotTimestamp, Math.Max(0,(now-c.YesOrderbookSnapshotTimestamp).TotalMilliseconds), c.YesOrderbookSnapshotTimestamp.ToString("O"));

    private static string Stage(PaperPhase1PositiveCapture c) => !c.EdgeStable ? "EdgeStable" : !c.DepthSufficient ? "DepthSufficient" :
        !c.FillPassed ? "FillPassed" : !c.RiskPassed ? "RiskPassed" : !c.PaperDiagnosticsLimitedEligible ? "PaperDiagnosticsLimitedEligible" :
        !c.RealWatchAccepted ? "RealWatchCandidate" : !c.PaperEligible ? "PaperEligible" : !c.ActualOpened ? "PaperOpened" : "None";
    private static string P99Blocker(CleanPositiveGateCandidate? c) => c is null ? "P99CandidateNotGateCandidate" : !c.GateCandidate ? c.FirstBlockingReason :
        c.RealGateEdge < c.MinEdge ? "P99CandidateBelowMinAtGate" : !c.EdgeStable ? "P99CandidateInvalidAtGate" :
        !c.DepthSufficient ? "P99CandidateDepthRejected" : !c.FillPassed ? "P99CandidateFillRejected" :
        !c.RiskPassed ? "P99CandidateRiskRejected" : c.FirstBlockingReason;
    private static FocusUniverseItem? Percentile(FocusUniverseItem[] rows, double p) => rows.Length == 0 ? null : rows.OrderBy(x => x.CurrentAfterSafetyEdge).ElementAt((int)Math.Clamp(Math.Ceiling(p*rows.Length)-1,0,rows.Length-1));
    private static string[] Split(string value) => string.IsNullOrWhiteSpace(value) || value == "None" ? [] : value.Split(['|',',',';'], StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries);
    private static void Write(string path, object value) => SafeExportWriter.WriteJson(path,JsonSerializer.Serialize(value,Options));
    private static void Append(string path, object value) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); SafeExportWriter.AppendText(path,JsonSerializer.Serialize(value)+Environment.NewLine); }
    private static readonly JsonSerializerOptions Options = new() { WriteIndented=true, PropertyNamingPolicy=JsonNamingPolicy.CamelCase };
}
