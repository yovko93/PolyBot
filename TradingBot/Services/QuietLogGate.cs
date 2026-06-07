using System.Collections.Concurrent;

namespace TradingBot.Services;

public sealed record LogEventKey(string Category, string EventName, string? GroupKey = null, string? MarketId = null, string? Strategy = null)
{
    public string EventScope => $"{Category}|{EventName}".ToLowerInvariant();
    public string InstanceScope => $"{Category}|{EventName}|{GroupKey ?? string.Empty}|{MarketId ?? string.Empty}|{Strategy ?? string.Empty}".ToLowerInvariant();
}

public sealed record LogEventFingerprint(string StableHash, string? BucketHash = null, IReadOnlyDictionary<string, string>? MaterialFields = null)
{
    public string MaterialHash
    {
        get
        {
            var fields = MaterialFields is null
                ? string.Empty
                : string.Join(";", MaterialFields.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}={x.Value}"));
            return string.IsNullOrWhiteSpace(BucketHash) && string.IsNullOrWhiteSpace(fields)
                ? StableHash
                : $"bucket:{BucketHash ?? string.Empty}|fields:{fields}";
        }
    }
}

public enum LogImportance
{
    Critical,
    Important,
    Normal,
    Debug
}

public sealed record QuietLogPolicy(
    bool OperationalQuietMode = true,
    int EveryNCycles = 100,
    int EveryMinutes = 10,
    bool SuppressRepeatedHash = true,
    int MaxSameEventPerHour = 3,
    bool DebugEnabled = false);

public sealed record QuietLogGateStats(
    long QuietSuppressedTotal,
    long EmittedLogs,
    IReadOnlyDictionary<string, long> QuietSuppressedByCategory,
    IReadOnlyDictionary<string, long> EmittedByCategory,
    int LogGateCacheSize,
    long CappedSuppressions);

public sealed class QuietLogGate
{
    private readonly object _gate = new();
    private readonly Dictionary<string, QuietLogState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HourlyCounter> _hourly = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _suppressedByCategory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _emittedByCategory = new(StringComparer.OrdinalIgnoreCase);
    private long _quietSuppressedTotal;
    private long _emittedLogs;
    private long _cappedSuppressions;
    private int _maxEntries = 5000;
    private TimeSpan _ttl = TimeSpan.FromMinutes(120);

    public void ConfigureBounds(int maxEntries, TimeSpan ttl)
    {
        lock (_gate)
        {
            _maxEntries = Math.Max(1, maxEntries);
            _ttl = ttl <= TimeSpan.Zero ? TimeSpan.FromMinutes(120) : ttl;
            TrimLocked(DateTime.UtcNow);
        }
    }

    public void TrimExpired()
    {
        lock (_gate) TrimLocked(DateTime.UtcNow);
    }

