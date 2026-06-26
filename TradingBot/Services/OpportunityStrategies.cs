using TradingBot.Engines;
using TradingBot.Models;

namespace TradingBot.Services;

public sealed class SingleMarketBuyBothOpportunityStrategy : IOpportunityStrategy
{
    private readonly SingleMarketOrderBookArbEngine _engine;

    public SingleMarketBuyBothOpportunityStrategy(SingleMarketOrderBookArbEngine engine) => _engine = engine;

    public string Name => "SingleMarketBuyBoth";

    public async Task<OpportunityStrategyScanResult> ScanAsync(OpportunityStrategyContext context)
    {
        var paper = (PaperTradingEngine)context.Paper.Engine;
        var stats = await _engine.ScanAsync(
            context.Markets.ToList(),
            paper,
            context.OrderbookSemaphore,
            context.FullCycleId,
            context.IsFullCycleComplete,
            context.SuppressBatchDataQualitySummary,
            context.CancellationToken);

        var rejectedByReason = stats.SkipReasons ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var topSkip = rejectedByReason
            .OrderByDescending(x => x.Value)
            .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return new OpportunityStrategyScanResult(
            Name,
            StrategyMode.PaperEligible,
            Scanned: stats.Scanned,
            Candidates: stats.Candidates,
            ExecutionCandidates: stats.Executed,
            PaperOpened: stats.Executed,
            Books: stats.BookOk,
            BothAsks: stats.BothAsks,
            PositiveEdges: stats.PositiveEdgeFound,
            ExecutionReady: stats.ExecutionReady,
            OrderbookUnavailable: rejectedByReason.Where(x => x.Key.Contains("OrderbookUnavailable", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Value),
            BestEdge: stats.BestEdgeSeen,
            TopSkipReason: topSkip.Key ?? "None",
            TopSkipCount: topSkip.Value,
            RejectedByReason: rejectedByReason,
            SingleMarketCircuitBreakerSkippedMarkets: stats.CircuitBreakerSkippedMarkets,
            SingleMarketCircuitBreakerSkippedCycles: stats.CircuitBreakerSkippedCycles,
            ValidPriced: stats.BestEdgeSeen.HasValue ? 1 : 0,
            InvalidOrUnpriced: Math.Max(0, stats.Candidates - (stats.BestEdgeSeen.HasValue ? 1 : 0)),
            DataQualityRejected: rejectedByReason.Where(x => x.Key.Contains("DataQuality", StringComparison.OrdinalIgnoreCase) || x.Key.Contains("Suspicious", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Value),
            MissingPricing: rejectedByReason.Where(x => x.Key.Contains("MissingYesAsk", StringComparison.OrdinalIgnoreCase) || x.Key.Contains("MissingNoAsk", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Value),
            BestCandidateValid: stats.BestEdgeSeen.HasValue,
            BestCandidatePriced: stats.BestEdgeSeen.HasValue,
            BestCandidateExecutableLike: stats.ExecutionReady > 0,
            BestCandidateReason: stats.BestEdgeSeen.HasValue ? (topSkip.Key ?? "Priced") : (topSkip.Key ?? "N/A"));
    }
}

public sealed class DiagnosticsOnlyOpportunityStrategy : IOpportunityStrategy
{
    private readonly Func<OpportunityStrategyContext, Task<OpportunityStrategyScanResult>> _scan;
    public DiagnosticsOnlyOpportunityStrategy(string name, Func<OpportunityStrategyContext, Task<OpportunityStrategyScanResult>> scan)
    {
        Name = name;
        _scan = scan;
    }

    public string Name { get; }

    public async Task<OpportunityStrategyScanResult> ScanAsync(OpportunityStrategyContext context)
    {
        var result = await _scan(context);
        return result with
        {
            StrategyName = Name,
            Mode = StrategyMode.DiagnosticsOnly,
            PaperOpened = 0,
            DiagnosticsOnlyBlocked = result.StrategyName.Equals("VerifiedMultiOutcome", StringComparison.OrdinalIgnoreCase)
                ? result.DiagnosticsOnlyBlocked
                : Math.Max(result.DiagnosticsOnlyBlocked, result.ExecutionCandidates)
        };
    }
}

public sealed class NullOpportunityStrategy(string name) : IOpportunityStrategy
{
    public string Name { get; } = name;

    public Task<OpportunityStrategyScanResult> ScanAsync(OpportunityStrategyContext context)
        => Task.FromResult(new OpportunityStrategyScanResult(Name, StrategyMode.DiagnosticsOnly, Scanned: context.Markets.Count, TopSkipReason: "DiagnosticsOnly"));
}
