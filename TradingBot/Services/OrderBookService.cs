using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using TradingBot.Models;
using TradingBot.Options;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradingBot.Services;

public enum OrderbookCircuitBreakerState { Closed, Open, HalfOpen, Recovering }

public class OrderBookService : IOrderBookProvider
{
    private long _batchRequests;
    private long _batchBooksLoaded;
    private long _singleRequests;
    private long _cacheHits;
    private long _snapshotCacheHits;
    private long _timeouts;
    private long _httpErrors;
    private long _parseErrors;
    private long _batchBadRequests;
    private long _batchTimeouts;
    private long _batchRetrySuccesses;
    private long _batchInvalidTokens;
    private long _batchSuppressedErrors;
    private long _batchSplitRetriesAttempted;
    private long _batchSplitRetrySucceeded;
    private long _batchSplitRetryFailed;
    private long _batchSingleTokenFailures;
    private long _batchSingleTokenQuarantined;
    private long _batchSkippedQuarantinedTokens;
    private long _batchSkippedMarketsWithQuarantinedTokens;
    private long _batchRepeatedInvalidTokenAfterQuarantine;
    private long _invalidTokenQuarantineAdded;
    private long _invalidTokenQuarantineExpired;
    private long _batchRequestsAvoidedByQuarantine;
    private long _marketsSkippedByInvalidTokenQuarantine;
    private long _marketOrderbookQuarantineAdded;
    private long _marketOrderbookQuarantineExpired;
    private long _marketsSkippedByMarketOrderbookQuarantine;
    private long _quarantinedMarketsReintroducedBlocked;
    private long _quarantinedTokensReintroducedBlocked;
    private long _batchRequestsAvoidedByMarketQuarantine;
    private long _orderbookCircuitBreakerOpenCount;
    private long _orderbookCircuitBreakerHalfOpenAttempts;
    private long _orderbookCircuitBreakerHalfOpenSucceeded;
    private long _orderbookCircuitBreakerHalfOpenFailed;
    private long _batchBookCanaryTimeouts;
    private long _batchBookCanaryInvalidTokens;
    private long _batchBookCanaryOrderbookUnavailable;
    private long _orderbookCircuitBreakerCooldownExtensions;
    private long _orderbookCircuitBreakerReopenedAfterClose;
    private long _orderbookRequestsBlockedByCircuitBreaker;
    private long _orderbookPostCloseBadRequests;
    private long _orderbookPostCloseInvalidTokens;
    private long _singleTokenIsolationBudgetExhausted;
    private long _batchBookBadRequestsPreventedEstimate;
    private long _batchBookCanaryRequests;
    private long _batchBookCanaryBadRequests;
    private long _batchBookRecoveryRequests;
    private long _batchBookRecoveryBadRequests;
    private long _orderbookRecoveryLimitedRequests;
    private long _orderbookRecoveryLimitedMarkets;
    private long _orderbookRecoveryBadRequests;
    private long _orderbookRecoveryInvalidTokens;
    private long _orderbookRecoverySucceededCount;
    private long _orderbookRecoveryFailedCount;
    private long _batchBookNormalRequests;
    private long _batchBookNormalBadRequests;
    private long _batchBookNormalRequestsBeforeBreakerOpen;
    private long _batchBookNormalBadRequestsBeforeBreakerOpen;
    private long _batchBookNormalRequestsAfterBreakerOpen;
    private long _batchBookNormalBadRequestsAfterBreakerOpen;
    private long _reducedUniversePostRecoveryBadRequests;
    private long _inFlightBeforeBreakerCompletedAfterOpen;
    private long _inFlightBeforeBreakerBadRequestsAfterOpen;
    private long _truePostBreakerNormalRequests;
    private long _truePostBreakerBadRequests;
    private readonly HttpClient _http;
    private readonly object _cacheLock = new();

    private long _bookCacheMissLogs;
    private long _singleRequestCallerLogs;
    private long _bookCacheMisses;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _bookCacheMissSamples = new();
    public bool LogBookCacheMissDetails { get; set; } = false;
    public int BookCacheMissSampleSize { get; set; } = 5;
    public int MaxBatchBookRequestSize { get; set; } = 100;
    public bool SplitBatchOnBadRequest { get; set; } = true;
    public bool LogInvalidBatchPayloadSamples { get; set; } = true;
    public int MaxInvalidPayloadSamplesToLog { get; set; } = 5;
    public TimeSpan InvalidTokenQuarantineTtl { get; set; } = TimeSpan.FromMinutes(360);
    public TimeSpan MarketOrderbookQuarantineTtl { get; set; } = TimeSpan.FromMinutes(360);
    public int MaxInvalidTokensPerCycle { get; set; } = 50;
    public int MaxSingleTokenIsolationsPerCycle { get; set; } = 50;
    public int MaxSingleTokenIsolationsPerHour { get; set; } = 200;
    public int MaxNewTokenQuarantinesPerHour { get; set; } = 200;
    public int MaxBatchBookBadRequestsPerCycle { get; set; } = 20;
    public TimeSpan OrderbookCircuitBreakerCooldown { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan CircuitBreakerInitialCooldown { get; set; } = TimeSpan.FromMinutes(15);
    public TimeSpan CircuitBreakerMaxCooldown { get; set; } = TimeSpan.FromMinutes(120);
    public int CircuitBreakerHalfOpenCanaryMarkets { get; set; } = 25;
    public int CircuitBreakerHalfOpenMaxBadRequests { get; set; } = 0;
    public int CircuitBreakerHalfOpenMaxTimeouts { get; set; } = 1;
    public double CircuitBreakerCooldownBackoffMultiplier { get; set; } = 2;
    public TimeSpan RecoveryDuration { get; set; } = TimeSpan.FromMinutes(5);
    public int RecoveryMaxMarketsPerCycle { get; set; } = 100;
    public int RecoveryMaxBatchSize { get; set; } = 25;
    public int CircuitBreakerBadRequestsPerHourThreshold { get; set; } = 25;
    public int CircuitBreakerInvalidTokensPerHourThreshold { get; set; } = 25;
    public double CircuitBreakerBadRequestRateThreshold { get; set; } = 0.05;
    public int CircuitBreakerUnavailableMarketsThreshold { get; set; } = 500;
    public int MaxInvalidTokenSingleRetriesPerCycle { get; set; } = 1;
    public int MaxBatchSplitRetriesPerCycle { get; set; } = 20;
    public bool DropMarketsWithQuarantinedTokens { get; set; } = true;
    public bool SkipQuarantinedTokensBeforeBatch { get; set; } = true;
    public int MaxInvalidTokenCacheEntries { get; set; } = 5000;
    public int MaxBatchBookErrorSamples { get; set; } = 100;
    public TimeSpan BatchBookErrorSampleTtl { get; set; } = TimeSpan.FromMinutes(60);
    public bool OperationalQuietMode { get; set; } = true;
    public bool ExportInvalidTokenQuarantine { get; set; } = true;
    public string ExportDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory, "exports");
    public TradingBot.Options.MultiOutcomeLoggingOptions Logging { get; set; } = new();
    public QuietLogGate? QuietLogGate { get; set; }
    public Action<OrderBookServiceStats>? StatsUpdated { get; set; }

