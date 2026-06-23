namespace TradingBot.Models;

public sealed record ReducedUniverseBadOrderbookHistoryEntry(
    string Key,
    string? MarketId,
    string? TokenId,
    string Reason,
    DateTime FirstSeenUtc,
    DateTime LastSeenUtc,
    int FailureCount,
    string LastFailureKind,
    DateTime? QuarantineUntilUtc,
    string SourceRunId,
    string SourceDiscoveryMode);
