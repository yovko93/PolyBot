using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.Api;
using TradingBot.Options;

namespace TradingBot.Services;

public enum ScannerRuntimeState
{
    Stopped,
    Starting,
    Running,
    PausedByMemoryGuard,
    PausedByConfigError,
    Stopping,
    Faulted
}

public sealed class ScannerStateMachine
{
    private readonly object _gate = new();
    public ScannerRuntimeState State { get; private set; } = ScannerRuntimeState.Stopped;
    public int StartAttempts { get; private set; }
    public int RejectedTransitions { get; private set; }

    public bool TryStart(Action<string>? log = null)
    {
        lock (_gate)
        {
            StartAttempts++;
            if (State != ScannerRuntimeState.Stopped)
                return Reject(ScannerRuntimeState.Running, $"StartAllowedOnlyFromStopped Attempts={StartAttempts}", log);
            State = ScannerRuntimeState.Starting;
            State = ScannerRuntimeState.Running;
            return true;
        }
    }

    public bool TryPauseByMemoryGuard(Action<string>? log = null) => TryTransition(ScannerRuntimeState.PausedByMemoryGuard, log);
    public bool TryPauseByConfigError(Action<string>? log = null) => TryTransition(ScannerRuntimeState.PausedByConfigError, log);
    public bool TryFault(Action<string>? log = null) => TryTransition(ScannerRuntimeState.Faulted, log, allowSameState: true);
    public bool TryStop(Action<string>? log = null) => TryTransition(ScannerRuntimeState.Stopping, log, allowSameState: true);
    public bool TryResume(Action<string>? log = null) => TryTransition(ScannerRuntimeState.Running, log);

    private bool TryTransition(ScannerRuntimeState to, Action<string>? log, bool allowSameState = false)
    {
        lock (_gate)
        {
            if (allowSameState && State == to) return true;
            var valid = (State, to) switch
            {
                (ScannerRuntimeState.Running, ScannerRuntimeState.PausedByMemoryGuard) => true,
                (ScannerRuntimeState.Running, ScannerRuntimeState.PausedByConfigError) => true,
                (ScannerRuntimeState.PausedByMemoryGuard, ScannerRuntimeState.Running) => true,
                (ScannerRuntimeState.PausedByConfigError, ScannerRuntimeState.Running) => true,
                (ScannerRuntimeState.Running, ScannerRuntimeState.Faulted) => true,
                (ScannerRuntimeState.PausedByMemoryGuard, ScannerRuntimeState.Faulted) => true,
                (ScannerRuntimeState.PausedByConfigError, ScannerRuntimeState.Faulted) => true,
                (ScannerRuntimeState.Running, ScannerRuntimeState.Stopping) => true,
                (ScannerRuntimeState.PausedByMemoryGuard, ScannerRuntimeState.Stopping) => true,
                (ScannerRuntimeState.PausedByConfigError, ScannerRuntimeState.Stopping) => true,
                (ScannerRuntimeState.Faulted, ScannerRuntimeState.Stopping) => true,
                (ScannerRuntimeState.Stopping, ScannerRuntimeState.Stopped) => true,
                _ => false
            };
            if (!valid) return Reject(to, "InvalidScannerStateTransition", log);
            State = to;
            return true;
        }
    }

    private bool Reject(ScannerRuntimeState to, string reason, Action<string>? log)
    {
        RejectedTransitions++;
        log?.Invoke($"[SCANNER_STATE_TRANSITION_REJECTED] From={State} To={to} Reason={reason}");
        return false;
    }
}

public sealed record ScannerExceptionContext(
    string Stage,
    string Component,
    long ScanId,
    long Cycle,
    int BatchOffset,
    string MarketRange,
    string ScannerState,
    bool IsPausedByMemoryGuard,
    bool IsDisposed);

public sealed record ScannerExceptionRecord(
    DateTime TimestampUtc,
    string Type,
    string Message,
    string Stage,
    string Component,
    long ScanId,
    long Cycle,
    int BatchOffset,
    string MarketRange,
    string ScannerState,
    bool IsPausedByMemoryGuard,
    bool IsDisposed,
    string StackTrace,
    int SameExceptionCount,
    bool FullStackLogged,
    bool Faulted);

