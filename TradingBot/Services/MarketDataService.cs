using Newtonsoft.Json;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public record MarketDiscoverySummary(
    int MarketsDiscovered = 0,
    int PagesFetched = 0,
    int DuplicatesRemoved = 0,
    int InactiveSkipped = 0,
    int ActiveMarketsAvailable = 0,
    int RawLoadedTotal = 0,
    int UniqueMarketsTotal = 0,
    int SkippedClosed = 0,
    int SkippedArchived = 0,
    int SkippedInactive = 0,
    int SkippedMissingTokenIds = 0,
    int SkippedMissingOutcomes = 0,
    int SkippedPastEndDate = 0,
    int SkippedInvalidShape = 0,
    int SkippedUnknownStatus = 0,
    bool DiscoveryHealthy = false,
    string PaginationMode = "offset",
    string? LastPaginationCursor = null,
    string? LastDiscoveryWarning = null,
    DateTime LastDiscoveryCompletedAtUtc = default,
    bool DiscoveryCompleted = false,
    string? StoppedReason = null,
    string? LastDiscoveryError = null,
    int ExpectedMaxPages = 0,
    bool SafetyCapReached = false,
    string DiscoveryLastFailureKind = "Unknown",
    string DiscoveryMode = "Offset",
    bool DiscoveryFallbackAttempted = false,
    string DiscoveryFallbackReason = "None",
    bool DiscoveryFallbackSucceeded = false,
    int DiscoveryFallbackActiveMarkets = 0,
    int DiscoveryObservedHealthyBaselineActiveMarkets = 0);

public class MarketDataService
{
    private readonly HttpClient _http;

    public MarketDataService(HttpClient http)
    {
        _http = http;
    }

