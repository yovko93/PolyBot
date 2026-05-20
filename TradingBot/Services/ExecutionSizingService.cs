using TradingBot.Models;

namespace TradingBot.Services;

public sealed class ExecutionSizingService
{
    private readonly ExecutionPolicy _policy;

    public ExecutionSizingService(ExecutionPolicy? policy = null)
    {
        _policy = policy ?? new ExecutionPolicy();
    }

    public decimal ClampQuantityByNotional(decimal quantityAvailable, decimal capitalPerShare)
    {
        if (quantityAvailable <= 0m || capitalPerShare <= 0m)
            return 0m;

        var maxQty = _policy.MaxNotionalPerTrade / capitalPerShare;
        var executable = Math.Min(quantityAvailable, maxQty);
        var notional = EstimateNotional(executable, capitalPerShare);

        if (notional < _policy.MinNotionalPerTrade)
            return 0m;

        return executable;
    }

    public decimal EstimateNotional(decimal quantity, decimal capitalPerShare)
    {
        if (quantity <= 0m || capitalPerShare <= 0m)
            return 0m;

        return quantity * capitalPerShare;
    }
}