    private readonly Dictionary<string, (DateTime Time, ClobOrderBook? Book)> _bookCache = new();
    private readonly Dictionary<string, (DateTime Time, BinaryOrderBookSnapshot? Snapshot)> _snapshotCache = new();
    private readonly Dictionary<string, DateTime> _invalidTokenQuarantine = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokenMarketIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string YesTokenId, string NoTokenId)> _marketTokenPairs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, MarketOrderbookQuarantine> _marketOrderbookQuarantine = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _reducedUniverseBadHistory = new(StringComparer.OrdinalIgnoreCase);
    private readonly OrderbookEligibilityRegistry _orderbookEligibility = new();
    private readonly HashSet<string> _orderbookUnavailableMarkets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(DateTime Time, string EventName, string Sample)> _batchErrorSamples = new();
    private readonly HashSet<string> _knownInvalidTokensThisCycle = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _singleTokenRetriesThisCycle = new(StringComparer.Ordinal);
    private int _batchSplitRetriesThisCycle;
    private int _singleTokenIsolationsThisCycle;
    private int _batchBadRequestsThisCycle;
    private DateTime _circuitBreakerOpenUntilUtc = DateTime.MinValue;
    private OrderbookCircuitBreakerState _circuitBreakerState = OrderbookCircuitBreakerState.Closed;
    private TimeSpan _currentCircuitBreakerCooldown = TimeSpan.FromMinutes(15);
    private string _circuitBreakerLastOpenReason = string.Empty;
    private string _circuitBreakerLastHalfOpenFailureReason = string.Empty;
    private long _halfOpenBaselineBadRequests;
    private long _halfOpenBaselineTimeouts;
    private DateTime? _recoveringSinceUtc;
    private DateTime? _lastClosedUtc;
    private long _postCloseBaselineBadRequests;
    private long _postCloseBaselineInvalidTokens;
    private readonly Queue<DateTime> _badRequestTimes = new();
    private readonly Queue<DateTime> _invalidTokenTimes = new();
    private bool _circuitBreakerLoggedOpen;
    private bool _reducedUniverseBadHistoryLoaded;

    private TimeSpan _cacheTtl = TimeSpan.FromSeconds(60);
    private int _maxCacheEntries = 5000;
    private TimeSpan ReducedUniverseBadHistoryTtl => TimeSpan.FromDays(7);

    public OrderBookService(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(10);
    }

    public bool DisableSingleBookHttpFallback { get; set; } = false;
    public bool LogPrefetchDetails { get; set; } = false;
    public int BookCacheCount { get { lock (_cacheLock) return _bookCache.Count; } }
    public int SnapshotCacheCount { get { lock (_cacheLock) return _snapshotCache.Count; } }
    public int CacheEntryCount { get { lock (_cacheLock) return _bookCache.Count + _snapshotCache.Count; } }
    public int QuarantinedTokenCount { get { lock (_cacheLock) { TrimInvalidTokenQuarantineLocked(DateTime.UtcNow); return _invalidTokenQuarantine.Count; } } }
    public int BatchBookErrorSampleCount { get { lock (_cacheLock) { TrimBatchErrorSamplesLocked(DateTime.UtcNow); return _batchErrorSamples.Count; } } }
    public void ConfigureCache(TimeSpan ttl, int maxEntries) { _cacheTtl = ttl; _maxCacheEntries = Math.Max(1, maxEntries); }
    public void ConfigureBatchOptions(TradingBot.Options.OrderBookOptions options, bool operationalQuietMode, TradingBot.Options.MultiOutcomeLoggingOptions logging, QuietLogGate? quietLogGate = null)
    {
        MaxBatchBookRequestSize = Math.Max(1, options.MaxBatchBookRequestSize);
        SplitBatchOnBadRequest = options.SplitBatchOnBadRequest;
        LogInvalidBatchPayloadSamples = options.LogInvalidBatchPayloadSamples;
        MaxInvalidPayloadSamplesToLog = Math.Max(0, options.MaxInvalidPayloadSamplesToLog);
        InvalidTokenQuarantineTtl = TimeSpan.FromMinutes(Math.Max(1, options.InvalidTokenQuarantineTtlMinutes > 0 ? options.InvalidTokenQuarantineTtlMinutes : options.InvalidTokenQuarantineMinutes));
        MaxInvalidTokensPerCycle = Math.Max(1, options.MaxInvalidTokensPerCycle);
        MaxInvalidTokenSingleRetriesPerCycle = Math.Max(0, options.MaxInvalidTokenSingleRetriesPerCycle);
        MaxBatchSplitRetriesPerCycle = Math.Max(0, options.MaxBatchSplitRetriesPerCycle);
        DropMarketsWithQuarantinedTokens = options.DropMarketsWithQuarantinedTokens;
        SkipQuarantinedTokensBeforeBatch = options.SkipQuarantinedTokensBeforeBatch;
        ExportInvalidTokenQuarantine = options.ExportInvalidTokenQuarantine;
        MaxSingleTokenIsolationsPerCycle = Math.Max(1, options.MaxSingleTokenIsolationsPerCycle);
        MaxSingleTokenIsolationsPerHour = Math.Max(1, options.MaxSingleTokenIsolationsPerHour);
        MaxNewTokenQuarantinesPerHour = Math.Max(1, options.MaxNewTokenQuarantinesPerHour);
        MaxBatchBookBadRequestsPerCycle = Math.Max(1, options.MaxBatchBookBadRequestsPerCycle);
        OrderbookCircuitBreakerCooldown = TimeSpan.FromMinutes(Math.Max(1, options.OrderbookCircuitBreakerCooldownMinutes));
        CircuitBreakerInitialCooldown = TimeSpan.FromMinutes(Math.Max(1, options.CircuitBreakerInitialCooldownMinutes > 0 ? options.CircuitBreakerInitialCooldownMinutes : options.OrderbookCircuitBreakerCooldownMinutes));
        CircuitBreakerMaxCooldown = TimeSpan.FromMinutes(Math.Max(1, options.CircuitBreakerMaxCooldownMinutes));
        _currentCircuitBreakerCooldown = CircuitBreakerInitialCooldown;
        CircuitBreakerHalfOpenCanaryMarkets = Math.Max(1, options.CircuitBreakerHalfOpenCanaryMarkets);
        CircuitBreakerHalfOpenMaxBadRequests = Math.Max(0, options.CircuitBreakerHalfOpenMaxBadRequests);
        CircuitBreakerHalfOpenMaxTimeouts = Math.Max(0, options.CircuitBreakerHalfOpenMaxTimeouts);
        CircuitBreakerCooldownBackoffMultiplier = Math.Max(1.0, options.CircuitBreakerCooldownBackoffMultiplier);
        RecoveryDuration = TimeSpan.FromMinutes(Math.Max(1, options.RecoveryDurationMinutes));
        RecoveryMaxMarketsPerCycle = Math.Max(1, options.RecoveryMaxMarketsPerCycle);
        RecoveryMaxBatchSize = Math.Max(1, options.RecoveryMaxBatchSize);
        CircuitBreakerBadRequestsPerHourThreshold = Math.Max(1, options.CircuitBreakerBadRequestsPerHourThreshold);
        CircuitBreakerInvalidTokensPerHourThreshold = Math.Max(1, options.CircuitBreakerInvalidTokensPerHourThreshold);
        CircuitBreakerBadRequestRateThreshold = Math.Max(0.0001, options.CircuitBreakerBadRequestRateThreshold);
        CircuitBreakerUnavailableMarketsThreshold = Math.Max(1, options.CircuitBreakerUnavailableMarketsThreshold);
        MarketOrderbookQuarantineTtl = TimeSpan.FromMinutes(Math.Max(1, options.MarketOrderbookQuarantineTtlMinutes));
        OperationalQuietMode = operationalQuietMode;
        Logging = logging;
        QuietLogGate = quietLogGate;
    }

    public async Task<BinaryOrderBookSnapshot?> GetBinarySnapshotAsync(
        Market market,
        CancellationToken ct = default)
    {
        var snapshotCacheKey = market.id;
        if (CircuitBreakerState != OrderbookCircuitBreakerState.Closed) { Interlocked.Increment(ref _orderbookRequestsBlockedByCircuitBreaker); return null; }
        if (IsMarketOrderbookQuarantined(market.id)) return null;

        lock (_cacheLock)
        {
            if (_snapshotCache.TryGetValue(snapshotCacheKey, out var cached) &&
                DateTime.UtcNow - cached.Time < _cacheTtl)
            {
                Interlocked.Increment(ref _snapshotCacheHits);
                return cached.Snapshot;
            }
        }

        if (market.clobTokenIds.Count < 2)
        {
            Console.WriteLine($"[ORDERBOOK] Missing clobTokenIds for market: {market.question}");
            return null;
        }

        var yesTokenId = GetTokenIdForOutcome(market, "yes") ?? market.clobTokenIds[0];
        var noTokenId = GetTokenIdForOutcome(market, "no") ?? market.clobTokenIds[1];

        if (string.IsNullOrWhiteSpace(yesTokenId) || string.IsNullOrWhiteSpace(noTokenId))
            return null;

        if (IsTokenQuarantined(yesTokenId) || IsTokenQuarantined(noTokenId))
        {
            if (IsTokenQuarantined(yesTokenId) && IsTokenQuarantined(noTokenId))
                QuarantineMarketOrderbook(market.id, yesTokenId, noTokenId, "BothTokensQuarantined", "TokenQuarantinePair");
            return null;
        }

        var yesBookTask = GetOrderBookAsync(yesTokenId, ct);
        var noBookTask = GetOrderBookAsync(noTokenId, ct);

        await Task.WhenAll(yesBookTask, noBookTask);

        var yesBook = yesBookTask.Result;
        var noBook = noBookTask.Result;

        if (yesBook == null || noBook == null)
            return null;

        var snapshot = new BinaryOrderBookSnapshot(
            MarketId: market.id,
            Question: market.question,
            YesTokenId: yesTokenId,
            NoTokenId: noTokenId,
            YesBid: GetBestBid(yesBook),
            YesAsk: GetBestAsk(yesBook),
            NoBid: GetBestBid(noBook),
            NoAsk: GetBestAsk(noBook),
            TimestampUtc: DateTime.UtcNow
        );

        lock (_cacheLock)
        {
            _snapshotCache[snapshotCacheKey] = (DateTime.UtcNow, snapshot);
            TrimCacheLocked();
        }

        return snapshot;

        //return new BinaryOrderBookSnapshot(
        //    MarketId: market.id,
        //    Question: market.question,
        //    YesAsk: GetBestAsk(yesBook),
        //    NoAsk: GetBestAsk(noBook),
        //    YesBid: GetBestBid(yesBook),
        //    NoBid: GetBestBid(noBook)
        //);
    }


    private List<Market> SanitizeMarketsForOrderbookSchedule(IEnumerable<Market> markets, OrderbookCircuitBreakerState breakerState, int? maxMarkets = null)
    {
        var original = markets.Where(m => m != null).ToList();
        var skippedMarketQuarantine = 0;
        var skippedTokenQuarantine = 0;
        var skippedEligibility = 0;
        var remaining = new List<Market>();
        lock (_cacheLock) TrimMarketOrderbookQuarantineLocked(DateTime.UtcNow);
        foreach (var market in original)
        {
            if (string.IsNullOrWhiteSpace(market.id) || market.clobTokenIds == null || market.clobTokenIds.Count < 2)
            {
                skippedEligibility++;
                continue;
            }
            if (IsMarketOrderbookQuarantined(market.id))
            {
                skippedMarketQuarantine++;
                continue;
            }
            var eligibility = GetOrderbookEligibility(market.id);
            if (!eligibility.Eligible)
            {
                skippedEligibility++;
                continue;
            }
            if (DropMarketsWithQuarantinedTokens && market.clobTokenIds.Any(IsTokenQuarantined))
            {
                skippedTokenQuarantine++;
                continue;
            }
            remaining.Add(market);
        }
        var capped = maxMarkets.HasValue && remaining.Count > maxMarkets.Value;
        var cappedCount = capped ? remaining.Count - maxMarkets.Value : 0;
        if (capped) remaining = remaining.Take(maxMarkets.Value).ToList();
        if (skippedMarketQuarantine > 0)
        {
            Interlocked.Add(ref _marketsSkippedByMarketOrderbookQuarantine, skippedMarketQuarantine);
            Interlocked.Add(ref _batchRequestsAvoidedByMarketQuarantine, skippedMarketQuarantine);
            Interlocked.Add(ref _batchBookBadRequestsPreventedEstimate, skippedMarketQuarantine);
            Interlocked.Add(ref _quarantinedMarketsReintroducedBlocked, skippedMarketQuarantine);
        }
        if (skippedTokenQuarantine > 0)
        {
            Interlocked.Add(ref _batchSkippedMarketsWithQuarantinedTokens, skippedTokenQuarantine);
            Interlocked.Add(ref _marketsSkippedByInvalidTokenQuarantine, skippedTokenQuarantine);
            Interlocked.Add(ref _batchRequestsAvoidedByQuarantine, skippedTokenQuarantine);
            Interlocked.Add(ref _quarantinedTokensReintroducedBlocked, skippedTokenQuarantine);
        }
        if (breakerState == OrderbookCircuitBreakerState.HalfOpen && remaining.Count == 0 && original.Count > 0)
        {
            _circuitBreakerLastHalfOpenFailureReason = "NoEligibleMarkets";
            Interlocked.Increment(ref _batchBookCanaryOrderbookUnavailable);
            Console.WriteLine($"[ORDERBOOK_CIRCUIT_BREAKER_HALF_OPEN_FAILED] Reason=NoEligibleMarkets CanaryRequests={Interlocked.Read(ref _batchBookCanaryRequests)} CanaryBadRequests={Interlocked.Read(ref _batchBookCanaryBadRequests)} CanaryTimeouts={Interlocked.Read(ref _batchBookCanaryTimeouts)} CanaryInvalidTokens={Interlocked.Read(ref _batchBookCanaryInvalidTokens)}");
            lock (_cacheLock) OpenCircuitBreakerLocked(DateTime.UtcNow, "HalfOpenCanaryFailed");
        }
        if (skippedMarketQuarantine > 0 || skippedTokenQuarantine > 0 || skippedEligibility > 0 || cappedCount > 0)
        {
            var skippedCircuitBreaker = breakerState == OrderbookCircuitBreakerState.Open ? original.Count : cappedCount;
            if (ShouldLogBatchDiagnostic("BATCH_BOOK_SANITIZED", $"{original.Count}|{remaining.Count}|{skippedMarketQuarantine}|{skippedTokenQuarantine}|{skippedCircuitBreaker}", original.Count))
                Console.WriteLine($"[BATCH_BOOK_SANITIZED] OriginalMarkets={original.Count} RemainingMarkets={remaining.Count} SkippedDiscoveryPartialUntrusted=0 SkippedMarketQuarantine={skippedMarketQuarantine} SkippedTokenQuarantine={skippedTokenQuarantine} SkippedCircuitBreaker={skippedCircuitBreaker}");
        }
        return remaining;
    }

    public async Task PrefetchBinarySnapshotsAsync(
    List<Market> markets,
    CancellationToken ct = default)
    {
        var breakerState = CircuitBreakerState;
        if (breakerState == OrderbookCircuitBreakerState.Open)
        {
            var sanitizedOpen = SanitizeMarketsForOrderbookSchedule(markets, breakerState);
            Interlocked.Add(ref _batchBookBadRequestsPreventedEstimate, sanitizedOpen.Count);
            Interlocked.Add(ref _orderbookRequestsBlockedByCircuitBreaker, sanitizedOpen.Count);
            return;
        }
        if (breakerState == OrderbookCircuitBreakerState.Recovering)
        {
            var originalRecoveryMarkets = markets.Count;
            markets = SanitizeMarketsForOrderbookSchedule(markets, breakerState, RecoveryMaxMarketsPerCycle);
            Interlocked.Increment(ref _orderbookRecoveryLimitedRequests);
            Interlocked.Add(ref _orderbookRecoveryLimitedMarkets, markets.Count);
            if (originalRecoveryMarkets > markets.Count) Interlocked.Add(ref _orderbookRequestsBlockedByCircuitBreaker, originalRecoveryMarkets - markets.Count);
            if (markets.Count == 0) return;
        }
        else if (breakerState == OrderbookCircuitBreakerState.HalfOpen)
        {
            Interlocked.Increment(ref _orderbookCircuitBreakerHalfOpenAttempts);
            markets = SanitizeMarketsForOrderbookSchedule(markets, breakerState, CircuitBreakerHalfOpenCanaryMarkets);
            if (markets.Count == 0) return;
            _halfOpenBaselineBadRequests = Interlocked.Read(ref _batchBadRequests);
            _halfOpenBaselineTimeouts = Interlocked.Read(ref _batchTimeouts);
        }
        else
        {
            markets = SanitizeMarketsForOrderbookSchedule(markets, breakerState);
            if (markets.Count == 0) return;
        }

        var quarantinedMarketTokens = DropMarketsWithQuarantinedTokens
            ? markets
                .Where(m => m != null && m.clobTokenIds != null && m.clobTokenIds.Any(IsTokenQuarantined))
                .SelectMany(m => m.clobTokenIds.Where(IsTokenQuarantined))
                .Distinct(StringComparer.Ordinal)
                .ToArray()
            : Array.Empty<string>();
        var skippedMarketsWithQuarantine = DropMarketsWithQuarantinedTokens
            ? markets.Count(m => m != null && m.clobTokenIds != null && m.clobTokenIds.Any(IsTokenQuarantined))
            : 0;
        if (skippedMarketsWithQuarantine > 0)
        {
            Interlocked.Add(ref _batchSkippedMarketsWithQuarantinedTokens, skippedMarketsWithQuarantine);
            Interlocked.Add(ref _marketsSkippedByInvalidTokenQuarantine, skippedMarketsWithQuarantine);
            Interlocked.Add(ref _batchRequestsAvoidedByQuarantine, skippedMarketsWithQuarantine);
        }
        if (quarantinedMarketTokens.Length > 0)
            Interlocked.Add(ref _batchSkippedQuarantinedTokens, quarantinedMarketTokens.Length);

        var validMarkets = markets
            .Where(m =>
                m != null &&
                !string.IsNullOrWhiteSpace(m.id) &&
                m.clobTokenIds != null &&
                m.clobTokenIds.Count >= 2 &&
                (!DropMarketsWithQuarantinedTokens || !m.clobTokenIds.Any(IsTokenQuarantined)) && !IsMarketOrderbookQuarantined(m.id))
            .ToList();

        RememberTokenMarketMap(validMarkets);

        var tokenIds = validMarkets
            .SelectMany(m => m.clobTokenIds)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !SkipQuarantinedTokensBeforeBatch || !IsTokenQuarantined(x))
            .Distinct()
            .ToList();

        if (tokenIds.Count == 0)
            return;

        var books = await GetOrderBooksBatchAsync(tokenIds, ct);
        if (breakerState == OrderbookCircuitBreakerState.HalfOpen)
            EvaluateHalfOpenCanaryResult();

        var now = DateTime.UtcNow;
        var snapshotsLoaded = 0;

        lock (_cacheLock)
        {
            _snapshotCache.Clear();

            foreach (var market in validMarkets)
            {
                var yesTokenId = GetTokenIdForOutcome(market, "yes") ?? market.clobTokenIds[0];
                var noTokenId = GetTokenIdForOutcome(market, "no") ?? market.clobTokenIds[1];

                if (string.IsNullOrWhiteSpace(yesTokenId) || string.IsNullOrWhiteSpace(noTokenId))
                    continue;

                if (!books.TryGetValue(yesTokenId, out var yesBook))
                {
                    if (!_bookCache.TryGetValue(yesTokenId, out var cachedYes) ||
                        cachedYes.Book == null ||
                        now - cachedYes.Time > _cacheTtl)
                    {
                        continue;
                    }

                    yesBook = cachedYes.Book;
                }

                if (!books.TryGetValue(noTokenId, out var noBook))
                {
                    if (!_bookCache.TryGetValue(noTokenId, out var cachedNo) ||
                        cachedNo.Book == null ||
                        now - cachedNo.Time > _cacheTtl)
                    {
                        continue;
                    }

                    noBook = cachedNo.Book;
                }

                var snapshot = new BinaryOrderBookSnapshot(
                    MarketId: market.id,
                    Question: market.question,
                    YesTokenId: yesTokenId,
                    NoTokenId: noTokenId,
                    YesBid: GetBestBid(yesBook),
                    YesAsk: GetBestAsk(yesBook),
                    NoBid: GetBestBid(noBook),
                    NoAsk: GetBestAsk(noBook),
                    TimestampUtc: now
                );

                _snapshotCache[market.id] = (now, snapshot);
                snapshotsLoaded++;
            }
            TrimCacheLocked();
        }

        if (LogPrefetchDetails)
        {
            Console.WriteLine($"[PREFETCH] Binary snapshots loaded: {snapshotsLoaded}/{validMarkets.Count}");
            Console.WriteLine($"[PREFETCH] Markets={validMarkets.Count}, Tokens={tokenIds.Count}, Books={books.Count}, Snapshots={snapshotsLoaded}");
        }
    }


    public bool IsMarketOrderbookQuarantined(string? marketId)
    {
        if (string.IsNullOrWhiteSpace(marketId)) return false;
        lock (_cacheLock)
        {
            TrimMarketOrderbookQuarantineLocked(DateTime.UtcNow);
            return _marketOrderbookQuarantine.ContainsKey(marketId);
        }
    }

    public OrderbookEligibilityState GetOrderbookEligibility(string marketId)
    {
        return _orderbookEligibility.Get(marketId);
    }

    private string ReducedUniverseBadHistoryPath => Path.Combine(ExportDirectory, "reduced-universe-orderbook-bad-history.json");

    private void EnsureReducedUniverseBadHistoryLoadedLocked(DateTime now)
    {
        if (_reducedUniverseBadHistoryLoaded) return;
        _reducedUniverseBadHistoryLoaded = true;
        try
        {
            if (!File.Exists(ReducedUniverseBadHistoryPath)) return;
            var loaded = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(ReducedUniverseBadHistoryPath));
            if (loaded == null) return;
            foreach (var item in loaded)
                if (item.Value > now) _reducedUniverseBadHistory[item.Key] = item.Value;
        }
        catch { }
    }

    private void PersistReducedUniverseBadHistoryLocked()
    {
        try
        {
            Directory.CreateDirectory(ExportDirectory);
            File.WriteAllText(ReducedUniverseBadHistoryPath, System.Text.Json.JsonSerializer.Serialize(_reducedUniverseBadHistory));
        }
        catch { }
    }

    private void RememberReducedUniverseBadHistoryLocked(string key, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        EnsureReducedUniverseBadHistoryLoadedLocked(now);
        _reducedUniverseBadHistory[key] = now.Add(ReducedUniverseBadHistoryTtl);
        PersistReducedUniverseBadHistoryLocked();
    }

    private void TrimReducedUniverseBadHistoryLocked(DateTime now)
    {
        EnsureReducedUniverseBadHistoryLoadedLocked(now);
        foreach (var key in _reducedUniverseBadHistory.Where(x => x.Value <= now).Select(x => x.Key).ToList())
            _reducedUniverseBadHistory.Remove(key);
    }

    public ReducedUniverseOrderbookFilterResult FilterReducedUniverseOrderbookEligibleMarkets(IEnumerable<Market> markets)
    {
        var raw = markets.Where(m => m != null).ToList();
        var filtered = new List<Market>();
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        var excludedInvalidTokens = 0;
        var excludedQuarantinedMarkets = 0;
        var excludedBadHistory = 0;

        lock (_cacheLock)
        {
            var now = DateTime.UtcNow;
            TrimInvalidTokenQuarantineLocked(now);
            TrimMarketOrderbookQuarantineLocked(now);
            TrimReducedUniverseBadHistoryLocked(now);
            foreach (var market in raw)
            {
                if (string.IsNullOrWhiteSpace(market.id) || market.clobTokenIds == null || market.clobTokenIds.Count < 2)
                {
                    excludedInvalidTokens++;
                    continue;
                }

                var tokens = market.clobTokenIds.Select(NormalizeTokenId).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Take(2).ToArray();
                if (tokens.Length < 2 || tokens.Any(t => !IsValidTokenIdFormat(t)) || tokens.Distinct(StringComparer.Ordinal).Count() != tokens.Length || tokens.Any(t => seenTokens.Contains(t)))
                {
                    excludedInvalidTokens++;
                    continue;
                }

                if (_invalidTokenQuarantine.ContainsKey(tokens[0]) || _invalidTokenQuarantine.ContainsKey(tokens[1]) || _marketOrderbookQuarantine.ContainsKey(market.id))
                {
                    excludedQuarantinedMarkets++;
                    continue;
                }

                if (!_orderbookEligibility.Get(market.id).Eligible || _orderbookUnavailableMarkets.Contains(market.id) || _reducedUniverseBadHistory.ContainsKey($"market:{market.id}") || tokens.Any(t => _reducedUniverseBadHistory.ContainsKey($"token:{t}")))
                {
                    excludedBadHistory++;
                    continue;
                }

                seenTokens.Add(tokens[0]);
                seenTokens.Add(tokens[1]);
                filtered.Add(market);
            }
        }

        return new ReducedUniverseOrderbookFilterResult(
            RawMarkets: raw.Count,
            FilteredMarkets: filtered.Count,
            ExcludedInvalidTokens: excludedInvalidTokens,
            ExcludedQuarantinedMarkets: excludedQuarantinedMarkets,
            ExcludedBadHistory: excludedBadHistory,
            EligibleMarkets: filtered.Count,
            Markets: filtered);
    }

    public void QuarantineMarketOrderbook(string? marketId, string? yesTokenId, string? noTokenId, string reason, string source = "BatchBookPairBadRequest")
    {
        if (string.IsNullOrWhiteSpace(marketId)) return;
        var now = DateTime.UtcNow;
        var until = now.Add(MarketOrderbookQuarantineTtl);
        lock (_cacheLock)
        {
            TrimMarketOrderbookQuarantineLocked(now);
            if (!_marketOrderbookQuarantine.ContainsKey(marketId)) Interlocked.Increment(ref _marketOrderbookQuarantineAdded);
            var existing = _marketOrderbookQuarantine.TryGetValue(marketId, out var q) ? q : null;
            _marketOrderbookQuarantine[marketId] = new MarketOrderbookQuarantine(marketId, yesTokenId ?? existing?.YesTokenId ?? "", noTokenId ?? existing?.NoTokenId ?? "", reason, existing?.FirstDetectedAtUtc ?? now, now, (int)Math.Round(MarketOrderbookQuarantineTtl.TotalMinutes), source, until);
            _orderbookUnavailableMarkets.Add(marketId);
            _orderbookEligibility.MarkIneligible(marketId, reason, source, until);
            RememberReducedUniverseBadHistoryLocked($"market:{marketId}", now);
            RememberReducedUniverseBadHistoryLocked($"token:{yesTokenId}", now);
            RememberReducedUniverseBadHistoryLocked($"token:{noTokenId}", now);
        }
        Console.WriteLine($"[MARKET_ORDERBOOK_QUARANTINED] MarketId={marketId} Reason={reason} Source={source} TtlMinutes={(int)Math.Round(MarketOrderbookQuarantineTtl.TotalMinutes)}");
        PublishStats();
    }

    private void TrimMarketOrderbookQuarantineLocked(DateTime now)
    {
        foreach (var key in _marketOrderbookQuarantine.Where(x => x.Value.ExpiresAtUtc <= now).Select(x => x.Key).ToList())
        {
            _marketOrderbookQuarantine.Remove(key);
            Interlocked.Increment(ref _marketOrderbookQuarantineExpired);
            _orderbookEligibility.MarkEligible(key);
        }
    }

    public OrderbookCircuitBreakerState CircuitBreakerState
    {
        get
        {
            lock (_cacheLock)
            {
                RefreshCircuitBreakerStateLocked(DateTime.UtcNow);
                return _circuitBreakerState;
            }
        }
    }

    private bool IsCircuitBreakerActive()
    {
        lock (_cacheLock)
        {
            RefreshCircuitBreakerStateLocked(DateTime.UtcNow);
            return _circuitBreakerState != OrderbookCircuitBreakerState.Closed;
        }
    }

    private OrderbookCircuitBreakerState RecordModeRequest()
    {
        var state = CircuitBreakerState;
        if (state == OrderbookCircuitBreakerState.HalfOpen) Interlocked.Increment(ref _batchBookCanaryRequests);
        else if (state == OrderbookCircuitBreakerState.Recovering) Interlocked.Increment(ref _batchBookRecoveryRequests);
        else
        {
            Interlocked.Increment(ref _batchBookNormalRequests);
            if (state == OrderbookCircuitBreakerState.Closed) Interlocked.Increment(ref _batchBookNormalRequestsBeforeBreakerOpen);
            else
            {
                Interlocked.Increment(ref _batchBookNormalRequestsAfterBreakerOpen);
                Interlocked.Increment(ref _truePostBreakerNormalRequests);
            }
        }
        return state;
    }

    private void RecordModeBadRequest(OrderbookCircuitBreakerState requestState)
    {
        var state = CircuitBreakerState;
        if (requestState == OrderbookCircuitBreakerState.Closed && state != OrderbookCircuitBreakerState.Closed)
        {
            Interlocked.Increment(ref _inFlightBeforeBreakerCompletedAfterOpen);
            Interlocked.Increment(ref _inFlightBeforeBreakerBadRequestsAfterOpen);
            Interlocked.Increment(ref _batchBookNormalBadRequests);
            Interlocked.Increment(ref _batchBookNormalBadRequestsBeforeBreakerOpen);
            return;
        }
        if (state == OrderbookCircuitBreakerState.HalfOpen) Interlocked.Increment(ref _batchBookCanaryBadRequests);
        else if (state == OrderbookCircuitBreakerState.Recovering) Interlocked.Increment(ref _batchBookRecoveryBadRequests);
        else
        {
            Interlocked.Increment(ref _batchBookNormalBadRequests);
            if (state == OrderbookCircuitBreakerState.Closed) Interlocked.Increment(ref _batchBookNormalBadRequestsBeforeBreakerOpen);
            else
            {
                Interlocked.Increment(ref _batchBookNormalBadRequestsAfterBreakerOpen);
                Interlocked.Increment(ref _truePostBreakerBadRequests);
            }
        }
    }

    private void RecordModeTimeout()
    {
        if (CircuitBreakerState == OrderbookCircuitBreakerState.HalfOpen) Interlocked.Increment(ref _batchBookCanaryTimeouts);
    }

    private void RecordBadRequestAndMaybeOpenCircuit()
    {
        var now = DateTime.UtcNow;
        lock (_cacheLock)
        {
            _badRequestTimes.Enqueue(now);
            _batchBadRequestsThisCycle++;
            if (_circuitBreakerState == OrderbookCircuitBreakerState.Closed && _lastClosedUtc.HasValue && now - _lastClosedUtc.Value <= TimeSpan.FromMinutes(5)) Interlocked.Increment(ref _orderbookPostCloseBadRequests);
            TrimRollingQueuesLocked(now);
            var rate = _batchRequests <= 0 ? 0d : (double)_batchBadRequests / Math.Max(1, _batchRequests);
            var shouldOpen = Interlocked.Read(ref _batchBookNormalBadRequestsBeforeBreakerOpen) >= 25
                || Interlocked.Read(ref _batchInvalidTokens) >= 25
                || _badRequestTimes.Count >= Math.Min(CircuitBreakerBadRequestsPerHourThreshold, 25)
                || _invalidTokenTimes.Count >= Math.Min(CircuitBreakerInvalidTokensPerHourThreshold, 25)
                || (Interlocked.Read(ref _batchRequests) >= 50 && rate >= Math.Min(CircuitBreakerBadRequestRateThreshold, 0.05))
                || _orderbookUnavailableMarkets.Count > CircuitBreakerUnavailableMarketsThreshold
                || _batchBadRequestsThisCycle > MaxBatchBookBadRequestsPerCycle;
            if (_circuitBreakerState == OrderbookCircuitBreakerState.Recovering && _batchBadRequests > _postCloseBaselineBadRequests)
            {
                Interlocked.Increment(ref _reducedUniversePostRecoveryBadRequests);
                Interlocked.Increment(ref _orderbookRecoveryBadRequests);
                Interlocked.Increment(ref _orderbookRecoveryFailedCount);
                Console.WriteLine("[ORDERBOOK_CIRCUIT_BREAKER_RECOVERY_FAILED] Reason=RecoveringBadRequest");
                OpenCircuitBreakerLocked(now, "RecoveringBadRequest");
            }
            else if (_circuitBreakerState == OrderbookCircuitBreakerState.Closed && _lastClosedUtc.HasValue && now - _lastClosedUtc.Value <= TimeSpan.FromMinutes(5) && _batchBadRequests - _postCloseBaselineBadRequests > MaxBatchBookBadRequestsPerCycle)
                OpenCircuitBreakerLocked(now, "PostCloseBadRequestBurst");
            else if (shouldOpen)
                OpenCircuitBreakerLocked(now, "BadRequestThreshold");
        }
    }

    private void OpenCircuitBreakerLocked(DateTime now, string reason)
    {
        if (_circuitBreakerState == OrderbookCircuitBreakerState.Closed
            && _lastClosedUtc.HasValue
            && now - _lastClosedUtc.Value <= TimeSpan.FromMinutes(5)
            && (reason.Contains("BadRequest", StringComparison.OrdinalIgnoreCase) || reason.Contains("Invalid", StringComparison.OrdinalIgnoreCase)))
        {
            Interlocked.Increment(ref _orderbookCircuitBreakerReopenedAfterClose);
            Console.WriteLine($"[ORDERBOOK_CIRCUIT_BREAKER_REOPENED_AFTER_CLOSE] Reason=PostCloseBadRequestBurst");
            reason = "PostCloseBadRequestBurst";
        }
        if (_circuitBreakerState != OrderbookCircuitBreakerState.Open) Interlocked.Increment(ref _orderbookCircuitBreakerOpenCount);
        _circuitBreakerState = OrderbookCircuitBreakerState.Open;
        _recoveringSinceUtc = null;
        _circuitBreakerLastOpenReason = reason;
        _circuitBreakerOpenUntilUtc = now.Add(_currentCircuitBreakerCooldown);
        if (!_circuitBreakerLoggedOpen)
        {
            _circuitBreakerLoggedOpen = true;
            Console.WriteLine($"[ORDERBOOK_CIRCUIT_BREAKER_OPENED] Reason={reason} CooldownMinutes={(int)Math.Ceiling(_currentCircuitBreakerCooldown.TotalMinutes)}");
        }
        PublishStats();
    }

    private void RefreshCircuitBreakerStateLocked(DateTime now)
    {
        if (_circuitBreakerState == OrderbookCircuitBreakerState.Open && _circuitBreakerOpenUntilUtc <= now)
        {
            _circuitBreakerState = OrderbookCircuitBreakerState.HalfOpen;
            _circuitBreakerLoggedOpen = false;
        }
        if (_circuitBreakerState == OrderbookCircuitBreakerState.Recovering && _recoveringSinceUtc.HasValue && now - _recoveringSinceUtc.Value >= RecoveryDuration)
        {
            _circuitBreakerState = OrderbookCircuitBreakerState.Closed;
            _lastClosedUtc = now;
            _recoveringSinceUtc = null;
            _currentCircuitBreakerCooldown = CircuitBreakerInitialCooldown;
            Interlocked.Increment(ref _orderbookRecoverySucceededCount);
            Console.WriteLine("[ORDERBOOK_CIRCUIT_BREAKER_RECOVERY_SUCCEEDED] State=Closed");
        }
    }

    private void EvaluateHalfOpenCanaryResult()
    {
        lock (_cacheLock)
        {
            if (_circuitBreakerState != OrderbookCircuitBreakerState.HalfOpen) return;
            var badDelta = Interlocked.Read(ref _batchBadRequests) - _halfOpenBaselineBadRequests;
            var timeoutDelta = Interlocked.Read(ref _batchTimeouts) - _halfOpenBaselineTimeouts;
            if (badDelta > CircuitBreakerHalfOpenMaxBadRequests || timeoutDelta > CircuitBreakerHalfOpenMaxTimeouts)
            {
                Interlocked.Increment(ref _orderbookCircuitBreakerHalfOpenFailed);
                var minutes = Math.Min(CircuitBreakerMaxCooldown.TotalMinutes, Math.Max(CircuitBreakerInitialCooldown.TotalMinutes, _currentCircuitBreakerCooldown.TotalMinutes * CircuitBreakerCooldownBackoffMultiplier));
                if (minutes > _currentCircuitBreakerCooldown.TotalMinutes) Interlocked.Increment(ref _orderbookCircuitBreakerCooldownExtensions);
                _currentCircuitBreakerCooldown = TimeSpan.FromMinutes(minutes);
                var reason = badDelta > CircuitBreakerHalfOpenMaxBadRequests ? "BadRequest" : timeoutDelta > CircuitBreakerHalfOpenMaxTimeouts ? "Timeout" : "UnexpectedException";
                _circuitBreakerLastHalfOpenFailureReason = reason;
                Console.WriteLine($"[ORDERBOOK_CIRCUIT_BREAKER_HALF_OPEN_FAILED] Reason={reason} CanaryRequests={Interlocked.Read(ref _batchBookCanaryRequests)} CanaryBadRequests={Interlocked.Read(ref _batchBookCanaryBadRequests)} CanaryTimeouts={Interlocked.Read(ref _batchBookCanaryTimeouts)} CanaryInvalidTokens={Interlocked.Read(ref _batchBookCanaryInvalidTokens)}");
                OpenCircuitBreakerLocked(DateTime.UtcNow, "HalfOpenCanaryFailed");
                return;
            }
            _circuitBreakerState = OrderbookCircuitBreakerState.Recovering;
            _recoveringSinceUtc = DateTime.UtcNow;
            _postCloseBaselineBadRequests = Interlocked.Read(ref _batchBadRequests);
            _postCloseBaselineInvalidTokens = Interlocked.Read(ref _batchInvalidTokens);
            Interlocked.Increment(ref _orderbookCircuitBreakerHalfOpenSucceeded);
            Console.WriteLine($"[ORDERBOOK_CIRCUIT_BREAKER_RECOVERING] State=Recovering RecoveryDurationSeconds={(long)RecoveryDuration.TotalSeconds} RecoveryMaxMarketsPerCycle={RecoveryMaxMarketsPerCycle} RecoveryMaxBatchSize={RecoveryMaxBatchSize}");
        }
    }

    private void TrimRollingQueuesLocked(DateTime now)
    {
        var cutoff = now.AddHours(-1);
        while (_badRequestTimes.Count > 0 && _badRequestTimes.Peek() < cutoff) _badRequestTimes.Dequeue();
        while (_invalidTokenTimes.Count > 0 && _invalidTokenTimes.Peek() < cutoff) _invalidTokenTimes.Dequeue();
    }

    private void PublishStats()
    {
        try { StatsUpdated?.Invoke(GetStats()); }
        catch { }
    }

    public OrderBookServiceStats GetStats()
    {
        TrimInvalidTokenQuarantine();
        lock (_cacheLock)
        {
            TrimMarketOrderbookQuarantineLocked(DateTime.UtcNow);
            TrimReducedUniverseBadHistoryLocked(DateTime.UtcNow);
        }
        var marketActive = _marketOrderbookQuarantine.Count;
        var marketAdded = Interlocked.Read(ref _marketOrderbookQuarantineAdded);
        var marketExpired = Interlocked.Read(ref _marketOrderbookQuarantineExpired);
        var invalidActive = QuarantinedTokenCount;
        var invalidAdded = Interlocked.Read(ref _invalidTokenQuarantineAdded);
        var invalidExpired = Interlocked.Read(ref _invalidTokenQuarantineExpired);
        var marketBalanced = marketAdded == marketExpired + marketActive;
        var invalidBalanced = invalidAdded == invalidExpired + invalidActive;
        var lifecycleMismatch = string.Join("|", new[]
        {
            marketBalanced ? "" : $"MarketOrderbookQuarantineAdded={marketAdded},Expired={marketExpired},Active={marketActive}",
            invalidBalanced ? "" : $"InvalidTokenQuarantineAdded={invalidAdded},Expired={invalidExpired},Active={invalidActive}"
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new OrderBookServiceStats(
            BatchRequests: Interlocked.Read(ref _batchRequests),
            BatchBooksLoaded: Interlocked.Read(ref _batchBooksLoaded),
            SingleRequests: Interlocked.Read(ref _singleRequests),
            CacheHits: Interlocked.Read(ref _cacheHits),
            SnapshotCacheHits: Interlocked.Read(ref _snapshotCacheHits),
            Timeouts: Interlocked.Read(ref _timeouts),
            HttpErrors: Interlocked.Read(ref _httpErrors),
            ParseErrors: Interlocked.Read(ref _parseErrors),
            BookCacheMisses: Interlocked.Read(ref _bookCacheMisses),
            BatchBadRequests: Interlocked.Read(ref _batchBadRequests),
            BatchTimeouts: Interlocked.Read(ref _batchTimeouts),
            BatchRetrySuccesses: Interlocked.Read(ref _batchRetrySuccesses),
            BatchInvalidTokens: Interlocked.Read(ref _batchInvalidTokens),
            BatchSuppressedErrors: Interlocked.Read(ref _batchSuppressedErrors),
            QuarantinedTokens: invalidActive,
            BatchBookErrorSampleCount: BatchBookErrorSampleCount,
            BatchBookSplitRetriesAttempted: Interlocked.Read(ref _batchSplitRetriesAttempted),
            BatchBookSplitRetrySucceeded: Interlocked.Read(ref _batchSplitRetrySucceeded),
            BatchBookSplitRetryFailed: Interlocked.Read(ref _batchSplitRetryFailed),
            BatchBookSingleTokenFailures: Interlocked.Read(ref _batchSingleTokenFailures),
            BatchBookSingleTokenQuarantined: Interlocked.Read(ref _batchSingleTokenQuarantined),
            BatchBookSkippedQuarantinedTokens: Interlocked.Read(ref _batchSkippedQuarantinedTokens),
            BatchBookSkippedMarketsWithQuarantinedTokens: Interlocked.Read(ref _batchSkippedMarketsWithQuarantinedTokens),
            BatchBookRepeatedInvalidTokenAfterQuarantine: Interlocked.Read(ref _batchRepeatedInvalidTokenAfterQuarantine),
            OrderbookUnavailableMarkets: OrderbookUnavailableMarketCount,
            InvalidTokenQuarantineActive: invalidActive,
            InvalidTokenQuarantineAdded: invalidAdded,
            InvalidTokenQuarantineExpired: invalidExpired,
            BatchBookRequestsAvoidedByQuarantine: Interlocked.Read(ref _batchRequestsAvoidedByQuarantine),
            MarketsSkippedByInvalidTokenQuarantine: Interlocked.Read(ref _marketsSkippedByInvalidTokenQuarantine),
            MarketOrderbookQuarantineActive: marketActive,
            MarketOrderbookQuarantineAdded: marketAdded,
            MarketOrderbookQuarantineExpired: marketExpired,
            MarketsSkippedByMarketOrderbookQuarantine: Interlocked.Read(ref _marketsSkippedByMarketOrderbookQuarantine),
            BatchBookRequestsAvoidedByMarketQuarantine: Interlocked.Read(ref _batchRequestsAvoidedByMarketQuarantine),
            OrderbookCircuitBreakerActive: IsCircuitBreakerActive(),
            OrderbookCircuitBreakerState: CircuitBreakerState.ToString(),
            OrderbookCircuitBreakerOpenCount: Interlocked.Read(ref _orderbookCircuitBreakerOpenCount),
            OrderbookCircuitBreakerCooldownRemainingSeconds: Math.Max(0, (long)(_circuitBreakerOpenUntilUtc - DateTime.UtcNow).TotalSeconds),
            OrderbookCircuitBreakerHalfOpenAttempts: Interlocked.Read(ref _orderbookCircuitBreakerHalfOpenAttempts),
            OrderbookCircuitBreakerHalfOpenSucceeded: Interlocked.Read(ref _orderbookCircuitBreakerHalfOpenSucceeded),
            OrderbookCircuitBreakerHalfOpenFailed: Interlocked.Read(ref _orderbookCircuitBreakerHalfOpenFailed),
            BatchBookCanaryTimeouts: Interlocked.Read(ref _batchBookCanaryTimeouts),
            BatchBookCanaryInvalidTokens: Interlocked.Read(ref _batchBookCanaryInvalidTokens),
            BatchBookCanaryOrderbookUnavailable: Interlocked.Read(ref _batchBookCanaryOrderbookUnavailable),
            OrderbookCircuitBreakerLastHalfOpenFailureReason: _circuitBreakerLastHalfOpenFailureReason,
            OrderbookCircuitBreakerCooldownExtensions: Interlocked.Read(ref _orderbookCircuitBreakerCooldownExtensions),
            OrderbookCircuitBreakerLastOpenReason: _circuitBreakerLastOpenReason,
            OrderbookCircuitBreakerRecoveringSinceUtc: _recoveringSinceUtc,
            OrderbookCircuitBreakerRecoveryRemainingSeconds: _circuitBreakerState == OrderbookCircuitBreakerState.Recovering && _recoveringSinceUtc.HasValue ? Math.Max(0, (long)(_recoveringSinceUtc.Value.Add(RecoveryDuration) - DateTime.UtcNow).TotalSeconds) : 0,
            OrderbookCircuitBreakerReopenedAfterClose: Interlocked.Read(ref _orderbookCircuitBreakerReopenedAfterClose),
            OrderbookRequestsBlockedByCircuitBreaker: Interlocked.Read(ref _orderbookRequestsBlockedByCircuitBreaker),
            OrderbookPostCloseBadRequests: Interlocked.Read(ref _orderbookPostCloseBadRequests),
            OrderbookPostCloseInvalidTokens: Interlocked.Read(ref _orderbookPostCloseInvalidTokens),
            SingleTokenIsolationBudgetExhausted: Interlocked.Read(ref _singleTokenIsolationBudgetExhausted),
            BatchBookBadRequestsPreventedEstimate: Interlocked.Read(ref _batchBookBadRequestsPreventedEstimate),
            BatchBookCanaryRequests: Interlocked.Read(ref _batchBookCanaryRequests),
            BatchBookCanaryBadRequests: Interlocked.Read(ref _batchBookCanaryBadRequests),
            BatchBookRecoveryRequests: Interlocked.Read(ref _batchBookRecoveryRequests),
            BatchBookRecoveryBadRequests: Interlocked.Read(ref _batchBookRecoveryBadRequests),
            OrderbookRecoveryLimitedRequests: Interlocked.Read(ref _orderbookRecoveryLimitedRequests),
            OrderbookRecoveryLimitedMarkets: Interlocked.Read(ref _orderbookRecoveryLimitedMarkets),
            OrderbookRecoveryBadRequests: Interlocked.Read(ref _orderbookRecoveryBadRequests),
            OrderbookRecoveryInvalidTokens: Interlocked.Read(ref _orderbookRecoveryInvalidTokens),
            OrderbookRecoverySucceededCount: Interlocked.Read(ref _orderbookRecoverySucceededCount),
            OrderbookRecoveryFailedCount: Interlocked.Read(ref _orderbookRecoveryFailedCount),
            BatchBookNormalRequests: Interlocked.Read(ref _batchBookNormalRequests),
            BatchBookNormalBadRequests: Interlocked.Read(ref _batchBookNormalBadRequests),
            BatchBookNormalRequestsBeforeBreakerOpen: Interlocked.Read(ref _batchBookNormalRequestsBeforeBreakerOpen),
            BatchBookNormalBadRequestsBeforeBreakerOpen: Interlocked.Read(ref _batchBookNormalBadRequestsBeforeBreakerOpen),
            BatchBookNormalRequestsAfterBreakerOpen: Interlocked.Read(ref _batchBookNormalRequestsAfterBreakerOpen),
            BatchBookNormalBadRequestsAfterBreakerOpen: Interlocked.Read(ref _batchBookNormalBadRequestsAfterBreakerOpen),
            QuarantinedMarketsReintroducedBlocked: Interlocked.Read(ref _quarantinedMarketsReintroducedBlocked),
            QuarantinedTokensReintroducedBlocked: Interlocked.Read(ref _quarantinedTokensReintroducedBlocked),
            ReducedUniverseScanPausedByOrderbookHealth: CircuitBreakerState != OrderbookCircuitBreakerState.Closed || invalidActive > 0 || marketActive > 0,
            ReducedUniverseOrderbookRecoveryMode: CircuitBreakerState == OrderbookCircuitBreakerState.HalfOpen || CircuitBreakerState == OrderbookCircuitBreakerState.Recovering,
            ReducedUniverseOrderbookRecoveryCleanWindowSeconds: _recoveringSinceUtc.HasValue ? Math.Max(0, (long)(DateTime.UtcNow - _recoveringSinceUtc.Value).TotalSeconds) : (_lastClosedUtc.HasValue ? Math.Max(0, (long)(DateTime.UtcNow - _lastClosedUtc.Value).TotalSeconds) : 0),
            ReducedUniversePostRecoveryBadRequests: Interlocked.Read(ref _reducedUniversePostRecoveryBadRequests),
            MarketOrderbookQuarantineLifecycleBalanced: marketBalanced,
            InvalidTokenQuarantineLifecycleBalanced: invalidBalanced,
            OrderbookQuarantineLifecycleMismatchReason: lifecycleMismatch,
            InFlightBeforeBreakerCompletedAfterOpen: Interlocked.Read(ref _inFlightBeforeBreakerCompletedAfterOpen),
            InFlightBeforeBreakerBadRequestsAfterOpen: Interlocked.Read(ref _inFlightBeforeBreakerBadRequestsAfterOpen),
            TruePostBreakerNormalRequests: Interlocked.Read(ref _truePostBreakerNormalRequests),
            TruePostBreakerBadRequests: Interlocked.Read(ref _truePostBreakerBadRequests),
            ReducedUniverseBadHistoryActive: _reducedUniverseBadHistory.Count,
            ReducedUniverseBadHistoryLoaded: _reducedUniverseBadHistoryLoaded
        );
    }

    public void ResetStats()
    {
        Interlocked.Exchange(ref _batchRequests, 0);
        Interlocked.Exchange(ref _batchBooksLoaded, 0);
        Interlocked.Exchange(ref _singleRequests, 0);
        Interlocked.Exchange(ref _cacheHits, 0);
        Interlocked.Exchange(ref _snapshotCacheHits, 0);
        Interlocked.Exchange(ref _timeouts, 0);
        Interlocked.Exchange(ref _httpErrors, 0);
        Interlocked.Exchange(ref _parseErrors, 0);
        Interlocked.Exchange(ref _bookCacheMissLogs, 0);
        Interlocked.Exchange(ref _singleRequestCallerLogs, 0);
        Interlocked.Exchange(ref _bookCacheMisses, 0);
        Interlocked.Exchange(ref _batchBadRequests, 0);
        Interlocked.Exchange(ref _batchTimeouts, 0);
        Interlocked.Exchange(ref _batchRetrySuccesses, 0);
        Interlocked.Exchange(ref _batchInvalidTokens, 0);
        Interlocked.Exchange(ref _batchSuppressedErrors, 0);
        Interlocked.Exchange(ref _batchRepeatedInvalidTokenAfterQuarantine, 0);
        Interlocked.Exchange(ref _batchSkippedMarketsWithQuarantinedTokens, 0);
        Interlocked.Exchange(ref _batchSkippedQuarantinedTokens, 0);
        Interlocked.Exchange(ref _batchSingleTokenQuarantined, 0);
        Interlocked.Exchange(ref _batchRequestsAvoidedByQuarantine, 0);
        Interlocked.Exchange(ref _marketsSkippedByInvalidTokenQuarantine, 0);
        Interlocked.Exchange(ref _quarantinedMarketsReintroducedBlocked, 0);
        Interlocked.Exchange(ref _quarantinedTokensReintroducedBlocked, 0);
        Interlocked.Exchange(ref _batchSingleTokenFailures, 0);
        Interlocked.Exchange(ref _batchSplitRetryFailed, 0);
        Interlocked.Exchange(ref _batchSplitRetrySucceeded, 0);
        Interlocked.Exchange(ref _batchSplitRetriesAttempted, 0);
        while (_bookCacheMissSamples.TryDequeue(out _)) { }
        lock (_cacheLock) _batchErrorSamples.Clear();
    }

    public async Task<ClobOrderBook?> GetOrderBookAsync(
     string tokenId,
     CancellationToken ct = default)
    {
        tokenId = NormalizeTokenId(tokenId) ?? "";
        if (string.IsNullOrWhiteSpace(tokenId))
            return null;

        lock (_cacheLock)
        {
            if (_bookCache.TryGetValue(tokenId, out var cached) &&
                DateTime.UtcNow - cached.Time < _cacheTtl)
            {
                Interlocked.Increment(ref _cacheHits);
                return cached.Book;
            }
        }

        if (DisableSingleBookHttpFallback)
        {
            Interlocked.Increment(ref _bookCacheMisses);
            _bookCacheMissSamples.Enqueue(tokenId);
            while (_bookCacheMissSamples.Count > 100) _bookCacheMissSamples.TryDequeue(out _);
            if (LogBookCacheMissDetails && Interlocked.Increment(ref _bookCacheMissLogs) <= 20)
            {
                Console.WriteLine($"[BOOK CACHE MISS - HTTP DISABLED] caller=OrderBookService.GetOrderBookAsync | token_id={tokenId}");
            }

            return null;
        }

        Interlocked.Increment(ref _singleRequests);

        if (Interlocked.Increment(ref _singleRequestCallerLogs) <= 20)
        {
            var trace = new StackTrace();
            var caller = trace.GetFrame(1)?.GetMethod();

            Console.WriteLine(
                $"[SINGLE /book CALLER] " +
                $"{caller?.DeclaringType?.Name}.{caller?.Name} | token_id={tokenId}"
            );
        }

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                var url = $"https://clob.polymarket.com/book?token_id={Uri.EscapeDataString(tokenId)}";

                var json = await _http.GetStringAsync(url, ct);

                var root = JToken.Parse(json);
                var bookToken = root.Type == JTokenType.Object && root["data"] != null
                    ? root["data"]!
                    : root;

                var book = bookToken.ToObject<ClobOrderBook>();

                lock (_cacheLock)
                {
                    _bookCache[tokenId] = (DateTime.UtcNow, book);
                    TrimCacheLocked();
                }

                return book;
            }
            catch (TaskCanceledException)
            {
                Interlocked.Increment(ref _timeouts);

                if (attempt == 2)
                {
                    Console.WriteLine($"[ORDERBOOK TIMEOUT] token_id={tokenId}");
                    return null;
                }

                await Task.Delay(150, ct);
            }
            catch (HttpRequestException ex)
            {
                Interlocked.Increment(ref _httpErrors);

                if (attempt == 2)
                {
                    Console.WriteLine($"[ORDERBOOK HTTP ERROR] token_id={tokenId} | {ex.Message}");
                    return null;
                }

                await Task.Delay(150, ct);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref _parseErrors);
                Console.WriteLine($"[ORDERBOOK ERROR] token_id={tokenId} | {ex.Message}");
                return null;
            }
        }

        return null;
    }


    public CachedOrderBookSnapshot? GetCachedOrderBookSnapshot(string tokenId, string marketId = "")
    {
        tokenId = NormalizeTokenId(tokenId) ?? "";
        if (string.IsNullOrWhiteSpace(tokenId)) return null;

        lock (_cacheLock)
        {
            if (!_bookCache.TryGetValue(tokenId, out var cached) || cached.Book is null)
                return null;

            return new CachedOrderBookSnapshot(
                tokenId,
                marketId,
                cached.Time,
                cached.Book.asks?.Select(ToBookQuote).Where(x => x is not null).Select(x => x!).OrderBy(x => x.Price).ToArray() ?? Array.Empty<BookQuote>(),
                cached.Book.bids?.Select(ToBookQuote).Where(x => x is not null).Select(x => x!).OrderByDescending(x => x.Price).ToArray() ?? Array.Empty<BookQuote>());
        }
    }

    public void ClearAllCache()
    {
        lock (_cacheLock)
        {
            _bookCache.Clear();
            _snapshotCache.Clear();
            while (_bookCacheMissSamples.TryDequeue(out _)) { }
            _batchErrorSamples.Clear();
        }
    }

    public void ClearExpiredCache()
    {
        lock (_cacheLock)
        {
            var now = DateTime.UtcNow;

            foreach (var key in _bookCache
                         .Where(x => now - x.Value.Time > _cacheTtl)
                         .Select(x => x.Key)
                         .ToList())
            {
                _bookCache.Remove(key);
            }

            foreach (var key in _snapshotCache
                         .Where(x => now - x.Value.Time > _cacheTtl)
                         .Select(x => x.Key)
                         .ToList())
            {
                _snapshotCache.Remove(key);
            }
        }
    }


    private void TrimCacheLocked()
    {
        var now = DateTime.UtcNow;
        foreach (var key in _bookCache.Where(x => now - x.Value.Time > _cacheTtl).Select(x => x.Key).ToList()) _bookCache.Remove(key);
        foreach (var key in _snapshotCache.Where(x => now - x.Value.Time > _cacheTtl).Select(x => x.Key).ToList()) _snapshotCache.Remove(key);
        while (_bookCache.Count > _maxCacheEntries)
        {
            var oldest = _bookCache.OrderBy(x => x.Value.Time).FirstOrDefault().Key;
            if (string.IsNullOrWhiteSpace(oldest)) break;
            _bookCache.Remove(oldest);
        }
        while (_snapshotCache.Count > _maxCacheEntries)
        {
            var oldest = _snapshotCache.OrderBy(x => x.Value.Time).FirstOrDefault().Key;
            if (string.IsNullOrWhiteSpace(oldest)) break;
            _snapshotCache.Remove(oldest);
        }
    }

    public BatchPayloadValidationResult ValidateBatchPayload(IEnumerable<string?> tokenIds, int? maxBatchSize = null)
    {
        var maxSize = Math.Max(1, maxBatchSize ?? MaxBatchBookRequestSize);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cleaned = new List<string>();
        var nullsRemoved = 0;
        var duplicatesRemoved = 0;
        var invalidRemoved = 0;
        var quarantinedRemoved = 0;
        var capped = false;
        var invalidSamples = new List<string>();

        foreach (var raw in tokenIds)
        {
            var token = NormalizeTokenId(raw);
            if (string.IsNullOrWhiteSpace(token))
            {
                nullsRemoved++;
                continue;
            }

            if (!IsValidTokenIdFormat(token))
            {
                invalidRemoved++;
                if (invalidSamples.Count < Math.Max(0, MaxInvalidPayloadSamplesToLog)) invalidSamples.Add(token);
                continue;
            }

            if (SkipQuarantinedTokensBeforeBatch && IsTokenQuarantined(token))
            {
                quarantinedRemoved++;
                Interlocked.Increment(ref _batchSkippedQuarantinedTokens);
                continue;
            }

            if (!seen.Add(token))
            {
                duplicatesRemoved++;
                continue;
            }

            if (cleaned.Count >= maxSize)
            {
                capped = true;
                continue;
            }

            cleaned.Add(token);
        }

        return new BatchPayloadValidationResult(cleaned, nullsRemoved, duplicatesRemoved, invalidRemoved, quarantinedRemoved, capped, invalidSamples);
    }

    private async Task<BatchPostResult> TryPostBooksBatchAsync(
        IReadOnlyList<string> batch,
        Func<IEnumerable<string>, object> bodyFactory,
        Dictionary<string, ClobOrderBook> result,
        CancellationToken ct,
        int depth = 0,
        OrderbookCircuitBreakerState requestState = OrderbookCircuitBreakerState.Closed)
    {
        batch = FilterKnownInvalidTokens(batch).ToArray();
        if (batch.Count == 0)
        {
            Interlocked.Increment(ref _batchRequestsAvoidedByQuarantine);
            return BatchPostResult.Success(0);
        }

        try
        {
            var url = "https://clob.polymarket.com/books";
            var jsonBody = JsonConvert.SerializeObject(bodyFactory(batch));

            using var content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");
            var response = await _http.PostAsync(url, content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (response.StatusCode == HttpStatusCode.BadRequest)
            {
                Interlocked.Increment(ref _httpErrors);
                Interlocked.Increment(ref _batchBadRequests);
                RecordModeBadRequest(requestState);
                RecordBadRequestAndMaybeOpenCircuit();
                PublishStats();
                ExportBatchBookBadRequestDiagnostic(batch, json, depth, quarantineApplied: false);
                if (SplitBatchOnBadRequest && batch.Count > 1 && depth == 0 && TryReserveSplitRetry())
                {
                    lock (_cacheLock) OpenCircuitBreakerLocked(DateTime.UtcNow, "InvalidTokenPatternFailedBatchSplit");
                    return await SplitAndRetryBadRequestAsync(batch, bodyFactory, result, ct, depth, json);
                }

                if (batch.Count == 1)
                {
                    var token = batch[0];
                    if (CanRetrySingleToken(token))
                    {
                        Interlocked.Increment(ref _batchInvalidTokens);
                        if (CircuitBreakerState == OrderbookCircuitBreakerState.HalfOpen) Interlocked.Increment(ref _batchBookCanaryInvalidTokens);
                        Interlocked.Increment(ref _batchSingleTokenFailures);
                        QuarantineInvalidToken(token, "SingleTokenBadRequest");
                        Interlocked.Increment(ref _batchSingleTokenQuarantined);
                    }
                    else
                    {
                        Interlocked.Increment(ref _batchRequestsAvoidedByQuarantine);
                    }
                    if (ShouldLogBatchDiagnostic("BATCH_BOOK_INVALID_TOKEN_SUPPRESSED", token, batch.Count))
                        Console.WriteLine($"[BATCH_BOOK_INVALID_TOKEN_SUPPRESSED] TokenId={token} Reason=SingleTokenBadRequest");
                }
                else
                {
                    var isolated = await IsolateInvalidTokensAsync(batch, bodyFactory, result, ct, depth + 1);
                    if (isolated.BadRequest || isolated.FailedTokens > 0) return isolated;
                    if (ShouldLogBatchDiagnostic("BATCH_BOOK_ERROR", $"badrequest:size:{batch.Count}", batch.Count))
                        Console.WriteLine($"[BATCH_BOOK_ERROR] Status=400 BatchSize={batch.Count} RetryStrategy=TokenIsolation");
                }

                return BatchPostResult.FromBadRequest(0, batch.Count);
            }

            if (!response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _httpErrors);
                if (ShouldLogBatchDiagnostic("BATCH_BOOK_ERROR", response.StatusCode.ToString(), batch.Count))
                    Console.WriteLine($"[BATCH_BOOK_ERROR] Status={(int)response.StatusCode} BatchSize={batch.Count} RetryStrategy=None");
                return BatchPostResult.Failed(0);
            }

            var root = JToken.Parse(json);
            var booksArray = ExtractBooksArray(root);
            if (booksArray == null)
            {
                Interlocked.Increment(ref _parseErrors);
                if (ShouldLogBatchDiagnostic("BATCH_BOOK_ERROR", $"shape:{root.Type}", batch.Count))
                    Console.WriteLine($"[BATCH_BOOK_ERROR] Status=ParseError BatchSize={batch.Count} RetryStrategy=None Shape={root.Type}");
                return BatchPostResult.Failed(0);
            }

            var loaded = StoreBooks(booksArray, result);
            if (loaded == 0 && ShouldLogBatchDiagnostic("BATCH_BOOK_ERROR", "loaded:0", batch.Count))
                Console.WriteLine($"[BATCH_BOOK_ERROR] Status=NoBooksLoaded BatchSize={batch.Count} RetryStrategy=None");

            return BatchPostResult.Success(loaded);
        }
        catch (TaskCanceledException)
        {
            Interlocked.Increment(ref _timeouts);
            Interlocked.Increment(ref _batchTimeouts);
            RecordModeTimeout();
            if (CircuitBreakerState == OrderbookCircuitBreakerState.Recovering)
            {
                Console.WriteLine("[ORDERBOOK_CIRCUIT_BREAKER_RECOVERY_FAILED] Reason=RecoveringTimeout");
                lock (_cacheLock) OpenCircuitBreakerLocked(DateTime.UtcNow, "RecoveringTimeout");
            }
            if (ShouldLogBatchDiagnostic("BATCH_BOOK_TIMEOUT", "timeout", batch.Count))
                Console.WriteLine($"[BATCH_BOOK_TIMEOUT] BatchSize={batch.Count}");
            return BatchPostResult.Failed(0, timeout: true);
        }
        catch (HttpRequestException ex)
        {
            Interlocked.Increment(ref _httpErrors);
            if (ShouldLogBatchDiagnostic("BATCH_BOOK_HTTP_ERROR", ex.Message, batch.Count))
                Console.WriteLine($"[BATCH_BOOK_HTTP_ERROR] BatchSize={batch.Count} Message={Short(ex.Message)}");
            return BatchPostResult.Failed(0);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _parseErrors);
            if (ShouldLogBatchDiagnostic("BATCH_BOOK_ERROR", ex.Message, batch.Count))
                Console.WriteLine($"[BATCH_BOOK_ERROR] BatchSize={batch.Count} Message={Short(ex.Message)}");
            return BatchPostResult.Failed(0);
        }
    }


    private IReadOnlyList<string> FilterKnownInvalidTokens(IEnumerable<string> tokens)
    {
        var filtered = new List<string>();
        var skipped = 0;
        lock (_cacheLock)
        {
            foreach (var token in tokens.Select(NormalizeTokenId).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>())
            {
                if (_knownInvalidTokensThisCycle.Contains(token) || _invalidTokenQuarantine.ContainsKey(token))
                {
                    skipped++;
                    continue;
                }
                filtered.Add(token);
            }
        }
        if (skipped > 0)
        {
            Interlocked.Add(ref _batchSkippedQuarantinedTokens, skipped);
            Interlocked.Add(ref _batchRequestsAvoidedByQuarantine, skipped);
        }
        return filtered;
    }

    private bool TryReserveSplitRetry()
    {
        lock (_cacheLock)
        {
            if (_batchSplitRetriesThisCycle >= Math.Max(0, MaxBatchSplitRetriesPerCycle))
                return false;
            _batchSplitRetriesThisCycle++;
            return true;
        }
    }

    private bool CanRetrySingleToken(string token)
    {
        lock (_cacheLock)
        {
            TrimRollingQueuesLocked(DateTime.UtcNow);
            if (_knownInvalidTokensThisCycle.Contains(token) || _invalidTokenQuarantine.ContainsKey(token))
                return false;
            if (_singleTokenIsolationsThisCycle >= MaxSingleTokenIsolationsPerCycle || _invalidTokenTimes.Count >= MaxSingleTokenIsolationsPerHour || _knownInvalidTokensThisCycle.Count >= MaxNewTokenQuarantinesPerHour)
                return false;
            if (_knownInvalidTokensThisCycle.Count >= Math.Max(1, MaxInvalidTokensPerCycle))
                return false;
            _singleTokenRetriesThisCycle.TryGetValue(token, out var count);
            if (count >= Math.Max(0, MaxInvalidTokenSingleRetriesPerCycle))
                return false;
            _singleTokenRetriesThisCycle[token] = count + 1;
            _singleTokenIsolationsThisCycle++;
            return true;
        }
    }

    private async Task<BatchPostResult> IsolateInvalidTokensAsync(
        IReadOnlyList<string> batch,
        Func<IEnumerable<string>, object> bodyFactory,
        Dictionary<string, ClobOrderBook> result,
        CancellationToken ct,
        int depth)
    {
        var loadedBefore = result.Count;
        var failed = 0;
        foreach (var token in FilterKnownInvalidTokens(batch))
        {
            if (_singleTokenIsolationsThisCycle >= MaxSingleTokenIsolationsPerCycle)
            {
                Interlocked.Increment(ref _singleTokenIsolationBudgetExhausted);
                Console.WriteLine("[ORDERBOOK_ISOLATION_BUDGET_EXHAUSTED]");
                QuarantineMarketsForTokens(batch, "IsolationBudgetExceeded", "BatchBookPairBadRequest");
                lock (_cacheLock) OpenCircuitBreakerLocked(DateTime.UtcNow, "IsolationBudgetExceeded");
                return BatchPostResult.FromBadRequest(result.Count - loadedBefore, batch.Count);
            }
            Interlocked.Increment(ref _batchRequests);
            var requestState = RecordModeRequest();
            var single = await TryPostBooksBatchAsync(new[] { token }, bodyFactory, result, ct, depth, requestState);
            if (single.BadRequest || single.FailedTokens > 0) failed++;
            if (IsTokenQuarantined(token)) continue;
        }
        var loaded = result.Count - loadedBefore;
        return failed > 0 ? BatchPostResult.FromBadRequest(loaded, failed) : BatchPostResult.Success(loaded);
    }


    private void QuarantineMarketsForTokens(IEnumerable<string> tokens, string reason, string source)
    {
        string[] marketIds;
        lock (_cacheLock)
            marketIds = tokens.Select(t => _tokenMarketIds.TryGetValue(t, out var m) ? m : "").Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var marketId in marketIds)
        {
            var pair = _marketTokenPairs.TryGetValue(marketId, out var p) ? p : ("", "");
            QuarantineMarketOrderbook(marketId, pair.Item1, pair.Item2, reason, source);
        }
    }

    private async Task<BatchPostResult> SplitAndRetryBadRequestAsync(
        IReadOnlyList<string> batch,
        Func<IEnumerable<string>, object> bodyFactory,
        Dictionary<string, ClobOrderBook> result,
        CancellationToken ct,
        int depth,
        string responseBody)
    {
        Interlocked.Increment(ref _batchSplitRetriesAttempted);
        ExportBatchBookBadRequestDiagnostic(batch, responseBody, depth, quarantineApplied: false);
        if (ShouldLogBatchDiagnostic("BATCH_BOOK_BAD_REQUEST", $"size:{batch.Count}|failed:{batch.Count}", batch.Count))
        {
            Console.WriteLine($"[BATCH_BOOK_BAD_REQUEST] BatchSize={batch.Count} Action=SplitAndRetry");
            Console.WriteLine($"[BATCH_BOOK_ERROR] Status=400 BatchSize={batch.Count} RetryStrategy=Split");
        }

        var half = Math.Max(1, batch.Count / 2);
        var left = batch.Take(half).ToArray();
        var right = batch.Skip(half).ToArray();
        var before = result.Count;
        var leftResult = await TryPostBooksBatchAsync(left, bodyFactory, result, ct, depth + 1, CircuitBreakerState);
        var rightResult = await TryPostBooksBatchAsync(right, bodyFactory, result, ct, depth + 1, CircuitBreakerState);
        var loaded = result.Count - before;
        var failed = leftResult.FailedTokens + rightResult.FailedTokens;
        if (loaded > 0)
        {
            Interlocked.Increment(ref _batchRetrySuccesses);
            Interlocked.Increment(ref _batchSplitRetrySucceeded);
        }
        if (failed > 0 || loaded == 0)
        {
            Interlocked.Increment(ref _batchSplitRetryFailed);
        }

        if (ShouldLogBatchDiagnostic("BATCH_BOOK_RETRY_RESULT", $"original:{batch.Count}|failed:{failed / 20}", batch.Count))
            Console.WriteLine($"[BATCH_BOOK_RETRY_RESULT] OriginalBatch={batch.Count} Successful={loaded} Failed={failed}");

        return new BatchPostResult(loaded, leftResult.BadRequest || rightResult.BadRequest, leftResult.Timeout || rightResult.Timeout, failed);
    }

    private int StoreBooks(JArray booksArray, Dictionary<string, ClobOrderBook> result)
    {
        var loaded = 0;
        for (int i = 0; i < booksArray.Count; i++)
        {
            var item = booksArray[i];
            var book = item.ToObject<ClobOrderBook>();
            if (book == null) continue;

            var assetId = book.asset_id ?? item["asset_id"]?.ToString() ?? item["assetId"]?.ToString() ?? item["token_id"]?.ToString() ?? item["tokenId"]?.ToString();
            assetId = NormalizeTokenId(assetId);
            if (string.IsNullOrWhiteSpace(assetId)) continue;

            lock (_cacheLock)
            {
                _bookCache[assetId] = (DateTime.UtcNow, book);
                result[assetId] = book;
                TrimCacheLocked();
            }

            loaded++;
            Interlocked.Increment(ref _batchBooksLoaded);
        }

        return loaded;
    }

    private bool ShouldLogBatchDiagnostic(string eventName, string hash, int batchSize)
    {
        var shouldLog = QuietLogGate?.ShouldLog(
            new LogEventKey("orderbook", eventName),
            new LogEventFingerprint($"{eventName}|{hash}", $"size:{batchSize / 20}"),
            LogImportance.Normal,
            new QuietLogPolicy(OperationalQuietMode, Math.Max(1, Logging.QuietModeDefaultEveryNCycles), Math.Max(1, Logging.QuietModeDefaultEveryMinutes), Logging.QuietModeSuppressRepeatedHash, Math.Max(1, Logging.QuietModeMaxSameEventPerHour), !OperationalQuietMode)) ?? true;
        if (!shouldLog) Interlocked.Increment(ref _batchSuppressedErrors);
        RecordBatchErrorSample(eventName, hash);
        return shouldLog;
    }

    private static bool IsValidTokenIdFormat(string tokenId)
        => Regex.IsMatch(tokenId, "^[0-9]+$");

    private static string? NormalizeTokenId(string? tokenId)
    {
        if (string.IsNullOrWhiteSpace(tokenId))
            return null;

        return tokenId.Trim();
    }

    private static JArray? ExtractBooksArray(JToken root)
    {
        if (root.Type == JTokenType.Array)
            return (JArray)root;

        if (root.Type != JTokenType.Object)
            return null;

        if (root["data"] is JArray data)
            return data;

        if (root["books"] is JArray books)
            return books;

        if (root["orderbooks"] is JArray orderbooks)
            return orderbooks;

        if (root["results"] is JArray results)
            return results;

        return null;
    }

    private static string Short(string value, int max = 300)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        value = value.Replace("\r", " ").Replace("\n", " ");

        return value.Length <= max
            ? value
            : value.Substring(0, max) + "...";
    }

    public async Task<Dictionary<string, ClobOrderBook>> GetOrderBooksBatchAsync(
     IEnumerable<string> tokenIds,
     CancellationToken ct = default)
    {
        TrimInvalidTokenQuarantine();
        var entryState = CircuitBreakerState;
        var tokenArray = tokenIds.ToArray();
        if (entryState == OrderbookCircuitBreakerState.Open) { Interlocked.Add(ref _orderbookRequestsBlockedByCircuitBreaker, tokenArray.Length); return new Dictionary<string, ClobOrderBook>(); }
        if (entryState == OrderbookCircuitBreakerState.HalfOpen) tokenArray = tokenArray.Take(Math.Max(1, CircuitBreakerHalfOpenCanaryMarkets * 2)).ToArray();
        if (entryState == OrderbookCircuitBreakerState.Recovering)
        {
            var limited = tokenArray.Take(Math.Max(1, RecoveryMaxMarketsPerCycle * 2)).ToArray();
            Interlocked.Increment(ref _orderbookRecoveryLimitedRequests);
            Interlocked.Add(ref _orderbookRecoveryLimitedMarkets, Math.Max(1, limited.Length / 2));
            if (tokenArray.Length > limited.Length) Interlocked.Add(ref _orderbookRequestsBlockedByCircuitBreaker, tokenArray.Length - limited.Length);
            tokenArray = limited;
        }
        lock (_cacheLock)
        {
            _knownInvalidTokensThisCycle.Clear();
            _singleTokenRetriesThisCycle.Clear();
            _batchSplitRetriesThisCycle = 0;
            _singleTokenIsolationsThisCycle = 0;
            _batchBadRequestsThisCycle = 0;
        }
        var beforeSkipped = QuarantinedTokenCount;
        var validation = ValidateBatchPayload(tokenArray, int.MaxValue);
        var result = new Dictionary<string, ClobOrderBook>();
        var skippedQuarantined = beforeSkipped > 0 ? tokenArray.Count(t => NormalizeTokenId(t) is string n && IsTokenQuarantined(n)) : 0;

        if (validation.InvalidFormatRemoved > 0) Interlocked.Add(ref _batchInvalidTokens, validation.InvalidFormatRemoved);
        if (LogInvalidBatchPayloadSamples && (validation.NullsRemoved > 0 || validation.DuplicatesRemoved > 0 || validation.InvalidFormatRemoved > 0 || validation.QuarantinedRemoved > 0))
        {
            var sampleHash = string.Join(',', validation.InvalidSamples);
            if (ShouldLogBatchDiagnostic("BATCH_BOOK_PAYLOAD_DIAG", $"n:{validation.NullsRemoved}|d:{validation.DuplicatesRemoved}|i:{validation.InvalidFormatRemoved}|q:{validation.QuarantinedRemoved}|{sampleHash}", validation.TokenIds.Count))
                Console.WriteLine($"[BATCH_BOOK_PAYLOAD_DIAG] BatchSize={validation.TokenIds.Count} DistinctTokenIds={validation.TokenIds.Count} NullsRemoved={validation.NullsRemoved} DuplicatesRemoved={validation.DuplicatesRemoved} InvalidFormatRemoved={validation.InvalidFormatRemoved} QuarantinedRemoved={validation.QuarantinedRemoved}");
        }

        if (validation.QuarantinedRemoved > 0)
            Interlocked.Add(ref _batchRequestsAvoidedByQuarantine, validation.QuarantinedRemoved);

        if (validation.TokenIds.Count == 0)
            return result;

        var quarantinedBeforeCycle = QuarantinedTokenCount;

        lock (_cacheLock)
        {
            _snapshotCache.Clear();
        }

        var batchSize = Math.Max(1, entryState == OrderbookCircuitBreakerState.Recovering || entryState == OrderbookCircuitBreakerState.HalfOpen ? Math.Min(MaxBatchBookRequestSize, RecoveryMaxBatchSize) : MaxBatchBookRequestSize);

        foreach (var batch in validation.TokenIds.Chunk(batchSize))
        {
            var loopState = CircuitBreakerState;
            if (loopState == OrderbookCircuitBreakerState.Open)
            {
                Interlocked.Add(ref _orderbookRequestsBlockedByCircuitBreaker, batch.Count());
                continue;
            }
            var batchArray = FilterKnownInvalidTokens(batch).ToArray();
            if (batchArray.Length == 0)
            {
                Interlocked.Increment(ref _batchRequestsAvoidedByQuarantine);
                continue;
            }
            Interlocked.Increment(ref _batchRequests);
            var requestState = RecordModeRequest();

            var primary = await TryPostBooksBatchAsync(
                batchArray,
                bodyFactory: idsBatch => idsBatch
                    .Select(tokenId => new { token_id = tokenId })
                    .ToList(),
                result,
                ct,
                requestState: requestState
            );

            if (primary.Loaded > 0 || primary.BadRequest)
                continue;
            if (CircuitBreakerState == OrderbookCircuitBreakerState.Open)
            {
                Interlocked.Add(ref _orderbookRequestsBlockedByCircuitBreaker, batchArray.Length);
                continue;
            }

            _ = await TryPostBooksBatchAsync(
                batchArray,
                bodyFactory: idsBatch => new
                {
                    @params = idsBatch
                        .Select(tokenId => new { token_id = tokenId })
                        .ToList()
                },
                result,
                ct,
                requestState: requestState
            );
        }

        var quarantinedAfterCycle = QuarantinedTokenCount;
        var newInvalid = Math.Max(0, quarantinedAfterCycle - quarantinedBeforeCycle);
        if (newInvalid > 0 || skippedQuarantined > 0)
            Console.WriteLine($"[BATCH_BOOK_QUARANTINE_SUMMARY] NewInvalid={newInvalid} TotalQuarantined={quarantinedAfterCycle} SkippedThisCycle={skippedQuarantined} Expired=0");

        return result;
    }

    public int OrderbookUnavailableMarketCount { get { lock (_cacheLock) return _orderbookUnavailableMarkets.Count; } }

    private void RememberTokenMarketMap(IEnumerable<Market> markets)
    {
        lock (_cacheLock)
        {
            foreach (var market in markets)
            {
                foreach (var token in market.clobTokenIds.Select(NormalizeTokenId).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>())
                    _tokenMarketIds[token] = market.id;
                if (market.clobTokenIds.Count >= 2)
                    _marketTokenPairs[market.id] = (market.clobTokenIds[0], market.clobTokenIds[1]);
            }
        }
    }

    private void ExportBatchBookBadRequestDiagnostic(IReadOnlyList<string> batch, string responseBody, int splitDepth, bool quarantineApplied)
    {
        var invalidFormatCount = batch.Count(x => !IsValidTokenIdFormat(x));
        var repeatedTokenCount = batch.Count - batch.Distinct(StringComparer.Ordinal).Count();
        var tokenSamples = batch.Take(5).ToArray();
        string[] marketSamples;
        lock (_cacheLock)
            marketSamples = batch.Select(x => _tokenMarketIds.TryGetValue(x, out var m) ? m : "").Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
        var cause = invalidFormatCount > 0 ? "InvalidTokenFormat" : batch.Count == 1 ? "SingleTokenBadRequest" : repeatedTokenCount > 0 ? "RepeatedToken" : "BatchContainsInvalidOrStaleToken";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(responseBody ?? ""))).ToLowerInvariant();
        if (ShouldLogBatchDiagnostic("BATCH_BOOK_BAD_REQUEST_DIAG", $"{cause}|{batch.Count}|{hash[..Math.Min(12, hash.Length)]}", batch.Count))
            Console.WriteLine($"[BATCH_BOOK_BAD_REQUEST_DIAG] BatchSize={batch.Count} DistinctTokenIds={batch.Distinct(StringComparer.Ordinal).Count()} InvalidFormatCount={invalidFormatCount} QuarantinedSkipped=0 Endpoint=/books PayloadShape=ArrayOfTokenIdObjects ResponseError={Short(responseBody ?? "", 120)} FirstTokenSample={tokenSamples.FirstOrDefault() ?? ""} TokenFormatSample={(tokenSamples.FirstOrDefault(IsValidTokenIdFormat) is null ? "Invalid" : "Numeric")} MarketIdSample={marketSamples.FirstOrDefault() ?? ""} Cause={cause}");
        Directory.CreateDirectory(ExportDirectory);
        var path = Path.Combine(ExportDirectory, "batch-book-errors-latest.json");
        var payload = new { timestamp = DateTime.UtcNow, status = 400, cause, batchSize = batch.Count, tokenSamples, marketSamples, responseBodyHash = hash, responseBodySample = Short(responseBody ?? "", 500), splitDepth, quarantineApplied, repeatedTokenCount };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    private void ExportInvalidTokenQuarantineSnapshot()
    {
        Directory.CreateDirectory(ExportDirectory);
        Dictionary<string, DateTime> copy;
        lock (_cacheLock) copy = new Dictionary<string, DateTime>(_invalidTokenQuarantine, StringComparer.Ordinal);
        var payload = new { timestamp = DateTime.UtcNow, ttlMinutes = (int)Math.Round(InvalidTokenQuarantineTtl.TotalMinutes), tokens = copy.OrderBy(x => x.Key).Select(x => new { tokenId = x.Key, expiresAt = x.Value, marketId = _tokenMarketIds.TryGetValue(x.Key, out var m) ? m : "" }).ToArray() };
        File.WriteAllText(Path.Combine(ExportDirectory, "invalid-token-quarantine-latest.json"), System.Text.Json.JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }

    public bool IsTokenQuarantined(string? tokenId)
    {
        var token = NormalizeTokenId(tokenId);
        if (string.IsNullOrWhiteSpace(token)) return false;
        lock (_cacheLock)
        {
            TrimInvalidTokenQuarantineLocked(DateTime.UtcNow);
            return _invalidTokenQuarantine.ContainsKey(token);
        }
    }

    public void QuarantineInvalidToken(string? tokenId, string reason = "SingleTokenBadRequest")
    {
        var token = NormalizeTokenId(tokenId);
        if (string.IsNullOrWhiteSpace(token)) return;
        string marketId;
        lock (_cacheLock)
        {
            var now = DateTime.UtcNow;
            if (_invalidTokenQuarantine.ContainsKey(token)) Interlocked.Increment(ref _batchRepeatedInvalidTokenAfterQuarantine);
            else Interlocked.Increment(ref _invalidTokenQuarantineAdded);
            _invalidTokenQuarantine[token] = now.Add(InvalidTokenQuarantineTtl);
            RememberReducedUniverseBadHistoryLocked($"token:{token}", now);
            _knownInvalidTokensThisCycle.Add(token);
            _invalidTokenTimes.Enqueue(now);
            if (_circuitBreakerState == OrderbookCircuitBreakerState.Recovering && Interlocked.Read(ref _batchInvalidTokens) > _postCloseBaselineInvalidTokens)
            {
                Interlocked.Increment(ref _orderbookRecoveryInvalidTokens);
                Interlocked.Increment(ref _orderbookRecoveryFailedCount);
                Console.WriteLine("[ORDERBOOK_CIRCUIT_BREAKER_RECOVERY_FAILED] Reason=RecoveringInvalidToken");
                OpenCircuitBreakerLocked(now, "RecoveringInvalidToken");
            }
            if (_circuitBreakerState == OrderbookCircuitBreakerState.Closed && _lastClosedUtc.HasValue && now - _lastClosedUtc.Value <= TimeSpan.FromMinutes(5))
                Interlocked.Increment(ref _orderbookPostCloseInvalidTokens);
            TrimRollingQueuesLocked(now);
            if (_circuitBreakerState == OrderbookCircuitBreakerState.Closed
                && _invalidTokenTimes.Count >= Math.Min(CircuitBreakerInvalidTokensPerHourThreshold, 25))
                OpenCircuitBreakerLocked(now, "InvalidTokenThreshold");
            marketId = _tokenMarketIds.TryGetValue(token, out var m) ? m : "";
            if (!string.IsNullOrWhiteSpace(marketId))
            {
                _orderbookUnavailableMarkets.Add(marketId);
                if (_marketTokenPairs.TryGetValue(marketId, out var pair) && IsTokenQuarantined(pair.YesTokenId) && IsTokenQuarantined(pair.NoTokenId))
                    QuarantineMarketOrderbook(marketId, pair.YesTokenId, pair.NoTokenId, "BothTokensQuarantined", "TokenQuarantinePair");
            }
            TrimInvalidTokenQuarantineLocked(now);
        }
        Console.WriteLine($"[BATCH_BOOK_TOKEN_QUARANTINED] TokenId={token} MarketId={marketId} Reason={reason} TtlMinutes={(int)Math.Round(InvalidTokenQuarantineTtl.TotalMinutes)}");
        PublishStats();
        if (ExportInvalidTokenQuarantine) ExportInvalidTokenQuarantineSnapshot();
    }

    public void TrimInvalidTokenQuarantine()
    {
        lock (_cacheLock) TrimInvalidTokenQuarantineLocked(DateTime.UtcNow);
    }

    public void TrimAllBoundedStores()
    {
        lock (_cacheLock)
        {
            TrimCacheLocked();
            TrimInvalidTokenQuarantineLocked(DateTime.UtcNow);
            TrimBatchErrorSamplesLocked(DateTime.UtcNow);
        }
    }

    private void TrimInvalidTokenQuarantineLocked(DateTime now)
    {
        foreach (var key in _invalidTokenQuarantine.Where(x => x.Value <= now).Select(x => x.Key).ToList())
        {
            _invalidTokenQuarantine.Remove(key);
            Interlocked.Increment(ref _invalidTokenQuarantineExpired);
        }
        while (_invalidTokenQuarantine.Count > Math.Max(1, MaxInvalidTokenCacheEntries))
        {
            var oldest = _invalidTokenQuarantine.OrderBy(x => x.Value).First().Key;
            _invalidTokenQuarantine.Remove(oldest);
            Interlocked.Increment(ref _invalidTokenQuarantineExpired);
        }
    }

    private void RecordBatchErrorSample(string eventName, string sample)
    {
        lock (_cacheLock)
        {
            _batchErrorSamples.Enqueue((DateTime.UtcNow, eventName, Short(sample, 120)));
            TrimBatchErrorSamplesLocked(DateTime.UtcNow);
        }
    }

    private void TrimBatchErrorSamplesLocked(DateTime now)
    {
        var cutoff = now - BatchBookErrorSampleTtl;
        while (_batchErrorSamples.Count > 0 && (_batchErrorSamples.Peek().Time < cutoff || _batchErrorSamples.Count > Math.Max(1, MaxBatchBookErrorSamples)))
            _batchErrorSamples.Dequeue();
    }

    private static string? GetTokenIdForOutcome(Market market, string wantedOutcome)
    {
        if (market.outcomes.Count == market.clobTokenIds.Count)
        {
            for (int i = 0; i < market.outcomes.Count; i++)
            {
                var outcome = market.outcomes[i]
                    .Trim()
                    .ToLowerInvariant();

                if (outcome == wantedOutcome)
                    return market.clobTokenIds[i];
            }
        }

        return null;
    }

    private static BookQuote? GetBestAsk(ClobOrderBook book)
    {
        return book.asks
            ?.Select(ToBookQuote)
            .Where(x => x != null)
            .OrderBy(x => x!.Price)
            .FirstOrDefault();
    }

    private static BookQuote? GetBestBid(ClobOrderBook book)
    {
        return book.bids
            ?.Select(ToBookQuote)
            .Where(x => x != null)
            .OrderByDescending(x => x!.Price)
            .FirstOrDefault();
    }

    private static BookQuote? ToBookQuote(ClobBookLevel level)
    {
        if (!TryParseDecimal(level.price, out var price))
            return null;

        if (!TryParseDecimal(level.size, out var size))
            return null;

        if (price <= 0 || size <= 0)
            return null;

        return new BookQuote(price, size);
    }

    private static bool TryParseDecimal(string? value, out decimal result)
    {
        return decimal.TryParse(
            value,
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out result
        );
    }
    public IReadOnlyList<string> GetBookCacheMissSamples(int limit)
    {
        var capped = Math.Clamp(limit, 1, 50);
        return _bookCacheMissSamples.Distinct().Take(capped).ToArray();
    }

}

