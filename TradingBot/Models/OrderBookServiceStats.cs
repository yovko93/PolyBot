namespace TradingBot.Models;

public record OrderBookServiceStats(
    long BatchRequests,
    long BatchBooksLoaded,
    long SingleRequests,
    long CacheHits,
    long SnapshotCacheHits,
    long Timeouts,
    long HttpErrors,
    long ParseErrors
);