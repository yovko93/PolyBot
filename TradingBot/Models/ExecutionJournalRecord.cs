namespace TradingBot.Models;

public record ExecutionJournalRecord(
    DateTime TimestampUtc,
    string Mode,
    string Engine,
    string Strategy,
    string Key,
    decimal Quantity,
    decimal TotalCost,
    decimal GuaranteedPayout,
    decimal EdgePerShare,
    decimal ExpectedProfit,
    decimal BalanceAfter,
    decimal LockedCapitalAfter,
    decimal EquityAfter,
    string Status,
    string Legs
);