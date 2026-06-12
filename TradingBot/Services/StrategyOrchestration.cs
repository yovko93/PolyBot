using System.Collections.Concurrent;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public enum StrategyMode { Disabled, DiagnosticsOnly, PaperEligible }

public sealed record OpportunityStrategyConfig(bool Enabled = false, StrategyMode Mode = StrategyMode.Disabled, int Priority = 0);

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
    string? LastError = null)
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
            Console.WriteLine($"[STRATEGY_EXECUTION_BLOCKED] Strategy={candidate.StrategyName} Mode={candidate.Mode} MarketOrGroup={candidate.MarketOrGroup} Reason=DiagnosticsOnlyCannotOpenPaper");
            return false;
        }

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

        var tasks = enabled.Select(x => RunOneAsync(x.Strategy, x.Config, context)).ToArray();
        var results = await Task.WhenAll(tasks);
        foreach (var result in results) _recordResult?.Invoke(result);
        return results;
    }

    private async Task<OpportunityStrategyScanResult> RunOneAsync(IOpportunityStrategy strategy, OpportunityStrategyConfig config, OpportunityStrategyContext context)
    {
        try
        {
            var result = await strategy.ScanAsync(context);
            return result with { Mode = config.Mode };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[STRATEGY_FAULT] Strategy={strategy.Name} Mode={config.Mode} Error={ex.Message}");
            return new OpportunityStrategyScanResult(strategy.Name, config.Mode, Faults: 1, LastError: ex.Message);
        }
    }

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
        _lastScanUtc,
        _lastError);
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
    DateTime LastScanUtc,
    string? LastError);
