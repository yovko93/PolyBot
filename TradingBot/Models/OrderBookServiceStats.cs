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
    long BatchSuppressedErrors = 0,
    int QuarantinedTokens = 0,
    int BatchBookErrorSampleCount = 0,
    long BatchBookSplitRetriesAttempted = 0,
    long BatchBookSplitRetrySucceeded = 0,
    long BatchBookSplitRetryFailed = 0,
    long BatchBookSingleTokenFailures = 0,
    long BatchBookSingleTokenQuarantined = 0,
    long BatchBookSkippedQuarantinedTokens = 0,
    long BatchBookSkippedMarketsWithQuarantinedTokens = 0,
    long BatchBookRepeatedInvalidTokenAfterQuarantine = 0,
    int OrderbookUnavailableMarkets = 0
);