    public async Task<(List<Market> Markets, MarketDiscoverySummary Summary)> GetMarketsAsync(TradingBotOptions options, CancellationToken ct = default, int? effectiveBackoffSeconds = null)
    {
        var pageSize = Math.Clamp(options.DiscoveryPageSize, 1, Math.Max(1, options.MarketDiscovery.MaxPageSize));
        var cap = Math.Max(1, options.AbsoluteMaxMarketsSafetyCap);
        var expectedMaxPages = Math.Max(1, (int)Math.Ceiling(cap / (double)Math.Max(1, pageSize)));
        var endpoint = "gamma-api.polymarket.com/markets";
        Console.WriteLine($"[DISCOVERY_PAGINATION_CONFIG] Limit={pageSize} MaxPages={expectedMaxPages} OffsetMode=offset Endpoint={endpoint}");

        var primaryBucket = new DiscoveryBucket("offset-primary-accepting", "true", "false", "false", "true", "volume24hr", "false");
        var primary = await FetchBucketAsync(primaryBucket, "Offset", logSummary: false);
        if (primary.FailureKind == "GammaOffsetPaginationRejected" && options.MarketDiscovery.AllowBootstrapFromPartitionedDiscovery)
        {
            var partitions = new[]
            {
                new DiscoveryBucket("accepting-desc", "true", "false", "false", "true", "volume24hr", "false"),
                new DiscoveryBucket("accepting-asc", "true", "false", "false", "true", "volume24hr", "true"),
                new DiscoveryBucket("active-desc", "true", "false", "false", null, "volume24hr", "false"),
                new DiscoveryBucket("active-asc", "true", "false", "false", null, "volume24hr", "true")
            };
            Console.WriteLine($"[DISCOVERY_PARTITION_STARTED] Buckets={partitions.Length} Reason=GammaOffsetPaginationRejected");
            var merged = new List<Market>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicateMarkets = 0;
            var pages = 0;
            var raw = 0;
            var inactive = 0;
            var skippedClosed = 0;
            var skippedArchived = 0;
            var skippedInactive = 0;
            var skippedMissingTokenIds = 0;
            var skippedMissingOutcomes = 0;
            var skippedPastEndDate = 0;
            var skippedInvalidShape = 0;
            var skippedUnknownStatus = 0;
            var failed = 0;
            var lastFailure = "None";
            foreach (var bucket in partitions)
            {
                var result = await FetchBucketAsync(bucket, "PartitionedOffset", logSummary: true);
                pages += result.PagesFetched;
                raw += result.RawLoadedTotal;
                inactive += result.InactiveSkipped;
                skippedClosed += result.SkippedClosed;
                skippedArchived += result.SkippedArchived;
                skippedInactive += result.SkippedInactive;
                skippedMissingTokenIds += result.SkippedMissingTokenIds;
                skippedMissingOutcomes += result.SkippedMissingOutcomes;
                skippedPastEndDate += result.SkippedPastEndDate;
                skippedInvalidShape += result.SkippedInvalidShape;
                skippedUnknownStatus += result.SkippedUnknownStatus;
                if (result.Failed)
                {
                    failed++;
                    lastFailure = result.FailureKind;
                }
                foreach (var market in result.Markets)
                {
                    var key = BuildDedupKey(market);
                    if (!seen.Add(key))
                    {
                        duplicateMarkets++;
                        continue;
                    }
                    merged.Add(market);
                }
            }
            var required = Math.Max(1, options.MarketDiscovery.MinimumPartitionedActiveMarkets);
            var healthy = merged.Count >= required;
            Console.WriteLine($"[DISCOVERY_PARTITION_SUMMARY] Buckets={partitions.Length} Succeeded={partitions.Length - failed} Failed={failed} UniqueMarkets={seen.Count} ActiveMarkets={merged.Count} DuplicateMarkets={duplicateMarkets} Healthy={healthy.ToString().ToLowerInvariant()}");
            if (options.MarketDiscovery.DiagnosticsOnly)
            {
                var recommendation = merged.Count >= options.MarketDiscovery.MinimumActiveMarketsForBootstrap ? "ConsiderUpdatingMinimumAfterManualReview" : "KeepBlocked";
                Console.WriteLine($"[DISCOVERY_BASELINE_RECOMMENDATION] ObservedActiveMarkets={merged.Count} ConfiguredMinimum={options.MarketDiscovery.MinimumActiveMarketsForBootstrap} Recommendation={recommendation}");
            }
            var summary = new MarketDiscoverySummary(
                merged.Count,
                pages,
                duplicateMarkets,
                inactive,
                merged.Count,
                raw,
                seen.Count,
                skippedClosed,
                skippedArchived,
                skippedInactive,
                skippedMissingTokenIds,
                skippedMissingOutcomes,
                skippedPastEndDate,
                skippedInvalidShape,
                skippedUnknownStatus,
                healthy,
                "partitioned-offset",
                null,
                healthy ? null : "Partitioned discovery did not reach configured bootstrap minimum.",
                DateTime.UtcNow,
                healthy || failed == 0,
                healthy ? null : (failed == 0 ? "BelowMinimumPartitionedActiveMarkets" : "PartitionedDiscoveryPartialFailure"),
                healthy ? null : (failed == 0 ? null : lastFailure),
                expectedMaxPages,
                false,
                failed == 0 ? "Unknown" : lastFailure,
                "PartitionedOffset",
                true,
                "GammaOffsetPaginationRejected",
                healthy,
                merged.Count,
                0);
            if (!healthy)
                Console.WriteLine($"[DISCOVERY_WARNING] Discovery incomplete. PagesFetched={pages} Raw={raw} Active={merged.Count} Reason={summary.StoppedReason ?? "Unknown"}");
            return (merged, summary);
        }

        var primaryHealthy = primary.Markets.Count > 0 && !primary.Failed && (primary.DiscoveryCompleted || primary.SafetyCapReached);
        if (!primaryHealthy)
            Console.WriteLine($"[DISCOVERY_WARNING] Discovery incomplete. PagesFetched={primary.PagesFetched} Raw={primary.RawLoadedTotal} Active={primary.Markets.Count} Reason={primary.StoppedReason ?? "Unknown"}");
        var primarySummary = new MarketDiscoverySummary(
            primary.Markets.Count,
            primary.PagesFetched,
            primary.DuplicatesRemoved,
            primary.InactiveSkipped,
            primary.Markets.Count,
            primary.RawLoadedTotal,
            primary.UniqueMarketsTotal,
            primary.SkippedClosed,
            primary.SkippedArchived,
            primary.SkippedInactive,
            primary.SkippedMissingTokenIds,
            primary.SkippedMissingOutcomes,
            primary.SkippedPastEndDate,
            primary.SkippedInvalidShape,
            primary.SkippedUnknownStatus,
            primaryHealthy,
            "offset",
            null,
            primary.LastWarning,
            DateTime.UtcNow,
            primary.DiscoveryCompleted,
            primary.StoppedReason,
            primary.LastError,
            expectedMaxPages,
            primary.SafetyCapReached,
            primary.FailureKind,
            primary.Failed ? "Blocked" : "Offset",
            false,
            "None",
            false,
            0,
            0);
        return (primary.Markets, primarySummary);

        async Task<BucketResult> FetchBucketAsync(DiscoveryBucket bucket, string mode, bool logSummary)
        {
            var allMarkets = new List<Market>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pages = 0;
            var duplicates = 0;
            var rawLoadedTotal = 0;
            var skippedClosed = 0;
            var skippedArchived = 0;
            var skippedInactive = 0;
            var skippedMissingTokenIds = 0;
            var skippedMissingOutcomes = 0;
            var skippedPastEndDate = 0;
            var skippedInvalidShape = 0;
            var skippedUnknownStatus = 0;
            var sampled = 0;
            string? lastWarning = null;
            var discoveryCompleted = false;
            string? stoppedReason = null;
            string? lastError = null;
            var safetyCapReached = false;
            var failureKind = "Unknown";
            for (var offset = 0; offset < cap;)
            {
                if (ct.IsCancellationRequested) break;
                if (options.MaxMarketsToDiscover > 0 && allMarkets.Count >= options.MaxMarketsToDiscover) break;
                var url =
                    "https://gamma-api.polymarket.com/markets" +
                    $"?active={bucket.ActiveParam}" +
                    $"&closed={bucket.ClosedParam}" +
                    $"&archived={bucket.ArchivedParam}" +
                    (bucket.AcceptingOrdersParam is null ? string.Empty : $"&accepting_orders={bucket.AcceptingOrdersParam}") +
                    $"&order={bucket.OrderParam}" +
                    $"&ascending={bucket.AscendingParam}" +
                    $"&limit={pageSize}" +
                    $"&offset={offset}";
                if (options.MarketDiscovery.DiagnosticsOnly)
                    Console.WriteLine($"[DISCOVERY_REQUEST] Mode={mode} Bucket={bucket.Name} Page={pages + 1} Cursor=<none> Offset={offset} Limit={pageSize} EndpointName=PolymarketGammaMarkets Endpoint={endpoint} ActualLimitParam={pageSize} ActualOffsetParam={offset} ActualCursorParam=<none> ActualClosedParam={bucket.ClosedParam} ActualActiveParam={bucket.ActiveParam} ActualArchivedParam={bucket.ArchivedParam} ActualOrderParam={bucket.OrderParam} QueryShape=active,closed,archived,accepting_orders,order,ascending,limit,offset");
                List<Market> batch;
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(options.MarketDiscovery.RequestTimeoutMs));
                    using var response = await _http.GetAsync(url, timeoutCts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        lastError = $"Response status code does not indicate success: {(int)response.StatusCode}";
                        var gammaOffsetRejected = (int)response.StatusCode == 422 && offset > 0;
                        failureKind = gammaOffsetRejected ? "GammaOffsetPaginationRejected" : "RequestError";
                        stoppedReason = failureKind;
                        Console.WriteLine($"[DISCOVERY_REQUEST_FAILED] Reason={failureKind} Mode={mode} Bucket={bucket.Name} Page={pages + 1} Cursor=<none> Offset={offset} Limit={pageSize} StatusCode={(int)response.StatusCode} EffectiveBackoffSeconds={Math.Max(1, effectiveBackoffSeconds ?? options.MarketDiscovery.RetryBackoffMs / 1000)} EndpointName=PolymarketGammaMarkets Endpoint={endpoint} ActualLimitParam={pageSize} ActualOffsetParam={offset} ActualCursorParam=<none> ActualClosedParam={bucket.ClosedParam} ActualActiveParam={bucket.ActiveParam} ActualArchivedParam={bucket.ArchivedParam} ActualOrderParam={bucket.OrderParam} QueryShape=active,closed,archived,accepting_orders,order,ascending,limit,offset Action=BackoffAndRetryFullDiscovery");
                        break;
                    }
                    var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
                    batch = JsonConvert.DeserializeObject<List<Market>>(json) ?? new List<Market>();
                }
                catch (OperationCanceledException ex)
                {
                    lastError = ex.Message;
                    stoppedReason = "Timeout";
                    failureKind = "Timeout";
                    break;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    stoppedReason = "RequestError";
                    failureKind = "RequestError";
                    break;
                }
                pages++;
                rawLoadedTotal += batch.Count;
                if (options.LogDiscoveryPages || options.MarketDiscovery.DiagnosticsOnly)
                    Console.WriteLine($"[DISCOVERY] Mode={mode} Bucket={bucket.Name} Page={pages} Limit={pageSize} Offset={offset} Cursor=<none> Count={batch.Count} RawTotal={rawLoadedTotal} UniqueTotal={seen.Count} ActiveTotal={allMarkets.Count} InactiveSkipped={skippedClosed + skippedArchived + skippedInactive + skippedMissingTokenIds + skippedMissingOutcomes + skippedPastEndDate + skippedInvalidShape + skippedUnknownStatus}");
                if (batch.Count == 0) { discoveryCompleted = true; break; }
                foreach (var market in batch)
                {
                    if (market is null) continue;
                    var (isTradable, skipReason) = IsTradablePolymarketMarket(market);
                    if (options.LogRawMarketSamples && sampled < options.RawMarketSampleCount)
                    {
                        sampled++;
                        Console.WriteLine($"[DISCOVERY_SAMPLE] id={market.id} conditionId={market.conditionId} q=\"{(market.question ?? string.Empty).Replace("\"", "'")}\" active={Format(market.active)} closed={Format(market.closed)} archived={Format(market.archived)} accepting_orders={Format(market.accepting_orders)} tokens={market.clobTokenIds?.Count ?? 0} outcomes={market.outcomes?.Count ?? 0} endDate={market.endDate ?? market.endDateIso} included={isTradable} reason={skipReason ?? "Included"}");
                    }
                    if (!isTradable)
                    {
                        switch (skipReason)
                        {
                            case "Closed": skippedClosed++; break;
                            case "Archived": skippedArchived++; break;
                            case "Inactive": skippedInactive++; break;
                            case "MissingTokenIds": skippedMissingTokenIds++; break;
                            case "MissingOutcomes": skippedMissingOutcomes++; break;
                            case "PastEndDate": skippedPastEndDate++; break;
                            case "InvalidShape": skippedInvalidShape++; break;
                            default: skippedUnknownStatus++; break;
                        }
                        continue;
                    }
                    var key = BuildDedupKey(market);
                    if (!seen.Add(key))
                    {
                        duplicates++;
                        continue;
                    }
                    if (market.liquidityNum < options.MinLiquidity || market.volume24hrNum < options.MinVolume24h)
                    {
                        skippedInactive++;
                        continue;
                    }
                    allMarkets.Add(market);
                    if (options.MaxMarketsToDiscover > 0 && allMarkets.Count >= options.MaxMarketsToDiscover) break;
                    if (allMarkets.Count >= cap) break;
                }
                if (allMarkets.Count >= cap) { safetyCapReached = true; break; }
                offset += Math.Max(1, pageSize);
            }
            var failed = !string.IsNullOrWhiteSpace(stoppedReason) && stoppedReason != "SafetyCapReached";
            if (allMarkets.Count == 0)
            {
                lastWarning = "No active markets discovered. This usually means pagination/filtering/model mapping is wrong or API returned only inactive markets.";
                Console.WriteLine($"[DISCOVERY_WARNING] {lastWarning}");
            }
            if (logSummary)
                Console.WriteLine($"[DISCOVERY_PARTITION_RESULT] Bucket={bucket.Name} PagesFetched={pages} Raw={rawLoadedTotal} Active={allMarkets.Count} Failed={failed.ToString().ToLowerInvariant()} FailureReason={(failed ? failureKind : "None")}");
            return new BucketResult(allMarkets, pages, duplicates, skippedClosed + skippedArchived + skippedInactive + skippedMissingTokenIds + skippedMissingOutcomes + skippedPastEndDate + skippedInvalidShape + skippedUnknownStatus, allMarkets.Count, rawLoadedTotal, seen.Count, skippedClosed, skippedArchived, skippedInactive, skippedMissingTokenIds, skippedMissingOutcomes, skippedPastEndDate, skippedInvalidShape, skippedUnknownStatus, discoveryCompleted, stoppedReason, lastError, safetyCapReached, failureKind, failed, lastWarning);
        }
    }

    private sealed record DiscoveryBucket(string Name, string ActiveParam, string ClosedParam, string ArchivedParam, string? AcceptingOrdersParam, string OrderParam, string AscendingParam);
    private sealed record BucketResult(List<Market> Markets, int PagesFetched, int DuplicatesRemoved, int InactiveSkipped, int ActiveMarketsAvailable, int RawLoadedTotal, int UniqueMarketsTotal, int SkippedClosed, int SkippedArchived, int SkippedInactive, int SkippedMissingTokenIds, int SkippedMissingOutcomes, int SkippedPastEndDate, int SkippedInvalidShape, int SkippedUnknownStatus, bool DiscoveryCompleted, string? StoppedReason, string? LastError, bool SafetyCapReached, string FailureKind, bool Failed, string? LastWarning);

    private static string Format(bool? value) => value.HasValue ? value.Value.ToString().ToLowerInvariant() : "null";

    public static (bool IsTradable, string? SkipReason) IsTradablePolymarketMarket(Market market)
    {
        if (market.closed == true) return (false, "Closed");
        if (market.archived == true) return (false, "Archived");
        if (market.active == false) return (false, "Inactive");
        if (market.accepting_orders == false || market.acceptingOrders == false) return (false, "Inactive");
        if (market.clobTokenIds is null || market.clobTokenIds.Count < 2) return (false, "MissingTokenIds");
        if (market.outcomes is null || market.outcomes.Count < 2) return (false, "MissingOutcomes");
        if (TryReadEndDateUtc(market, out var endDateUtc) && endDateUtc < DateTime.UtcNow) return (false, "PastEndDate");
        return (true, null);
    }

    private static string BuildDedupKey(Market market)
    {
        if (!string.IsNullOrWhiteSpace(market.conditionId))
            return $"condition:{market.conditionId}";
        if (!string.IsNullOrWhiteSpace(market.id))
            return $"market:{market.id}";
        var tokenKey = string.Join(",", (market.clobTokenIds ?? new List<string>()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return $"tokens:{tokenKey}";
    }

    private static bool TryReadEndDateUtc(Market market, out DateTime endDateUtc)
    {
        endDateUtc = default;
        var raw = market.endDateIso ?? market.endDate;
        return !string.IsNullOrWhiteSpace(raw) && DateTime.TryParse(raw, out endDateUtc);
    }
}
