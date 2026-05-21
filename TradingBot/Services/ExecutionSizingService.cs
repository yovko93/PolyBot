using TradingBot.Models;

namespace TradingBot.Services;

public sealed class ExecutionSizingService
{
    private readonly ExecutionPolicy _policy;

    public ExecutionSizingService(ExecutionPolicy? policy = null)
    {
        _policy = policy ?? new ExecutionPolicy();
    }

    public bool EnableSizingLogs => _policy.EnableSizingLogs;

    public ExecutionSizingResult SizeByNotional(decimal quantityAvailable, decimal capitalPerShare)
    {
        if (quantityAvailable <= 0m || capitalPerShare <= 0m)
        {
            return new ExecutionSizingResult(
                QuantityAvailable: quantityAvailable,
                ExecutableQuantity: 0m,
                CapitalPerShare: capitalPerShare,
                Notional: 0m,
                MaxNotional: _policy.MaxNotionalPerTrade,
                WasClamped: false,
                MeetsMinNotional: false
            );
        }

        var maxQtyByNotional = _policy.MaxNotionalPerTrade / capitalPerShare;
        var executableQuantity = Math.Min(quantityAvailable, maxQtyByNotional);
        var notional = executableQuantity * capitalPerShare;

        if (notional < _policy.MinNotionalPerTrade)
        {
            return new ExecutionSizingResult(
                QuantityAvailable: quantityAvailable,
                ExecutableQuantity: 0m,
                CapitalPerShare: capitalPerShare,
                Notional: 0m,
                MaxNotional: _policy.MaxNotionalPerTrade,
                WasClamped: quantityAvailable > executableQuantity,
                MeetsMinNotional: false
            );
        }

        return new ExecutionSizingResult(
            QuantityAvailable: quantityAvailable,
            ExecutableQuantity: executableQuantity,
            CapitalPerShare: capitalPerShare,
            Notional: notional,
            MaxNotional: _policy.MaxNotionalPerTrade,
            WasClamped: quantityAvailable > executableQuantity,
            MeetsMinNotional: true
        );
    }

    public decimal ClampQuantityByNotional(decimal quantityAvailable, decimal capitalPerShare)
    {
        return SizeByNotional(quantityAvailable, capitalPerShare).ExecutableQuantity;
    }

    public decimal EstimateNotional(decimal quantity, decimal capitalPerShare)
    {
        if (quantity <= 0m || capitalPerShare <= 0m)
            return 0m;

        return quantity * capitalPerShare;
    }
}
