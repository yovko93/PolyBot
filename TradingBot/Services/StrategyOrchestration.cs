using System.Collections.Concurrent;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public enum StrategyMode { Disabled, DiagnosticsOnly, PaperEligible }

public sealed record OpportunityStrategyConfig(bool Enabled = false, StrategyMode Mode = StrategyMode.Disabled, int Priority = 0)
{
    public bool AllowPaperEligible { get; set; } = false;
    public decimal MaxPaperNotionalPerTrade { get; set; } = 25m;
    public int MaxPaperOpenPerHour { get; set; } = 1;
    public decimal MaxTotalVerifiedExposure { get; set; } = 50m;
    public bool RequireWouldOpenDryRunObserved { get; set; } = true;
    public int RequireConsecutiveStableSignals { get; set; } = 5;
}

public sealed record OpportunityStrategyContext(
    IReadOnlyList<Market> Markets,
    PaperTradingEngineFacade Paper,
    SemaphoreSlim OrderbookSemaphore,
    long? FullCycleId,
    bool IsFullCycleComplete,
    bool SuppressBatchDataQualitySummary,
    CancellationToken CancellationToken);

public sealed record OpportunityStrategyScanResult(
    string StrategyName,
    StrategyMode Mode,
    int Scanned = 0,
    int Candidates = 0,
    int ExecutionCandidates = 0,
    int PaperOpened = 0,
    int DiagnosticsOnlyBlocked = 0,
    int Faults = 0,
    string? LastError = null,
    int Books = 0,
    int BothAsks = 0,
    int PositiveEdges = 0,
    int ExecutionReady = 0,
    int OrderbookUnavailable = 0,
    decimal? BestEdge = null,
    string TopSkipReason = "None",
    int TopSkipCount = 0,
    IReadOnlyDictionary<string, int>? RejectedByReason = null,
    int VerifiedActiveConservativePositive = 0,
    int VerifiedActiveConservativeExecutable = 0,
    int VerifiedRawPositiveOnly = 0,
    int VerifiedAlternateProfilePositive = 0,
    int VerifiedExperimentalProfileCandidate = 0,
    int VerifiedDiagnosticsOnlyBlocked = 0,
    int VerifiedWouldOpenIfPaperEligible = 0,
    int VerifiedRejectedByCostProfile = 0,
    int VerifiedRejectedByStability = 0,
    int VerifiedRejectedByMissingNoAsk = 0,
    int VerifiedRejectedByUnresolvedGroup = 0,
    int VerifiedRejectedByRisk = 0,
    int VerifiedWouldOpenBlockedByFill = 0,
    int VerifiedWouldOpenBlockedByDepth = 0,
    int VerifiedWouldOpenBlockedByUnknown = 0)
{
    public static OpportunityStrategyScanResult Disabled(string strategyName) => new(strategyName, StrategyMode.Disabled);
}

public interface IOpportunityStrategy
{
    string Name { get; }
    Task<OpportunityStrategyScanResult> ScanAsync(OpportunityStrategyContext context);
}

public sealed record OpportunityExecutionCandidate(
    string StrategyName,
    StrategyMode Mode,
    string MarketOrGroup,
    decimal Notional,
    Func<CancellationToken, Task<bool>> ExecuteAsync);

public sealed class OpportunityExecutionQueue
{
    private readonly SemaphoreSlim _serializedExecution = new(1, 1);
    private long _enqueued;
    private long _executed;
    private long _rejectedDiagnosticsOnly;
    private long _failed;
    private int _maxObservedConcurrency;
    private int _currentConcurrency;

    public long Enqueued => Interlocked.Read(ref _enqueued);
    public long Executed => Interlocked.Read(ref _executed);
    public long RejectedDiagnosticsOnly => Interlocked.Read(ref _rejectedDiagnosticsOnly);
    public long Failed => Interlocked.Read(ref _failed);
    public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