    public bool ShouldLog(LogEventKey key, LogEventFingerprint fingerprint, LogImportance importance, QuietLogPolicy policy)
    {
        if (importance == LogImportance.Critical)
        {
            RecordEmitted(key.Category);
            return true;
        }

        if (importance == LogImportance.Debug && !policy.DebugEnabled)
        {
            RecordSuppressed(key.Category, capped: false);
            return false;
        }

        if (!policy.OperationalQuietMode)
        {
            RecordEmitted(key.Category);
            return true;
        }

        var now = DateTime.UtcNow;
        var stateKey = key.InstanceScope;
        var eventScope = key.EventScope;
        var comparisonHash = importance == LogImportance.Important ? fingerprint.StableHash : fingerprint.MaterialHash;

        lock (_gate)
        {
            TrimLocked(now);
            if (!_states.TryGetValue(stateKey, out var state))
            {
                state = new QuietLogState();
                _states[stateKey] = state;
            }

            state.Cycles++;
            state.LastTouchedAtUtc = now;
            var first = state.Cycles == 1;
            var changed = !string.Equals(state.LastComparisonHash, comparisonHash, StringComparison.OrdinalIgnoreCase);
            var stableChanged = !string.Equals(state.LastStableHash, fingerprint.StableHash, StringComparison.OrdinalIgnoreCase);
            var periodicCycle = policy.EveryNCycles > 0 && state.Cycles % policy.EveryNCycles == 0;
            var periodicTime = policy.EveryMinutes > 0 && (state.LastEmittedAtUtc == DateTime.MinValue || now - state.LastEmittedAtUtc >= TimeSpan.FromMinutes(policy.EveryMinutes));

            var shouldLog = first || changed || periodicCycle || periodicTime;
            if (importance == LogImportance.Important)
                shouldLog = first || stableChanged || periodicCycle || periodicTime;

            if (policy.SuppressRepeatedHash && !shouldLog)
            {
                RecordSuppressedUnderLock(key.Category, capped: false);
                return false;
            }

            var maxPerHour = Math.Max(0, policy.MaxSameEventPerHour);
            if (maxPerHour > 0)
            {
                if (!_hourly.TryGetValue(eventScope, out var counter) || now - counter.WindowStartUtc >= TimeSpan.FromHours(1))
                {
                    counter = new HourlyCounter(now, 0);
                    _hourly[eventScope] = counter;
                }

                if (counter.Count >= maxPerHour)
                {
                    RecordSuppressedUnderLock(key.Category, capped: true);
                    return false;
                }

                _hourly[eventScope] = counter with { Count = counter.Count + 1 };
            }

            state.LastStableHash = fingerprint.StableHash;
            state.LastComparisonHash = comparisonHash;
            state.LastEmittedAtUtc = now;
            RecordEmittedUnderLock(key.Category);
            return true;
        }
    }

    public QuietLogGateStats Snapshot()
    {
        lock (_gate)
        {
            TrimLocked(DateTime.UtcNow);
            return new QuietLogGateStats(
                _quietSuppressedTotal,
                _emittedLogs,
                new Dictionary<string, long>(_suppressedByCategory, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, long>(_emittedByCategory, StringComparer.OrdinalIgnoreCase),
                _states.Count + _hourly.Count,
                _cappedSuppressions);
        }
    }


    private void TrimLocked(DateTime now)
    {
        var cutoff = now - _ttl;
        foreach (var key in _states.Where(x => x.Value.LastTouchedAtUtc < cutoff).Select(x => x.Key).ToList())
            _states.Remove(key);
        foreach (var key in _hourly.Where(x => x.Value.WindowStartUtc < cutoff).Select(x => x.Key).ToList())
            _hourly.Remove(key);
        while (_states.Count + _hourly.Count > _maxEntries && _states.Count > 0)
        {
            var oldest = _states.OrderBy(x => x.Value.LastTouchedAtUtc).First().Key;
            _states.Remove(oldest);
        }
        while (_states.Count + _hourly.Count > _maxEntries && _hourly.Count > 0)
        {
            var oldest = _hourly.OrderBy(x => x.Value.WindowStartUtc).First().Key;
            _hourly.Remove(oldest);
        }
    }

    private void RecordSuppressed(string category, bool capped)
    {
        lock (_gate) RecordSuppressedUnderLock(category, capped);
    }

    private void RecordEmitted(string category)
    {
        lock (_gate) RecordEmittedUnderLock(category);
    }

    private void RecordSuppressedUnderLock(string category, bool capped)
    {
        _quietSuppressedTotal++;
        if (capped) _cappedSuppressions++;
        _suppressedByCategory[category] = _suppressedByCategory.TryGetValue(category, out var v) ? v + 1 : 1;
    }

    private void RecordEmittedUnderLock(string category)
    {
        _emittedLogs++;
        _emittedByCategory[category] = _emittedByCategory.TryGetValue(category, out var v) ? v + 1 : 1;
    }

    private sealed class QuietLogState
    {
        public int Cycles { get; set; }
        public DateTime LastTouchedAtUtc { get; set; } = DateTime.UtcNow;
        public string LastStableHash { get; set; } = string.Empty;
        public string LastComparisonHash { get; set; } = string.Empty;
        public DateTime LastEmittedAtUtc { get; set; } = DateTime.MinValue;
    }

    private sealed record HourlyCounter(DateTime WindowStartUtc, int Count);
}
