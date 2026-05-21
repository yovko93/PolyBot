namespace TradingBot.Models;

public sealed record ExecutionSizingResult(
    decimal QuantityAvailable,
    decimal ExecutableQuantity,
    decimal CapitalPerShare,
    decimal Notional,
    decimal MaxNotional,
    bool WasClamped,
    bool MeetsMinNotional
);