    public async Task<bool> EnqueueAsync(OpportunityExecutionCandidate candidate, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _enqueued);
        if (candidate.Mode != StrategyMode.PaperEligible)
        {
            Interlocked.Increment(ref _rejectedDiagnosticsOnly);
            Console.WriteLine($"[STRATEGY_EXECUTION_SKIPPED] Strategy={candidate.StrategyName} Mode={candidate.Mode} MarketOrGroup={candidate.MarketOrGroup} Reason=DiagnosticsOnlyCannotOpenPaper");
            Console.WriteLine($"[STRATEGY_EXECUTION_BLOCKED] Strategy={candidate.StrategyName} Mode={candidate.Mode} MarketOrGroup={candidate.MarketOrGroup} Reason=DiagnosticsOnlyCannotOpenPaper");
            return false;
        }

        Console.WriteLine($"[STRATEGY_EXECUTION_CANDIDATE] Strategy={candidate.StrategyName} Mode={candidate.Mode} MarketOrGroup={candidate.MarketOrGroup} Notional={candidate.Notional:0.####}");
        await _serializedExecution.WaitAsync(ct);
        var concurrent = Interlocked.Increment(ref _currentConcurrency);
        UpdateMaxConcurrency(concurrent);
        try
        {
            var ok = await candidate.ExecuteAsync(ct);
            if (ok) Interlocked.Increment(ref _executed);
            else Interlocked.Increment(ref _failed);
            return ok;
        }
        finally
        {
            Interlocked.Decrement(ref _currentConcurrency);
            _serializedExecution.Release();
        }
    }

    private void UpdateMaxConcurrency(int observed)
    {
        while (true)
        {
            var current = Volatile.Read(ref _maxObservedConcurrency);
            if (observed <= current) return;
            if (Interlocked.CompareExchange(ref _maxObservedConcurrency, observed, current) == current) return;
        }
    }
}

public sealed class StrategyOrchestrator
{
    private readonly IReadOnlyList<IOpportunityStrategy> _strategies;
    private readonly TradingBotOptions _options;
    private readonly Action<OpportunityStrategyScanResult>? _recordResult;
    private readonly Dictionary<string, DateTime> _lastSummaryAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _startLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _resultLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _diagnosticsOnlyLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _lastDiagnosticsSummaryAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastDiagnosticsFingerprint = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _logGate = new();

    public StrategyOrchestrator(IEnumerable<IOpportunityStrategy> strategies, TradingBotOptions options, Action<OpportunityStrategyScanResult>? recordResult = null)
    {
        _strategies = strategies.ToArray();
        _options = options;
        _recordResult = recordResult;
    }

    public async Task<IReadOnlyList<OpportunityStrategyScanResult>> RunEnabledAsync(OpportunityStrategyContext context)
    {
        var configured = _options.Strategies;
        var enabled = _strategies
            .Select(s => (Strategy: s, Config: ResolveConfig(configured, s.Name)))
            .Where(x => x.Config.Enabled && x.Config.Mode != StrategyMode.Disabled)
            .OrderByDescending(x => x.Config.Priority)
            .ToArray();

        var tasks = enabled.Select(x =>
        {
            LogScanStart(x.Strategy.Name, x.Config, context.Markets.Count);
            return RunOneAsync(x.Strategy, x.Config, context);
        }).ToArray();
        var results = await Task.WhenAll(tasks);
        foreach (var result in results) _recordResult?.Invoke(result);
        return results;
    }

    private async Task<OpportunityStrategyScanResult> RunOneAsync(IOpportunityStrategy strategy, OpportunityStrategyConfig config, OpportunityStrategyContext context)
    {
        try
        {
            var result = await strategy.ScanAsync(context);
            result = result with { Mode = config.Mode };
            LogScanResult(result, config);
            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[STRATEGY_FAULT] Strategy={strategy.Name} Mode={config.Mode} Error={ex.Message}");
            var result = new OpportunityStrategyScanResult(strategy.Name, config.Mode, Faults: 1, LastError: ex.Message, TopSkipReason: "Fault");
            LogScanResult(result, config);
            return result;
        }
    }