public sealed class ScannerExceptionReporter
{
    private readonly object _gate = new();
    private readonly Queue<ScannerExceptionRecord> _records = new();
    private readonly Dictionary<string, ExceptionSeries> _series = new(StringComparer.Ordinal);
    private readonly string _contentRootPath;
    private readonly TradingBotOptions _options;
    private DateTime _lastErrorWindowUtc = DateTime.MinValue;
    private int _errorsThisMinute;
    private int _currentBackoffSeconds;

    public ScannerExceptionReporter(string contentRootPath, TradingBotOptions options)
    {
        _contentRootPath = contentRootPath;
        _options = options;
        _currentBackoffSeconds = Math.Max(1, options.ScanErrorBackoffSeconds);
    }

    public ScannerExceptionRecord Record(Exception ex, ScannerExceptionContext context, Action<string> log)
    {
        var stack = ex.ToString();
        var hash = StableHash($"{ex.GetType().FullName}|{ex.Message}|{stack}");
        ScannerExceptionRecord record;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (now - _lastErrorWindowUtc >= TimeSpan.FromMinutes(1))
            {
                _lastErrorWindowUtc = now;
                _errorsThisMinute = 0;
            }
            _errorsThisMinute++;

            _series.TryGetValue(hash, out var series);
            series ??= new ExceptionSeries(0, false);
            var count = series.Count + 1;
            var fullStackLogged = !series.FullStackLogged || !_options.Diagnostics.OperationalQuietMode;
            var faulted = _options.PauseOnRepeatedScannerError && count >= Math.Max(1, _options.MaxRepeatedErrorsBeforeFault);
            _series[hash] = new ExceptionSeries(count, true);

            record = new ScannerExceptionRecord(
                now,
                ex.GetType().FullName ?? ex.GetType().Name,
                ex.Message,
                context.Stage,
                context.Component,
                context.ScanId,
                context.Cycle,
                context.BatchOffset,
                context.MarketRange,
                context.ScannerState,
                context.IsPausedByMemoryGuard,
                context.IsDisposed,
                stack,
                count,
                fullStackLogged,
                faulted);
            _records.Enqueue(record);
            while (_records.Count > 50) _records.Dequeue();
        }

        log(FormatLogLine(record));
        WriteExport();
        return record;
    }

    public TimeSpan NextBackoff(bool faulted)
    {
        if (faulted) return TimeSpan.FromSeconds(Math.Max(1, _options.ScanErrorMaxBackoffSeconds));
        lock (_gate)
        {
            var current = _currentBackoffSeconds;
            _currentBackoffSeconds = Math.Min(Math.Max(1, _options.ScanErrorMaxBackoffSeconds), Math.Max(1, _currentBackoffSeconds * 2));
            return TimeSpan.FromSeconds(current);
        }
    }

    public void ResetBackoff()
    {
        lock (_gate) _currentBackoffSeconds = Math.Max(1, _options.ScanErrorBackoffSeconds);
    }

    private static string FormatLogLine(ScannerExceptionRecord r)
    {
        var stack = r.FullStackLogged ? Short(r.StackTrace, 4000) : "suppressed_repeated_stack_available_in_export";
        return $"[SCANNER_EXCEPTION] Type={r.Type} Message={Sanitize(r.Message)} Stage={r.Stage} Component={r.Component} ScanId={r.ScanId} Cycle={r.Cycle} BatchOffset={r.BatchOffset} MarketRange={r.MarketRange} ScannerState={r.ScannerState} IsPausedByMemoryGuard={r.IsPausedByMemoryGuard.ToString().ToLowerInvariant()} IsDisposed={r.IsDisposed.ToString().ToLowerInvariant()} SameExceptionCount={r.SameExceptionCount} StackTrace={Sanitize(stack)}";
    }

    private void WriteExport()
    {
        try
        {
            ScannerExceptionRecord[] records;
            lock (_gate) records = _records.ToArray();
            var path = Path.Combine(_contentRootPath, "exports", "scanner-errors-latest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new { timestampUtc = DateTime.UtcNow, errors = records }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Avoid recursive scanner-error storms if the diagnostic export itself fails.
        }
    }

    private static string StableHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string Sanitize(string value) => value.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
    private static string Short(string value, int max) => value.Length <= max ? value : value[..max] + "...";
    private sealed record ExceptionSeries(int Count, bool FullStackLogged);
}
