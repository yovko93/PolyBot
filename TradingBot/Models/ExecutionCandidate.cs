namespace TradingBot.Models;

public enum ExecutionCandidateType
{
    SingleMarketBuyBoth,
    CompleteSetSell,
    ThresholdDominance,
    MultiOutcomeNoBasket,
    TrueSemanticCrossMarket
}

public record ExecutionCandidateLeg(
    string MarketId,
    string Question,
    string Outcome,
    decimal SnapshotPrice,
    decimal SnapshotSize
);

public record ExecutionCandidate(
    ExecutionCandidateType Type,
    string Key,
    string Strategy,
    List<ExecutionCandidateLeg> Legs,
    decimal SnapshotEdgePerShare,
    decimal SnapshotQuantity,
    decimal SnapshotExpectedProfit,
    decimal MinEdgePerShare,
    decimal MinExpectedProfit
);