    public void RecordExternalResult(OpportunityStrategyScanResult result, int scannedItems)
    {
        var config = ResolveConfig(_options.Strategies, result.StrategyName);
        if (!config.Enabled || config.Mode == StrategyMode.Disabled) return;
        LogScanStart(result.StrategyName, config, scannedItems);
        result = result with { Mode = config.Mode };
        LogScanResult(result, config);
        _recordResult?.Invoke(result);
    }

    private void LogScanStart(string strategyName, OpportunityStrategyConfig config, int scannedItems)
    {
        var shouldLog = !_options.Diagnostics.OperationalQuietMode;
        lock (_logGate) shouldLog = shouldLog || _startLogged.Add(strategyName);
        if (shouldLog)
            Console.WriteLine($"[STRATEGY_SCAN_START] Strategy={strategyName} Mode={config.Mode} PaperEligible={(config.Mode == StrategyMode.PaperEligible).ToString().ToLowerInvariant()} Markets={scannedItems} Priority={config.Priority}");
        if (config.Mode == StrategyMode.DiagnosticsOnly)
        {
            var shouldLogDiagnostics = false;
            lock (_logGate) shouldLogDiagnostics = _diagnosticsOnlyLogged.Add(strategyName);
            if (shouldLogDiagnostics)
                Console.WriteLine($"[STRATEGY_DIAGNOSTICS_ONLY] Strategy={strategyName} Reason=ModeDiagnosticsOnly");
        }
    }

