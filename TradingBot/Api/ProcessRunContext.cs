using System.Threading;
using TradingBot.Models;

namespace TradingBot.Api;

public static class ProcessRunContext
{
    private static long _diagnosticsCounterMismatchCount;
    private static string _diagnosticsCounterMismatchLastReason = string.Empty;
    private static long _batchBookTokenQuarantinedLogs;
    private static long _marketOrderbookQuarantinedLogs;
    private static long _orderbookCircuitBreakerOpenedLogs;
    private static long _readinessInvariantCorrectionLogs;

    public static string ProcessRunId { get; } = Guid.NewGuid().ToString("N");
    public static string ScannerInstanceId { get; } = Guid.NewGuid().ToString("N");
    public static DateTime StartedAtUtc { get; } = DateTime.UtcNow;
    public static long DiagnosticsCounterMismatchCount => Interlocked.Read(ref _diagnosticsCounterMismatchCount);
    public static string DiagnosticsCounterMismatchLastReason => Volatile.Read(ref _diagnosticsCounterMismatchLastReason);

    public static string EnrichLogLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return line;
        var enriched = line.Contains("ProcessRunId=", StringComparison.OrdinalIgnoreCase)
            ? line
            : line.StartsWith("[", StringComparison.Ordinal) ? $"{line} ProcessRunId={ProcessRunId}" : line;
        ObserveLog(enriched);
        return enriched;
    }

    private static void ObserveLog(string line)
    {
        if (!line.Contains($"ProcessRunId={ProcessRunId}", StringComparison.OrdinalIgnoreCase)) return;
        if (line.Contains("[BATCH_BOOK_TOKEN_QUARANTINED]", StringComparison.Ordinal)) Interlocked.Increment(ref _batchBookTokenQuarantinedLogs);
        if (line.Contains("[MARKET_ORDERBOOK_QUARANTINED]", StringComparison.Ordinal)) Interlocked.Increment(ref _marketOrderbookQuarantinedLogs);
        if (line.Contains("[ORDERBOOK_CIRCUIT_BREAKER_OPENED]", StringComparison.Ordinal)) Interlocked.Increment(ref _orderbookCircuitBreakerOpenedLogs);
    }

    public static string? ValidateOrderbookCounters(OrderBookServiceStats stats)
    {
        var reasons = new List<string>();
        if (stats.MarketOrderbookQuarantineAdded > 0 && stats.MarketOrderbookQuarantineActive < 1)
            reasons.Add($"MarketOrderbookQuarantineAdded={stats.MarketOrderbookQuarantineAdded},MarketOrderbookQuarantineActive={stats.MarketOrderbookQuarantineActive}");
        if (Interlocked.Read(ref _batchBookTokenQuarantinedLogs) > 0 && stats.BatchBookSingleTokenQuarantined <= 0)
            reasons.Add($"BatchBookTokenQuarantinedLogs={Interlocked.Read(ref _batchBookTokenQuarantinedLogs)},BatchBookSingleTokenQuarantined={stats.BatchBookSingleTokenQuarantined}");
        if (Interlocked.Read(ref _orderbookCircuitBreakerOpenedLogs) > 0 && stats.OrderbookCircuitBreakerOpenCount <= 0)
            reasons.Add($"CircuitBreakerOpenedLogs={Interlocked.Read(ref _orderbookCircuitBreakerOpenedLogs)},OrderbookCircuitBreakerOpenCount={stats.OrderbookCircuitBreakerOpenCount}");
        if (stats.OrderbookCircuitBreakerState.Equals("Closed", StringComparison.OrdinalIgnoreCase) && stats.OrderbookCircuitBreakerActive)
            reasons.Add("OrderbookCircuitBreakerState=Closed,OrderbookCircuitBreakerActive=true");
        if (reasons.Count == 0) return null;
        var reason = string.Join(";", reasons);
        Volatile.Write(ref _diagnosticsCounterMismatchLastReason, reason);
        Interlocked.Increment(ref _diagnosticsCounterMismatchCount);
        return reason;
    }

    public static string FormatMismatchLog(string reason, OrderBookServiceStats stats)
        => $"[DIAGNOSTICS_COUNTER_MISMATCH] ProcessRunId={ProcessRunId} Category=Orderbook ObservedLogs=BatchBookTokenQuarantined:{Interlocked.Read(ref _batchBookTokenQuarantinedLogs)},MarketOrderbookQuarantined:{Interlocked.Read(ref _marketOrderbookQuarantinedLogs)},CircuitBreakerOpened:{Interlocked.Read(ref _orderbookCircuitBreakerOpenedLogs)} Counters=BatchBookBadRequests:{stats.BatchBadRequests},BatchBookInvalidTokens:{stats.BatchInvalidTokens},BatchBookSingleTokenQuarantined:{stats.BatchBookSingleTokenQuarantined},MarketOrderbookQuarantineActive:{stats.MarketOrderbookQuarantineActive},MarketOrderbookQuarantineAdded:{stats.MarketOrderbookQuarantineAdded},OrderbookCircuitBreakerOpenCount:{stats.OrderbookCircuitBreakerOpenCount},OrderbookCircuitBreakerState:{stats.OrderbookCircuitBreakerState} Reason={reason} Action=FailDiagnostics";

    public static void RecordReadinessInvariantCorrection(string reason)
    {
        Volatile.Write(ref _diagnosticsCounterMismatchLastReason, $"ReadinessInvariant:{reason}");
        Interlocked.Increment(ref _diagnosticsCounterMismatchCount);
        var logs = Interlocked.Increment(ref _readinessInvariantCorrectionLogs);
        if (logs <= 5 || logs % 50 == 0)
            Console.WriteLine($"[READINESS_INVARIANT_CORRECTED] ProcessRunId={ProcessRunId} Count={logs} Reason={reason}");
    }
}
