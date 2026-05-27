using System.Text.Json;
using TradingBot.Services.MultiOutcome;

namespace TradingBot.Services;

public enum VerifiedBasketState { NotExecutable, NearExecutable, ExecutablePending, StableExecutable }

public sealed record VerifiedBasketEdgeSample(DateTime Timestamp, decimal GrossEdge, decimal ConservativeNet, decimal PolymarketApproxNet, decimal RawOnlyNet, bool Executable, string Classification, string TopReject, decimal NoAskSum, string LimitingLeg);

public sealed record VerifiedBasketHistorySummary(string GroupKey, IReadOnlyList<VerifiedBasketEdgeSample> Samples, decimal MinNet, decimal MaxNet, decimal AverageNet, decimal LastNet, int ConsecutiveExecutableScans, DateTime? LastExecutableAt, decimal Volatility, VerifiedBasketState CurrentState);

public sealed class VerifiedOpportunityStabilityTracker
{
    private readonly Dictionary<string, List<VerifiedBasketEdgeSample>> _history = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _consecutive = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, VerifiedBasketState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime?> _lastExec = new(StringComparer.OrdinalIgnoreCase);

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
        var st = !positive ? (row.NearExecutable ? VerifiedBasketState.NearExecutable : VerifiedBasketState.NotExecutable)
            : (c >= requiredConsecutive && vol <= maxVol ? VerifiedBasketState.StableExecutable : VerifiedBasketState.ExecutablePending);
        _state[groupKey] = st;
        return st;
    }

    public int Consecutive(string groupKey) => _consecutive.TryGetValue(groupKey, out var c) ? c : 0;
    public DateTime? LastExecutableAt(string groupKey) => _lastExec.TryGetValue(groupKey, out var v) ? v : null;
    public VerifiedBasketState State(string groupKey) => _state.TryGetValue(groupKey, out var s) ? s : VerifiedBasketState.NotExecutable;
    public decimal Volatility(string groupKey) => _history.TryGetValue(groupKey, out var l) ? ComputeVolatility(l) : 0m;
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
}