    private void LogScanResult(OpportunityStrategyScanResult result, OpportunityStrategyConfig config)
    {
        var shouldLog = !_options.Diagnostics.OperationalQuietMode;
        lock (_logGate) shouldLog = shouldLog || _resultLogged.Add(result.StrategyName);
        if (shouldLog)
        {
            Console.WriteLine($"[STRATEGY_SCAN_RESULT] Strategy={result.StrategyName} Mode={config.Mode} PaperEligible={(config.Mode == StrategyMode.PaperEligible).ToString().ToLowerInvariant()} Scanned={result.Scanned} Books={result.Books} BothAsks={result.BothAsks} Candidates={result.Candidates} Positive={result.PositiveEdges} ExecutionReady={result.ExecutionReady} PaperOpened={result.PaperOpened} ExecutionCandidates={result.ExecutionCandidates} ExecutionCandidatesSuppressed={result.DiagnosticsOnlyBlocked} OrderbookUnavailable={result.OrderbookUnavailable} BestEdge={(result.BestEdge.HasValue ? result.BestEdge.Value.ToString("0.####") : "N/A")} TopSkip={result.TopSkipReason}:{result.TopSkipCount} Faults={result.Faults}");
        }

        var now = DateTime.UtcNow;
        var summaryDue = false;
        lock (_logGate)
        {
            if (!_lastSummaryAt.TryGetValue(result.StrategyName, out var last) || now - last >= TimeSpan.FromMinutes(Math.Max(1, _options.Logging.LogScannerSummaryEveryMinutes)))
            {
                _lastSummaryAt[result.StrategyName] = now;
                summaryDue = true;
            }
        }
        if (config.Mode == StrategyMode.DiagnosticsOnly && result.DiagnosticsOnlyBlocked > 0)
        {
            var diagnosticsFingerprint = $"{result.TopSkipReason}|{EdgeBucket(result.BestEdge)}|{result.VerifiedWouldOpenIfPaperEligible}|{result.DiagnosticsOnlyBlocked}";
            var shouldDiagnosticsLog = false;
            var shouldDiagnosticsSummary = false;
            lock (_logGate)
            {
                var key = result.StrategyName;
                var changed = !_lastDiagnosticsFingerprint.TryGetValue(key, out var prev) || prev != diagnosticsFingerprint;
                _lastDiagnosticsFingerprint[key] = diagnosticsFingerprint;
                var first = _diagnosticsOnlyLogged.Add($"{result.StrategyName}:suppressed");
                var due = !_lastDiagnosticsSummaryAt.TryGetValue(key, out var last) || now - last >= TimeSpan.FromMinutes(10);
                shouldDiagnosticsLog = first || changed;
                shouldDiagnosticsSummary = due;
                if (due) _lastDiagnosticsSummaryAt[key] = now;
            }
            if (shouldDiagnosticsLog)
                Console.WriteLine($"[STRATEGY_DIAGNOSTICS_ONLY] Strategy={result.StrategyName} CandidatesSuppressed={result.DiagnosticsOnlyBlocked} Reason=ModeDiagnosticsOnly");
            if (shouldDiagnosticsSummary)
                Console.WriteLine($"[STRATEGY_DIAGNOSTICS_ONLY_SUMMARY] Strategy={result.StrategyName} CandidatesSuppressed={result.DiagnosticsOnlyBlocked} ActiveConservativeExecutable={result.VerifiedActiveConservativeExecutable} RawPositiveOnly={result.VerifiedRawPositiveOnly} AlternateProfilePositive={result.VerifiedAlternateProfilePositive} ExperimentalProfileCandidate={result.VerifiedExperimentalProfileCandidate} WouldOpenIfPaperEligible={result.VerifiedWouldOpenIfPaperEligible}");
        }
        if (summaryDue)
            Console.WriteLine($"[STRATEGY_SUMMARY] Strategy={result.StrategyName} Mode={config.Mode} Scanned={result.Scanned} Books={result.Books} Candidates={result.Candidates} Positive={result.PositiveEdges} ExecutionReady={result.ExecutionReady} PaperOpened={result.PaperOpened} RejectedByReason={FormatReasons(result.RejectedByReason)}");
    }

    private static string EdgeBucket(decimal? edge) => edge.HasValue ? Math.Round(edge.Value, 3).ToString("0.###") : "N/A";

    private static string FormatReasons(IReadOnlyDictionary<string, int>? reasons)
        => reasons is null || reasons.Count == 0
            ? "None"
            : string.Join("|", reasons.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value}"));

    public static OpportunityStrategyConfig ResolveConfig(IReadOnlyDictionary<string, OpportunityStrategyConfig> strategies, string name)
        => strategies.TryGetValue(name, out var config) ? config : new OpportunityStrategyConfig(false, StrategyMode.Disabled, 0);
}

// Small indirection keeps StrategyOrchestrator independent from the concrete paper engine in unit tests.
public sealed class PaperTradingEngineFacade
{
    public required object Engine { get; init; }
}

public sealed class StrategyRuntimeCounters
{
    private long _scanned;
    private long _candidates;
    private long _executionCandidates;
    private long _paperOpened;
    private long _diagnosticsOnlyBlocked;
    private long _faults;
    private long _books;
    private long _bothAsks;
    private long _positiveEdges;
    private long _executionReady;
    private long _orderbookUnavailable;
    private long _verifiedActiveConservativePositive;
    private long _verifiedActiveConservativeExecutable;
    private long _verifiedRawPositiveOnly;
    private long _verifiedAlternateProfilePositive;
    private long _verifiedExperimentalProfileCandidate;
    private long _verifiedDiagnosticsOnlyBlocked;
    private long _verifiedWouldOpenIfPaperEligible;
    private long _verifiedRejectedByCostProfile;
    private long _verifiedRejectedByStability;
    private long _verifiedRejectedByMissingNoAsk;
    private long _verifiedRejectedByUnresolvedGroup;
    private long _verifiedRejectedByRisk;
    private long _verifiedWouldOpenBlockedByFill;
    private long _verifiedWouldOpenBlockedByDepth;
    private long _verifiedWouldOpenBlockedByUnknown;
    private readonly object _reasonGate = new();
    private readonly Dictionary<string, long> _rejectedByReason = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _bestEdgeGate = new();
    private decimal? _bestEdge;
    private string _topSkipReason = "None";
    private int _topSkipCount;
    private DateTime _lastScanUtc;
    private string? _lastError;
    private StrategyMode _mode;

