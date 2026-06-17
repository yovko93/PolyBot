using TradingBot.Models;

namespace TradingBot.Services;

public sealed class OrderbookEligibilityRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, OrderbookEligibilityState> _states = new(StringComparer.OrdinalIgnoreCase);

    public OrderbookEligibilityState Get(string marketId)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(marketId, out var state)) return state;
            return new OrderbookEligibilityState(marketId, true, string.Empty, string.Empty, 0, null);
        }
    }

    public void MarkEligible(string marketId)
    {
        lock (_gate)
            _states[marketId] = new OrderbookEligibilityState(marketId, true, string.Empty, string.Empty, 0, null);
    }

    public void MarkIneligible(string marketId, string reason, string lastFailure, DateTime? quarantinedUntilUtc)
    {
        lock (_gate)
        {
            var priorFailures = _states.TryGetValue(marketId, out var prior) ? prior.ConsecutiveOrderbookFailures : 0;
            _states[marketId] = new OrderbookEligibilityState(marketId, false, reason, lastFailure, priorFailures + 1, quarantinedUntilUtc);
        }
    }
}
