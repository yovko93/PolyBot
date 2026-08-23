using System.Text.Json;
using TradingBot.Api;

namespace TradingBot.Services;

public sealed record DashboardWarningsClassification(
    long Total,
    long Blocking,
    long NonBlocking,
    string LastReason,
    string TopReason,
    IReadOnlyDictionary<string, long> ByReason,
    string[] LastWarnings,
    bool AffectsPaperPhase1,
    bool AffectsTradingSafety,
    bool Consistent,
    string RecommendedAction)
{
    public string Status(string normalStatus) => Blocking > 0
        ? "WarningBlocked"
        : Total > 0 && string.Equals(normalStatus, "ReadyWaitingForEdge", StringComparison.Ordinal)
            ? "ReadyWaitingForEdgeWithNonBlockingWarnings"
            : normalStatus;
}

/// <summary>Presentation-only classification and export of accumulated dashboard diagnostics.</summary>
public static class DashboardWarningsService
{
    private static readonly object Sync = new();
    public static bool LastExportOk { get; private set; } = true;
    public static string LastExportError { get; private set; } = "None";

    public static DashboardWarningsClassification Classify(RuntimeHealthSnapshot health)
    {
        var snapshot = ProcessRunContext.DiagnosticsWarningsSnapshot();
        var byReason = new Dictionary<string, long>(snapshot.ByReason, StringComparer.OrdinalIgnoreCase);

        // Compatibility for snapshots created before per-reason collection was introduced.
        var recorded = byReason.Values.Sum();
        if (recorded < health.DiagnosticsCounterMismatchCount)
        {
            var reason = string.IsNullOrWhiteSpace(health.DiagnosticsCounterMismatchLastReason)
                ? "UnclassifiedDiagnosticsWarning"
                : health.DiagnosticsCounterMismatchLastReason;
            byReason[reason] = byReason.GetValueOrDefault(reason) + health.DiagnosticsCounterMismatchCount - recorded;
        }

        if (!LastExportOk) byReason[$"DashboardExportFailure:{LastExportError}"] = 1;
        return Classify(byReason, snapshot.LastWarnings, health.LocalPaperPhase1Readiness,
            health.OrderbookStableNow, health.PaperPhase1ContractFixtureIsolationOk);
    }

    public static DashboardWarningsClassification Classify(
        IReadOnlyDictionary<string, long> warnings,
        IEnumerable<string>? lastWarnings,
        bool localPaperPhase1Readiness,
        bool orderbookStable,
        bool fixtureIsolationOk)
    {
        var byReason = warnings.Where(x => x.Value > 0).OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        long blocking = 0;
        foreach (var warning in byReason)
            if (IsBlocking(warning.Key, localPaperPhase1Readiness, orderbookStable, fixtureIsolationOk)) blocking += warning.Value;
        var total = byReason.Values.Sum();
        var recent = (lastWarnings ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).TakeLast(20).ToArray();
        var last = recent.LastOrDefault() ?? byReason.LastOrDefault().Key ?? "None";
        var top = byReason.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault().Key ?? "None";
        var safety = byReason.Any(x => x.Value > 0 && AffectsTradingSafety(x.Key));
        var paper = blocking > 0 && byReason.Any(x => x.Value > 0 && AffectsPaper(x.Key));
        var consistent = total == blocking + (total - blocking) && (!fixtureIsolationOk ? blocking > 0 : true)
            && (!orderbookStable ? blocking > 0 : true);
        var action = blocking == 0
            ? total == 0 ? "None" : "NoActionRequired;monitor_diagnostics"
            : $"InvestigateAndFix:{byReason.Where(x => IsBlocking(x.Key, localPaperPhase1Readiness, orderbookStable, fixtureIsolationOk)).OrderByDescending(x => x.Value).First().Key}";
        return new(total, blocking, total - blocking, last, top, byReason, recent, paper, safety, consistent, action);
    }

    public static bool Export(string contentRoot, DashboardWarningsClassification value, DateTime timeUtc)
    {
        var payload = new
        {
            TimeUtc = timeUtc, value.Total, value.Blocking, value.NonBlocking, value.ByReason,
            value.LastWarnings, AffectsPaperPhase1 = value.AffectsPaperPhase1,
            AffectsTradingSafety = value.AffectsTradingSafety, value.RecommendedAction
        };
        try
        {
            var directory = Path.Combine(contentRoot, "exports");
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(payload);
            lock (Sync)
            {
                File.WriteAllText(Path.Combine(directory, "dashboard-warnings-latest.json"), json);
                File.AppendAllText(Path.Combine(directory, "dashboard-warnings-history.jsonl"), json + Environment.NewLine);
                LastExportOk = true;
                LastExportError = "None";
            }
            return true;
        }
        catch (Exception ex)
        {
            LastExportOk = false;
            LastExportError = ex.GetType().Name + ":" + ex.Message;
            return false;
        }
    }

    private static bool IsBlocking(string reason, bool localReady, bool orderbookStable, bool fixtureOk)
    {
        if (reason.Contains("Formula", StringComparison.OrdinalIgnoreCase) && reason.Contains("NonBlocking", StringComparison.OrdinalIgnoreCase)) return false;
        if (localReady && (reason.Contains("UnhealthyDiscoveryRequiresUnstableRuntime", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("ReducedUniverse", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("GlobalReadiness", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("SourceAudit", StringComparison.OrdinalIgnoreCase))) return false;
        if (reason.Contains("StaleUI", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("Historical", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("DiagnosticOnly", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("SnapshotSkew", StringComparison.OrdinalIgnoreCase)) return false;
        // Unknown warnings are blockers by default. Only the explicitly recognized diagnostic-only
        // cases above may be allowed through to the operator as non-blocking.
        return true;
    }

    private static bool AffectsPaper(string reason) => reason.Contains("Paper", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("Readiness", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("Gate", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("Counter", StringComparison.OrdinalIgnoreCase);

    private static bool AffectsTradingSafety(string reason) => reason.Contains("Signing", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("LiveTrading", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("Fixture", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("Safety", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("Counter", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("Orderbook", StringComparison.OrdinalIgnoreCase)
        || reason.Contains("Export", StringComparison.OrdinalIgnoreCase);
}