    public void Add(OpportunityStrategyScanResult result)
    {
        _mode = result.Mode;
        _lastScanUtc = DateTime.UtcNow;
        _lastError = result.LastError;
        Interlocked.Add(ref _scanned, result.Scanned);
        Interlocked.Add(ref _candidates, result.Candidates);
        Interlocked.Add(ref _executionCandidates, result.ExecutionCandidates);
        Interlocked.Add(ref _paperOpened, result.PaperOpened);
        Interlocked.Add(ref _diagnosticsOnlyBlocked, result.DiagnosticsOnlyBlocked);
        Interlocked.Add(ref _faults, result.Faults);
        Interlocked.Add(ref _books, result.Books);
        Interlocked.Add(ref _bothAsks, result.BothAsks);
        Interlocked.Add(ref _positiveEdges, result.PositiveEdges);
        Interlocked.Add(ref _executionReady, result.ExecutionReady);
        Interlocked.Add(ref _orderbookUnavailable, result.OrderbookUnavailable);
        Interlocked.Add(ref _verifiedActiveConservativePositive, result.VerifiedActiveConservativePositive);
        Interlocked.Add(ref _verifiedActiveConservativeExecutable, result.VerifiedActiveConservativeExecutable);
        Interlocked.Add(ref _verifiedRawPositiveOnly, result.VerifiedRawPositiveOnly);
        Interlocked.Add(ref _verifiedAlternateProfilePositive, result.VerifiedAlternateProfilePositive);
        Interlocked.Add(ref _verifiedExperimentalProfileCandidate, result.VerifiedExperimentalProfileCandidate);
        Interlocked.Add(ref _verifiedDiagnosticsOnlyBlocked, result.VerifiedDiagnosticsOnlyBlocked);
        Interlocked.Add(ref _verifiedWouldOpenIfPaperEligible, result.VerifiedWouldOpenIfPaperEligible);
        Interlocked.Add(ref _verifiedRejectedByCostProfile, result.VerifiedRejectedByCostProfile);
        Interlocked.Add(ref _verifiedRejectedByStability, result.VerifiedRejectedByStability);
        Interlocked.Add(ref _verifiedRejectedByMissingNoAsk, result.VerifiedRejectedByMissingNoAsk);
        Interlocked.Add(ref _verifiedRejectedByUnresolvedGroup, result.VerifiedRejectedByUnresolvedGroup);
        Interlocked.Add(ref _verifiedRejectedByRisk, result.VerifiedRejectedByRisk);
        Interlocked.Add(ref _verifiedWouldOpenBlockedByFill, result.VerifiedWouldOpenBlockedByFill);
        Interlocked.Add(ref _verifiedWouldOpenBlockedByDepth, result.VerifiedWouldOpenBlockedByDepth);
        Interlocked.Add(ref _verifiedWouldOpenBlockedByUnknown, result.VerifiedWouldOpenBlockedByUnknown);
        if (result.BestEdge.HasValue)
        {
            lock (_bestEdgeGate)
            {
                if (!_bestEdge.HasValue || result.BestEdge.Value > _bestEdge.Value) _bestEdge = result.BestEdge.Value;
            }
        }
        _topSkipReason = result.TopSkipReason;
        _topSkipCount = result.TopSkipCount;
        if (result.RejectedByReason is { Count: > 0 })
        {
            lock (_reasonGate)
            {
                foreach (var (reason, count) in result.RejectedByReason)
                    _rejectedByReason[reason] = _rejectedByReason.TryGetValue(reason, out var existing) ? existing + count : count;
            }
        }
    }

