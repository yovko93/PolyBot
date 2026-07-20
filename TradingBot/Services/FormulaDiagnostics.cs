using TradingBot.Options;
using TradingBot.Api;

namespace TradingBot.Services;

public sealed record FormulaWarningDetail(string FormulaName, string Strategy, string EdgeKind,
    decimal MinAllowed, decimal MaxAllowed, decimal Actual, decimal Tolerance, bool IsBlocking,
    string Classification)
{
    public override string ToString() => $"FormulaName={FormulaName} Strategy={Strategy} EdgeKind={EdgeKind} MinAllowed={MinAllowed:0.####} MaxAllowed={MaxAllowed:0.####} Actual={Actual:0.####} Tolerance={Tolerance:0.####} IsBlocking={IsBlocking.ToString().ToLowerInvariant()} Classification={Classification}";
}

public sealed record FormulaDiagnosticsSnapshot(bool Enabled, long WarningsTotal, long WarningsBlocking,
    long WarningsNonBlocking, long WarningsSuppressed, string LastWarning, string LastWarningClassification,
    bool Consistent);

public static class FormulaDiagnostics
{
    private static readonly object Sync = new();
    private static FormulaDiagnosticsOptions _options = new();
    private static long _total, _blocking, _nonBlocking, _suppressed, _logged;
    private static string _last = "None", _classification = "None";
    private static DateTime _lastSummaryUtc = DateTime.UtcNow;

    public static void Configure(FormulaDiagnosticsOptions options) { lock (Sync) _options = options; }
    public static decimal WarningTolerance { get { lock (Sync) return _options.WarningTolerance; } }

    public static bool Report(FormulaWarningDetail warning)
    {
        lock (Sync)
        {
            if (!_options.Enabled) return false;
            _total++;
            if (warning.IsBlocking) _blocking++; else _nonBlocking++;
            _last = warning.ToString(); _classification = warning.Classification;
            var expected = !warning.IsBlocking && warning.Classification == "NonBlockingCostAdjustedEdgeRange";
            if (expected && _options.SuppressExpectedCostAdjustedWarnings && _logged >= Math.Max(0, _options.LogFirstN))
            {
                _suppressed++;
                MaybeLogSummary();
                return false;
            }
            _logged++;
            return true;
        }
    }

    private static void MaybeLogSummary()
    {
        if (DateTime.UtcNow - _lastSummaryUtc < TimeSpan.FromSeconds(Math.Max(1, _options.SummaryIntervalSeconds))) return;
        Console.WriteLine($"[FORMULA_WARNING_SUPPRESSED_SUMMARY] Classification=NonBlockingCostAdjustedEdgeRange Suppressed={_suppressed} ProcessRunId={ProcessRunContext.ProcessRunId}");
        _lastSummaryUtc = DateTime.UtcNow;
    }

    public static FormulaDiagnosticsSnapshot Current { get { lock (Sync) return new(_options.Enabled, _total, _blocking, _nonBlocking, _suppressed, _last, _classification, _total == _blocking + _nonBlocking && _suppressed <= _nonBlocking); } }
}
