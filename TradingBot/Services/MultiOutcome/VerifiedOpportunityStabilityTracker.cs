using System.Text.Json;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services.MultiOutcome;

namespace TradingBot.Services;

public enum VerifiedBasketState { NotExecutable, NearExecutable, EdgeExecutablePending, EdgeStable, ExecutionReadinessPending, ExecutionStable, PaperOpened, SuppressedDuplicate }

public sealed record VerifiedBasketEdgeSample(DateTime Timestamp, decimal GrossEdge, decimal ConservativeNet, decimal PolymarketApproxNet, decimal RawOnlyNet, bool Executable, string Classification, string TopReject, decimal NoAskSum, string LimitingLeg);

public sealed record ExecutionReadinessSample(
    string GroupKey,
    DateTime Timestamp,
    decimal NetEdge,
    decimal CostPerBasket,
    decimal MaxQtyByNotional,
    decimal MaxQtyByLiquidity,
    decimal PlannedQty,
    decimal PlannedCost,
    decimal PlannedExpectedProfit,
    string LimitingFactor,
    bool Ready,
    string? NotReadyReason,
    int ConsecutiveReadyScans,
    int RequiredConsecutiveReadyScans,
    VerifiedBasketState State,
    bool Reset,
    int PreviousReadyScans);

public sealed record ExecutionReadinessHistorySummary(
    string GroupKey,
    VerifiedBasketState State,
    ExecutionReadinessSample? LatestReadinessSample,
    int ConsecutiveReadyScans,
    int RequiredConsecutiveReadyScans,
    string? NotReadyReason,
    IReadOnlyList<ExecutionReadinessSample> Samples);

public sealed record VerifiedBasketHistorySummary(string GroupKey, IReadOnlyList<VerifiedBasketEdgeSample> Samples, decimal MinNet, decimal MaxNet, decimal AverageNet, decimal LastNet, int ConsecutiveExecutableScans, DateTime? LastExecutableAt, decimal Volatility, VerifiedBasketState CurrentState);

public sealed class VerifiedOpportunityStabilityTracker
{
    private readonly Dictionary<string, List<VerifiedBasketEdgeSample>> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _consecutive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VerifiedBasketState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime?> _lastExec = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ExecutionReadinessSample>> _readinessHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _consecutiveReady = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _stateChangedAt = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _lastResetReason = new(StringComparer.OrdinalIgnoreCase);

