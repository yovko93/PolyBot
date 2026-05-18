namespace TradingBot.Models;

public record ExecutionDecision(
    bool CanExecute,
    string Reason,
    decimal ExecutableQuantity,
    decimal TotalCost,
    decimal ExpectedProfit,
    decimal EdgePerShare
);