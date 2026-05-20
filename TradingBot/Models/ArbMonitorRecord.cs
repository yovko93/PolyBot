namespace TradingBot.Models;

public record ArbMonitorRecord(
    DateTime TimestampUtc,
    string Engine,
    string Strategy,
    string Key,
    decimal EdgePerShare,
    decimal CostOrProceeds,
    decimal GuaranteedPayout,
    decimal QuantityAvailable,
    decimal ExpectedProfit,
    bool IsExecutable,
    string Leg1,
    string? Leg2 = null,
    string? GroupKey = null
)
{
    public IReadOnlyList<OrderLegCandidate> OrderLegs { get; init; } =
        Array.Empty<OrderLegCandidate>();
}