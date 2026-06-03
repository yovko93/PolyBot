using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Globalization;
using TradingBot.Models;
using System.Diagnostics;

namespace TradingBot.Services;

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
    private readonly HttpClient _http;
    private readonly object _cacheLock = new();

    private long _bookCacheMissLogs;
    private long _singleRequestCallerLogs;
    private long _bookCacheMisses;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _bookCacheMissSamples = new();
    public bool LogBookCacheMissDetails { get; set; } = false;
    public int BookCacheMissSampleSize { get; set; } = 5;

    private readonly Dictionary<string, (DateTime Time, ClobOrderBook? Book)> _bookCache = new();
    private readonly Dictionary<string, (DateTime Time, BinaryOrderBookSnapshot? Snapshot)> _snapshotCache = new();

    private TimeSpan _cacheTtl = TimeSpan.FromSeconds(60);
    private int _maxCacheEntries = 5000;

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
    public void ConfigureCache(TimeSpan ttl, int maxEntries) { _cacheTtl = ttl; _maxCacheEntries = Math.Max(1, maxEntries); }

    public async Task<BinaryOrderBookSnapshot?> GetBinarySnapshotAsync(
        Market market,
        CancellationToken ct = default)
    {
        var snapshotCacheKey = market.id;

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
            NoAsk: GetBestAsk(noBook)
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

    public async Task PrefetchBinarySnapshotsAsync(
    List<Market> markets,
    CancellationToken ct = default)
    {
        var validMarkets = markets
            .Where(m =>
                m != null &&
                !string.IsNullOrWhiteSpace(m.id) &&
                m.clobTokenIds != null &&
                m.clobTokenIds.Count >= 2)
            .ToList();

        var tokenIds = validMarkets
            .SelectMany(m => m.clobTokenIds)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();

        if (tokenIds.Count == 0)
            return;

        var books = await GetOrderBooksBatchAsync(tokenIds, ct);

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
                    NoAsk: GetBestAsk(noBook)
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

    public OrderBookServiceStats GetStats()
    {
        return new OrderBookServiceStats(
            BatchRequests: Interlocked.Read(ref _batchRequests),
            BatchBooksLoaded: Interlocked.Read(ref _batchBooksLoaded),
            SingleRequests: Interlocked.Read(ref _singleRequests),
            CacheHits: Interlocked.Read(ref _cacheHits),
            SnapshotCacheHits: Interlocked.Read(ref _snapshotCacheHits),
            Timeouts: Interlocked.Read(ref _timeouts),
            HttpErrors: Interlocked.Read(ref _httpErrors),
            ParseErrors: Interlocked.Read(ref _parseErrors),
            BookCacheMisses: Interlocked.Read(ref _bookCacheMisses)
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
        while (_bookCacheMissSamples.TryDequeue(out _)) { }
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

    private async Task<int> TryPostBooksBatchAsync(
    IEnumerable<string> batch,
    Func<IEnumerable<string>, object> bodyFactory,
    Dictionary<string, ClobOrderBook> result,
    CancellationToken ct)
    {
        var requestedTokenIds = batch
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        try
        {
            var url = "https://clob.polymarket.com/books";

            var jsonBody = JsonConvert.SerializeObject(bodyFactory(requestedTokenIds));

            using var content = new StringContent(
                jsonBody,
                System.Text.Encoding.UTF8,
                "application/json"
            );

            var response = await _http.PostAsync(url, content, ct);
            var json = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                Interlocked.Increment(ref _httpErrors);
                Console.WriteLine($"[BATCH BOOK ERROR] Status={(int)response.StatusCode} {response.StatusCode}");
                Console.WriteLine($"[BATCH BOOK BODY] {Short(json)}");
                return 0;
            }

            var root = JToken.Parse(json);
            var booksArray = ExtractBooksArray(root);

            if (booksArray == null)
            {
                Interlocked.Increment(ref _parseErrors);
                Console.WriteLine($"[BATCH BOOK ERROR] Unexpected response shape: {root.Type}");
                Console.WriteLine($"[BATCH BOOK BODY] {Short(json)}");
                return 0;
            }

            var loaded = 0;

            for (int i = 0; i < booksArray.Count; i++)
            {
                var item = booksArray[i];

                var book = item.ToObject<ClobOrderBook>();

                if (book == null)
                    continue;

                var assetId =
                    book.asset_id ??
                    item["asset_id"]?.ToString() ??
                    item["assetId"]?.ToString() ??
                    item["token_id"]?.ToString() ??
                    item["tokenId"]?.ToString();

                assetId = NormalizeTokenId(assetId);

                if (string.IsNullOrWhiteSpace(assetId))
                {
                    Console.WriteLine("[BATCH BOOK WARNING] Missing asset_id/token_id in book response.");
                    continue;
                }

                lock (_cacheLock)
                {
                    _bookCache[assetId] = (DateTime.UtcNow, book);
                    result[assetId] = book;
                    TrimCacheLocked();
                }

                loaded++;
                Interlocked.Increment(ref _batchBooksLoaded);
            }

            if (loaded == 0)
            {
                Console.WriteLine("[BATCH BOOK WARNING] Response parsed but loaded 0 books.");
                Console.WriteLine($"[BATCH BOOK BODY] {Short(json)}");
            }

            return loaded;
        }
        catch (TaskCanceledException)
        {
            Interlocked.Increment(ref _timeouts);
            Console.WriteLine("[BATCH BOOK TIMEOUT]");
            return 0;
        }
        catch (HttpRequestException ex)
        {
            Interlocked.Increment(ref _httpErrors);
            Console.WriteLine($"[BATCH BOOK HTTP ERROR] {ex.Message}");
            return 0;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _parseErrors);
            Console.WriteLine($"[BATCH BOOK ERROR] {ex.Message}");
            return 0;
        }
    }

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
        var ids = tokenIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();

        var result = new Dictionary<string, ClobOrderBook>();

        if (ids.Count == 0)
            return result;

        lock (_cacheLock)
        {
            _snapshotCache.Clear();
        }

        const int batchSize = 50;

        foreach (var batch in ids.Chunk(batchSize))
        {
            Interlocked.Increment(ref _batchRequests);

            var loaded = await TryPostBooksBatchAsync(
                batch,
                bodyFactory: idsBatch => idsBatch
                    .Select(tokenId => new { token_id = tokenId })
                    .ToList(),
                result,
                ct
            );

            if (loaded > 0)
                continue;

            loaded = await TryPostBooksBatchAsync(
                batch,
                bodyFactory: idsBatch => new
                {
                    @params = idsBatch
                        .Select(tokenId => new { token_id = tokenId })
                        .ToList()
                },
                result,
                ct
            );
        }

        return result;
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
