using TradingBot.Services;

namespace TradingBot.Services.MultiOutcome;

public sealed record EdgeStabilityPendingLogDecision(
    bool LogPending,
    bool LogStalled,
    int ConsecutiveEdgeScans,
    int RequiredEdgeScans,
    TimeSpan StateAge,
    string LastResetReason);

public sealed class EdgeStabilityLogThrottle
{
    private readonly Dictionary<string, string> _lastFingerprint = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _unchangedCycles = new(StringComparer.OrdinalIgnoreCase);

    public EdgeStabilityPendingLogDecision Evaluate(
        string groupKey,
        VerifiedBasketState state,
        int consecutiveEdgeScans,
        int requiredEdgeScans,
        decimal netEdge,
        TimeSpan stateAge,
        string? lastResetReason,
        int stalledEveryCycles = 10)
    {
        if (state != VerifiedBasketState.EdgeExecutablePending)
        {
            _unchangedCycles.Remove(groupKey);
            _lastFingerprint.Remove(groupKey);
            return new(false, false, consecutiveEdgeScans, requiredEdgeScans, stateAge, lastResetReason ?? "None");
        }

        var reason = string.IsNullOrWhiteSpace(lastResetReason) ? "AwaitingConsecutiveScans" : lastResetReason!;
        var fingerprint = string.Join("|", state, consecutiveEdgeScans, requiredEdgeScans, Math.Round(netEdge, 6), reason);
        var changed = !_lastFingerprint.TryGetValue(groupKey, out var previous) || previous != fingerprint;
        _lastFingerprint[groupKey] = fingerprint;

        _unchangedCycles.TryGetValue(groupKey, out var unchanged);
        unchanged = changed ? 0 : unchanged + 1;
        _unchangedCycles[groupKey] = unchanged;

        var stalled = !changed && stalledEveryCycles > 0 && unchanged > 0 && unchanged % stalledEveryCycles == 0;
        return new(changed, stalled, consecutiveEdgeScans, requiredEdgeScans, stateAge, reason);
    }
}