public sealed record BatchPayloadValidationResult(
    IReadOnlyList<string> TokenIds,
    int NullsRemoved,
    int DuplicatesRemoved,
    int InvalidFormatRemoved,
    int QuarantinedRemoved,
    bool Capped,
    IReadOnlyList<string> InvalidSamples);

public sealed record BatchPostResult(int Loaded, bool BadRequest, bool Timeout, int FailedTokens)
{
    public static BatchPostResult Success(int loaded) => new(loaded, false, false, 0);
    public static BatchPostResult FromBadRequest(int loaded, int failedTokens) => new(loaded, true, false, failedTokens);
    public static BatchPostResult Failed(int loaded, bool timeout = false) => new(loaded, false, timeout, 0);
}

public class ClobOrderBook
{
    public string? market { get; set; }
    public string? asset_id { get; set; }
    public string? timestamp { get; set; }
    public string? hash { get; set; }

    public List<ClobBookLevel>? bids { get; set; }
    public List<ClobBookLevel>? asks { get; set; }

    public string? min_order_size { get; set; }
    public string? tick_size { get; set; }
    public bool neg_risk { get; set; }
    public string? last_trade_price { get; set; }
}

public class ClobBookLevel
{
    public string? price { get; set; }
    public string? size { get; set; }
}