    public VerifiedBasketState Track(string groupKey, VerifiedBasketScreener.ScreenResult row, int maxHistory, int requiredConsecutive, decimal minStableNet, decimal maxVol)
    {
        var poly = row.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? row.ActiveProfileNetEdge;
        var raw = row.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals("RawOnly", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? row.ActiveProfileNetEdge;
        var sample = new VerifiedBasketEdgeSample(DateTime.UtcNow, row.GrossEdge, row.ActiveProfileNetEdge, poly, raw, row.ActiveProfileNetEdge > 0m, row.Classification, row.TopMissingReason, row.NoAskSum, row.QuantityScenarios.FirstOrDefault()?.LimitingLeg ?? "None");
        if (!_history.TryGetValue(groupKey, out var list)) { list = []; _history[groupKey] = list; }
        list.Add(sample);
        while (list.Count > maxHistory) list.RemoveAt(0);

        var positive = row.ActiveProfileNetEdge >= minStableNet;
        _consecutive.TryGetValue(groupKey, out var c);
        if (positive) c++; else c = 0;
        _consecutive[groupKey] = c;
        if (positive) _lastExec[groupKey] = DateTime.UtcNow;

        var vol = ComputeVolatility(list);
        var edgeState = !positive ? (row.NearExecutable ? VerifiedBasketState.NearExecutable : VerifiedBasketState.NotExecutable)
            : (c >= requiredConsecutive && vol <= maxVol ? VerifiedBasketState.EdgeStable : VerifiedBasketState.EdgeExecutablePending);
        var resetReason = !positive ? "BelowThreshold"
            : vol > maxVol ? "NetEdgeVolatility"
            : edgeState == VerifiedBasketState.EdgeExecutablePending ? "AwaitingConsecutiveScans"
            : "None";
        _lastResetReason[groupKey] = resetReason;

        var current = State(groupKey);
        var st = edgeState == VerifiedBasketState.EdgeStable && current is VerifiedBasketState.ExecutionReadinessPending or VerifiedBasketState.ExecutionStable or VerifiedBasketState.PaperOpened or VerifiedBasketState.SuppressedDuplicate
            ? current
            : edgeState;
        if (st is VerifiedBasketState.NotExecutable or VerifiedBasketState.NearExecutable or VerifiedBasketState.EdgeExecutablePending)
            _consecutiveReady[groupKey] = 0;
        if (current != st || !_stateChangedAt.ContainsKey(groupKey)) _stateChangedAt[groupKey] = DateTime.UtcNow;
        _state[groupKey] = st;
        return st;
    }

    public ExecutionReadinessSample TrackExecutionReadiness(VerifiedMultiOutcomeOpportunity opp, ExecutionOptions options, bool hasOpenDuplicate, int maxHistory = 20)
    {
        var costPerBasket = opp.Legs.Sum(x => x.NoAsk);
        var maxQtyByNotional = costPerBasket > 0 ? options.MaxNotionalPerBasket / costPerBasket : 0m;
        var maxQtyByLiquidity = opp.Legs.Count > 0 ? opp.Legs.Min(x => x.NoAskQuantity) : 0m;
        var plannedQty = Math.Min(maxQtyByNotional, maxQtyByLiquidity);
        var plannedCost = plannedQty * costPerBasket;
        var plannedExpectedProfit = plannedQty * opp.NetEdge;
        var limitingFactor = maxQtyByNotional <= maxQtyByLiquidity ? "Notional" : "Liquidity";
        var previousReady = ConsecutiveExecutionReady(opp.GroupKey);
        var previous = LatestReadiness(opp.GroupKey);
        var reason = GetReadinessRejectionReason(opp, options, hasOpenDuplicate, costPerBasket, plannedQty, plannedCost, plannedExpectedProfit);
        if (reason is null && previous is { Ready: true })
        {
            if (RatioDelta(plannedQty, previous.PlannedQty) > options.MaxPlannedQtyVolatilityRatio) reason = "PlannedQtyVolatilityExceeded";
            else if (RatioDelta(plannedCost, previous.PlannedCost) > options.MaxPlannedCostVolatilityRatio) reason = "PlannedCostVolatilityExceeded";
            else if (Math.Abs(opp.NetEdge - previous.NetEdge) > options.MaxNetEdgeVolatility) reason = "NetEdgeVolatilityExceeded";
        }

        var ready = reason is null;
        var consecutive = ready ? previousReady + 1 : 0;
        _consecutiveReady[opp.GroupKey] = consecutive;
        var state = ready && (!options.RequireStableExecutionReadiness || consecutive >= options.RequiredConsecutiveExecutionReadyScans)
            ? VerifiedBasketState.ExecutionStable
            : ready ? VerifiedBasketState.ExecutionReadinessPending : VerifiedBasketState.EdgeStable;
        if (State(opp.GroupKey) != state || !_stateChangedAt.ContainsKey(opp.GroupKey)) _stateChangedAt[opp.GroupKey] = DateTime.UtcNow;
        _state[opp.GroupKey] = state;
        _lastResetReason[opp.GroupKey] = reason ?? "None";
        var reset = !ready && previousReady > 0;
        var sample = new ExecutionReadinessSample(opp.GroupKey, DateTime.UtcNow, opp.NetEdge, costPerBasket, maxQtyByNotional, maxQtyByLiquidity, plannedQty, plannedCost, plannedExpectedProfit, limitingFactor, ready, reason, consecutive, options.RequiredConsecutiveExecutionReadyScans, state, reset, previousReady);
        if (!_readinessHistory.TryGetValue(opp.GroupKey, out var list)) { list = []; _readinessHistory[opp.GroupKey] = list; }
        list.Add(sample);
        while (list.Count > maxHistory) list.RemoveAt(0);
        return sample;
    }

    private static string? GetReadinessRejectionReason(VerifiedMultiOutcomeOpportunity opp, ExecutionOptions options, bool hasOpenDuplicate, decimal costPerBasket, decimal plannedQty, decimal plannedCost, decimal plannedExpectedProfit)
    {
        if (hasOpenDuplicate) return "DuplicateOpenPosition";
        if (opp.NetEdge < options.MinStableNetEdgePerBasket) return "NetEdgeBelowMinimum";
        if (opp.Legs.Any(x => string.IsNullOrWhiteSpace(x.NoTokenId))) return "MissingNoTokenId";
        if (opp.Legs.Any(x => x.NoAsk <= 0m || x.NoAsk > 1m)) return "MissingOrInvalidNoAsk";
        if (costPerBasket <= 0m) return "MissingPrices";
        if (plannedQty < options.MinPlannedBasketQty) return "PlannedQtyBelowMinimum";
        if (plannedCost < options.MinPlannedNotional) return "PlannedNotionalBelowMinimum";
        if (plannedExpectedProfit < options.MinPlannedExpectedProfit) return "PlannedExpectedProfitBelowMinimum";
        if (opp.Legs.Any(x => x.NoAskQuantity < plannedQty)) return "InsufficientLiquidity";
        return null;
    }

    private static decimal RatioDelta(decimal current, decimal previous) => previous <= 0m ? (current == previous ? 0m : decimal.MaxValue) : Math.Abs(current - previous) / previous;

    public int Consecutive(string groupKey) => _consecutive.TryGetValue(groupKey, out var c) ? c : 0;
    public int ConsecutiveExecutionReady(string groupKey) => _consecutiveReady.TryGetValue(groupKey, out var c) ? c : 0;
    public DateTime? LastExecutableAt(string groupKey) => _lastExec.TryGetValue(groupKey, out var v) ? v : null;
    public VerifiedBasketState State(string groupKey) => _state.TryGetValue(groupKey, out var s) ? s : VerifiedBasketState.NotExecutable;
    public TimeSpan StateAge(string groupKey) => _stateChangedAt.TryGetValue(groupKey, out var t) ? DateTime.UtcNow - t : TimeSpan.Zero;
    public string LastResetReason(string groupKey) => _lastResetReason.TryGetValue(groupKey, out var r) ? r : "None";
    public ExecutionReadinessSample? LatestReadiness(string groupKey) => _readinessHistory.TryGetValue(groupKey, out var l) ? l.LastOrDefault() : null;
    public decimal Volatility(string groupKey) => _history.TryGetValue(groupKey, out var l) ? ComputeVolatility(l) : 0m;
    public IReadOnlyList<ExecutionReadinessHistorySummary> ReadinessSummaries(int requiredConsecutiveReadyScans) => _readinessHistory.Select(kv => new ExecutionReadinessHistorySummary(kv.Key, State(kv.Key), kv.Value.LastOrDefault(), ConsecutiveExecutionReady(kv.Key), requiredConsecutiveReadyScans, kv.Value.LastOrDefault()?.NotReadyReason, kv.Value.ToArray())).ToArray();
    public IReadOnlyList<VerifiedBasketHistorySummary> Summaries() => _history.Select(kv => {
        var l = kv.Value;
        var nets = l.Select(x => x.ConservativeNet).ToArray();
        return new VerifiedBasketHistorySummary(kv.Key, l.ToArray(), nets.Min(), nets.Max(), nets.Average(), nets.LastOrDefault(), Consecutive(kv.Key), LastExecutableAt(kv.Key), ComputeVolatility(l), State(kv.Key));
    }).ToArray();

    public static decimal ComputeVolatility(IReadOnlyList<VerifiedBasketEdgeSample> l) => l.Count <= 1 ? 0m : l.Max(x => x.ConservativeNet) - l.Min(x => x.ConservativeNet);

    public void Export(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(Summaries(), new JsonSerializerOptions { WriteIndented = true }));
    }

    public void ExportExecutionReadiness(string path, int requiredConsecutiveReadyScans)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new { timestamp = DateTime.UtcNow, groups = ReadinessSummaries(requiredConsecutiveReadyScans).Select(x => new { groupKey = x.GroupKey, state = x.State.ToString(), netEdge = x.LatestReadinessSample?.NetEdge, plannedQty = x.LatestReadinessSample?.PlannedQty, plannedCost = x.LatestReadinessSample?.PlannedCost, plannedExpectedProfit = x.LatestReadinessSample?.PlannedExpectedProfit, maxQtyByLiquidity = x.LatestReadinessSample?.MaxQtyByLiquidity, maxQtyByNotional = x.LatestReadinessSample?.MaxQtyByNotional, limitingFactor = x.LatestReadinessSample?.LimitingFactor, ready = x.LatestReadinessSample?.Ready, notReadyReason = x.NotReadyReason, consecutiveReadyScans = x.ConsecutiveReadyScans, requiredConsecutiveReadyScans }) };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
}