    public StrategyRuntimeCounterSnapshot Snapshot(string strategyName) => new(
        strategyName,
        _mode.ToString(),
        Interlocked.Read(ref _scanned),
        Interlocked.Read(ref _candidates),
        Interlocked.Read(ref _executionCandidates),
        Interlocked.Read(ref _paperOpened),
        Interlocked.Read(ref _diagnosticsOnlyBlocked),
        Interlocked.Read(ref _faults),
        Interlocked.Read(ref _books),
        Interlocked.Read(ref _bothAsks),
        Interlocked.Read(ref _positiveEdges),
        Interlocked.Read(ref _executionReady),
        Interlocked.Read(ref _orderbookUnavailable),
        RejectedReasonsSnapshot(),
        BestEdgeSnapshot(),
        _topSkipReason,
        _topSkipCount,
        _lastScanUtc,
        _lastError,
        Interlocked.Read(ref _verifiedActiveConservativePositive),
        Interlocked.Read(ref _verifiedActiveConservativeExecutable),
        Interlocked.Read(ref _verifiedRawPositiveOnly),
        Interlocked.Read(ref _verifiedAlternateProfilePositive),
        Interlocked.Read(ref _verifiedExperimentalProfileCandidate),
        Interlocked.Read(ref _verifiedDiagnosticsOnlyBlocked),
        Interlocked.Read(ref _verifiedWouldOpenIfPaperEligible),
        Interlocked.Read(ref _verifiedRejectedByCostProfile),
        Interlocked.Read(ref _verifiedRejectedByStability),
        Interlocked.Read(ref _verifiedRejectedByMissingNoAsk),
        Interlocked.Read(ref _verifiedRejectedByUnresolvedGroup),
        Interlocked.Read(ref _verifiedRejectedByRisk),
        Interlocked.Read(ref _verifiedWouldOpenBlockedByFill),
        Interlocked.Read(ref _verifiedWouldOpenBlockedByDepth),
        Interlocked.Read(ref _verifiedWouldOpenBlockedByUnknown));

    private decimal? BestEdgeSnapshot()
    {
        lock (_bestEdgeGate) return _bestEdge;
    }

    private IReadOnlyDictionary<string, long> RejectedReasonsSnapshot()
    {
        lock (_reasonGate) return _rejectedByReason.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
    }
}

public sealed record StrategyRuntimeCounterSnapshot(
    string StrategyName,
    string Mode,
    long Scanned,
    long Candidates,
    long ExecutionCandidates,
    long PaperOpened,
    long DiagnosticsOnlyBlocked,
    long Faults,
    long Books,
    long BothAsks,
    long PositiveEdges,
    long ExecutionReady,
    long OrderbookUnavailable,
    IReadOnlyDictionary<string, long> RejectedByReason,
    decimal? BestEdge,
    string TopSkipReason,
    int TopSkipCount,
    DateTime LastScanUtc,
    string? LastError,
    long VerifiedActiveConservativePositive = 0,
    long VerifiedActiveConservativeExecutable = 0,
    long VerifiedRawPositiveOnly = 0,
    long VerifiedAlternateProfilePositive = 0,
    long VerifiedExperimentalProfileCandidate = 0,
    long VerifiedDiagnosticsOnlyBlocked = 0,
    long VerifiedWouldOpenIfPaperEligible = 0,
    long VerifiedRejectedByCostProfile = 0,
    long VerifiedRejectedByStability = 0,
    long VerifiedRejectedByMissingNoAsk = 0,
    long VerifiedRejectedByUnresolvedGroup = 0,
    long VerifiedRejectedByRisk = 0,
    long VerifiedWouldOpenBlockedByFill = 0,
    long VerifiedWouldOpenBlockedByDepth = 0,
    long VerifiedWouldOpenBlockedByUnknown = 0);
