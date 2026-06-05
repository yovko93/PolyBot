namespace TradingBot.Models;

public record OrderBookServiceStats(
    long BatchRequests,
    long BatchBooksLoaded,
    long SingleRequests,
    long CacheHits,
    long SnapshotCacheHits,
    long Timeouts,
    long HttpErrors,
    long ParseErrors,
    long BookCacheMisses,
    long BatchBadRequests = 0,
    long BatchTimeouts = 0,
    long BatchRetrySuccesses = 0,
    long BatchInvalidTokens = 0,
    long BatchSuppressedErrors = 0
);