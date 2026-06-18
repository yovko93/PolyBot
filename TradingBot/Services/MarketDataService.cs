using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;
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
        var gammaMaxSafeOffset = Math.Max(0, options.MarketDiscovery.GammaMaxSafeOffset);
        var diagnostics = new DiscoveryDiagnosticsReport(DateTime.UtcNow, pageSize, gammaMaxSafeOffset, new List<DiscoverySourceReport>(), new List<DiscoveryBucketReport>());
        var endpoint = "gamma-api.polymarket.com/markets";
        Console.WriteLine($"[DISCOVERY_PAGINATION_CONFIG] Limit={pageSize} MaxPages={expectedMaxPages} OffsetMode=offset Endpoint={endpoint} GammaMaxSafeOffset={gammaMaxSafeOffset}");

        var primaryBucket = new DiscoveryBucket("offset-primary-accepting", "true", "false", "false", "true", "volume24hr", "false");
        var primary = await FetchBucketAsync(primaryBucket, "Offset", logSummary: false);
        diagnostics.Sources.Add(ToSourceReport("GammaOffset", primary, primary.FailureKind == "GammaOffsetPaginationRejected" || primary.FailureKind == "OffsetCapReachedBeforeRequest"));
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
            var fingerprints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var queue = new Queue<(DiscoveryBucket Bucket, int Depth)>(partitions.Select(x => (x, 0)));
            var bucketsRun = 0;
            var skippedDuplicateBuckets = 0;
            while (queue.Count > 0)
            {
                var (bucket, depth) = queue.Dequeue();
                var fingerprint = QueryFingerprint(bucket, pageSize, 0);
                if (fingerprints.TryGetValue(fingerprint, out var duplicateBucket))
                {
                    skippedDuplicateBuckets++;
                    Console.WriteLine($"[DISCOVERY_PARTITION_DUPLICATE_QUERY] BucketA={duplicateBucket} BucketB={bucket.Name} QueryFingerprint={fingerprint} Action=SkipDuplicateBucket");
                    continue;
                }
                fingerprints[fingerprint] = bucket.Name;
                bucketsRun++;
                var result = await FetchBucketAsync(bucket, "PartitionedOffset", logSummary: true);
                diagnostics.Buckets.Add(new DiscoveryBucketReport(bucket.Name, result.PagesFetched, result.RawLoadedTotal, result.ActiveMarketsAvailable, result.Failed, result.Failed ? result.FailureKind : "None"));
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
                    if ((result.FailureKind == "GammaOffsetPaginationRejected" || result.CappedIncomplete) && depth < 2)
                    {
                        foreach (var child in SplitBucket(bucket, depth + 1))
                        {
                            if (QueryFingerprint(child, pageSize, 0) == fingerprint) continue;
                            Console.WriteLine($"[DISCOVERY_RECURSIVE_PARTITION_STARTED] ParentBucket={bucket.Name} Reason=BucketHitOffsetCap NextDimension={child.SplitDimension} Depth={depth + 1}");
                            queue.Enqueue((child, depth + 1));
                        }
                    }
                }
                Console.WriteLine($"[DISCOVERY_RECURSIVE_PARTITION_RESULT] Bucket={bucket.Name} Depth={depth} Succeeded={(!result.Failed).ToString().ToLowerInvariant()} Active={result.ActiveMarketsAvailable} Failed={result.Failed.ToString().ToLowerInvariant()} FailureReason={(result.Failed ? result.FailureKind : "None")}");
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
            var healthy = merged.Count >= required && failed == 0;
            Console.WriteLine($"[DISCOVERY_PARTITION_SUMMARY] Buckets={bucketsRun} SkippedDuplicateBuckets={skippedDuplicateBuckets} Succeeded={bucketsRun - failed} Failed={failed} UniqueMarkets={seen.Count} ActiveMarkets={merged.Count} DuplicateMarkets={duplicateMarkets} Healthy={healthy.ToString().ToLowerInvariant()}");
            diagnostics.Sources.Add(new DiscoverySourceReport("GammaCursor", false, 0, "Unavailable", false, null, false, false));
            diagnostics.Sources.Add(new DiscoverySourceReport("GammaPartitionedOffset", healthy, merged.Count, failed == 0 ? "None" : lastFailure, !healthy, healthy ? null : DateTime.UtcNow.AddMinutes(5), !healthy, healthy));
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
                healthy,
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
            WriteDiagnosticsReportIfEnabled(options, diagnostics, summary);
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
        WriteDiagnosticsReportIfEnabled(options, diagnostics, primarySummary);
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
                if (offset > gammaMaxSafeOffset)
                {
                    stoppedReason = "GammaOffsetCapIncomplete";
                    failureKind = "OffsetCapReachedBeforeRequest";
                    Console.WriteLine($"[DISCOVERY_OFFSET_CAP_REACHED] Mode={mode} Bucket={bucket.Name} NextOffset={offset} GammaMaxSafeOffset={gammaMaxSafeOffset} Action=SplitOrBlock");
                    break;
                }
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
                    Console.WriteLine($"[DISCOVERY_REQUEST] Mode={mode} BucketName={bucket.Name} Page={pages + 1} Cursor=<none> Offset={offset} Limit={pageSize} EndpointName=PolymarketGammaMarkets Endpoint={endpoint} ActualAcceptingOrdersParam={bucket.AcceptingOrdersParam ?? "<omitted>"} ActualAscendingParam={bucket.AscendingParam} ActualOrderParam={bucket.OrderParam} ActualActiveParam={bucket.ActiveParam} ActualClosedParam={bucket.ClosedParam} ActualArchivedParam={bucket.ArchivedParam} ActualLimitParam={pageSize} ActualOffsetParam={offset} QueryFingerprint={QueryFingerprint(bucket, pageSize, offset)} ActualCursorParam=<none> QueryShape=active,closed,archived,accepting_orders,order,ascending,limit,offset");
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
                        Console.WriteLine($"[DISCOVERY_REQUEST_FAILED] Reason={failureKind} Mode={mode} BucketName={bucket.Name} Page={pages + 1} Cursor=<none> Offset={offset} Limit={pageSize} StatusCode={(int)response.StatusCode} EffectiveBackoffSeconds={Math.Max(1, effectiveBackoffSeconds ?? options.MarketDiscovery.RetryBackoffMs / 1000)} EndpointName=PolymarketGammaMarkets Endpoint={endpoint} ActualAcceptingOrdersParam={bucket.AcceptingOrdersParam ?? "<omitted>"} ActualAscendingParam={bucket.AscendingParam} ActualOrderParam={bucket.OrderParam} ActualActiveParam={bucket.ActiveParam} ActualClosedParam={bucket.ClosedParam} ActualArchivedParam={bucket.ArchivedParam} ActualLimitParam={pageSize} ActualOffsetParam={offset} QueryFingerprint={QueryFingerprint(bucket, pageSize, offset)} ActualCursorParam=<none> QueryShape=active,closed,archived,accepting_orders,order,ascending,limit,offset Action=BackoffAndRetryFullDiscovery");
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
                Console.WriteLine($"[DISCOVERY_PARTITION_RESULT] BucketName={bucket.Name} PagesFetched={pages} Raw={rawLoadedTotal} Active={allMarkets.Count} Failed={failed.ToString().ToLowerInvariant()} BucketFailed={failed.ToString().ToLowerInvariant()} FailureReason={(failed ? failureKind : "None")} ActualAcceptingOrdersParam={bucket.AcceptingOrdersParam ?? "<omitted>"} ActualAscendingParam={bucket.AscendingParam} ActualOrderParam={bucket.OrderParam} ActualActiveParam={bucket.ActiveParam} ActualClosedParam={bucket.ClosedParam} ActualArchivedParam={bucket.ArchivedParam} ActualLimitParam={pageSize} ActualOffsetParam={Math.Max(0, pages * pageSize)} QueryFingerprint={QueryFingerprint(bucket, pageSize, 0)}");
            return new BucketResult(allMarkets, pages, duplicates, skippedClosed + skippedArchived + skippedInactive + skippedMissingTokenIds + skippedMissingOutcomes + skippedPastEndDate + skippedInvalidShape + skippedUnknownStatus, allMarkets.Count, rawLoadedTotal, seen.Count, skippedClosed, skippedArchived, skippedInactive, skippedMissingTokenIds, skippedMissingOutcomes, skippedPastEndDate, skippedInvalidShape, skippedUnknownStatus, discoveryCompleted, stoppedReason, lastError, safetyCapReached, failureKind, failed, lastWarning, stoppedReason == "GammaOffsetCapIncomplete");
        }
    }

    public static void ExportSourceAudit(TradingBotOptions options, string contentRootPath)
    {
        var sources = new[]
        {
            new DiscoverySourceAuditEntry("PersistedHealthySnapshot", options.MarketDiscovery.PersistedSnapshotPath, false, "Unknown", true, true, true, true, true, "Unknown", false, new[] { "RequiresValidFreshSnapshot", "DiagnosticsOnlyUntilLoaded" }),
            new DiscoverySourceAuditEntry("GammaOffset", "MarketDataService:PolymarketGammaMarkets", true, "Offset", true, true, true, true, true, "Partial", false, new[] { "OffsetCapCanRejectBeforeFullUniverse", "MustPassFullHealthCriteria" }),
            new DiscoverySourceAuditEntry("GammaPartitionedOffset", "MarketDataService:PolymarketGammaMarkets partitioned offset", true, "Offset", true, true, true, true, true, "Partial", false, new[] { "PartitionDimensionsNotProvenComplete", "RecursivePartitioningFrozen" }),
            new DiscoverySourceAuditEntry("KalshiMarketDataService", "KalshiMarketDataService:GetMarketsAsync", true, "Cursor", false, true, true, false, false, "Unknown", false, new[] { "DifferentExchange", "CannotReturnPolymarketTokenIds", "NotScannerSafeForPolymarketOrderbooks" })
        };
        var report = new { CreatedAtUtc = DateTime.UtcNow, Mode = "DiscoverySourceAuditOnly", Scanner = "Disabled", Orderbooks = "Disabled", Paper = "Disabled", LiveTrading = "NotRequired", SourceSelectionOrder = new[] { "PersistedHealthySnapshot", "AlternativeFullMarketSource", "GammaOffset", "GammaPartitionedOffset", "Blocked" }, Sources = sources };
        var path = Path.Combine(contentRootPath, "exports/discovery-source-audit-latest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonConvert.SerializeObject(report, Formatting.Indented));
        Console.WriteLine($"[DISCOVERY_SOURCE_AUDIT_EXPORTED] Path={path} Sources={sources.Length} SafeCandidates={sources.Count(x => x.SafeForScannerCandidate)}");
    }

    private sealed record DiscoverySourceAuditEntry(string SourceName, string EndpointClientName, bool SupportsPagination, string PaginationMode, bool CanReturnTokenIds, bool CanReturnMarketIds, bool CanFilterActive, bool CanFilterClosedArchived, bool CanReturnAcceptingOrders, string EstimatedCompleteness, bool SafeForScannerCandidate, IReadOnlyList<string> MissingRequirements);

    private sealed record DiscoveryBucket(string Name, string ActiveParam, string ClosedParam, string ArchivedParam, string? AcceptingOrdersParam, string OrderParam, string AscendingParam, string SplitDimension = "Initial");
    private sealed record BucketResult(List<Market> Markets, int PagesFetched, int DuplicatesRemoved, int InactiveSkipped, int ActiveMarketsAvailable, int RawLoadedTotal, int UniqueMarketsTotal, int SkippedClosed, int SkippedArchived, int SkippedInactive, int SkippedMissingTokenIds, int SkippedMissingOutcomes, int SkippedPastEndDate, int SkippedInvalidShape, int SkippedUnknownStatus, bool DiscoveryCompleted, string? StoppedReason, string? LastError, bool SafetyCapReached, string FailureKind, bool Failed, string? LastWarning, bool CappedIncomplete = false);
    private sealed record DiscoverySourceReport(string SourceName, bool Healthy, int ActiveMarkets, string FailureKind, bool CanRetry, DateTime? RetryAfterUtc, bool IsPartial, bool IsSafeForScanner);
    private sealed record DiscoveryBucketReport(string BucketName, int PagesFetched, int Raw, int Active, bool Failed, string FailureReason);
    private sealed record DiscoveryDiagnosticsReport(DateTime CreatedAtUtc, int PageSize, int GammaMaxSafeOffset, List<DiscoverySourceReport> Sources, List<DiscoveryBucketReport> Buckets);

    private static DiscoverySourceReport ToSourceReport(string sourceName, BucketResult result, bool canRetry) =>
        new(sourceName, !result.Failed && result.Markets.Count > 0 && !result.CappedIncomplete, result.Markets.Count, result.Failed || result.CappedIncomplete ? result.FailureKind : "None", canRetry || result.CappedIncomplete, (canRetry || result.CappedIncomplete) ? DateTime.UtcNow.AddMinutes(5) : null, result.Failed || result.CappedIncomplete, !result.Failed && !result.CappedIncomplete);


    private static IEnumerable<DiscoveryBucket> SplitBucket(DiscoveryBucket bucket, int depth)
    {
        if (bucket.AcceptingOrdersParam is null)
        {
            yield return bucket with { Name = $"{bucket.Name}-accepting-true", AcceptingOrdersParam = "true", SplitDimension = "accepting_orders" };
            yield return bucket with { Name = $"{bucket.Name}-accepting-false", AcceptingOrdersParam = "false", SplitDimension = "accepting_orders" };
            yield break;
        }
        yield return bucket with { Name = $"{bucket.Name}-active-false", ActiveParam = "false", SplitDimension = "active" };
        yield return bucket with { Name = $"{bucket.Name}-closed-true", ClosedParam = "true", SplitDimension = "closed" };
    }

    private static void WriteDiagnosticsReportIfEnabled(TradingBotOptions options, DiscoveryDiagnosticsReport diagnostics, MarketDiscoverySummary summary)
    {
        if (!options.MarketDiscovery.DiagnosticsOnly) return;
        diagnostics.Sources.Add(new DiscoverySourceReport("PersistedHealthySnapshot", false, 0, "NotEvaluatedHere", false, null, false, false));
        diagnostics.Sources.Add(new DiscoverySourceReport("Blocked", !summary.DiscoveryHealthy, summary.ActiveMarketsAvailable, summary.DiscoveryLastFailureKind, false, null, true, false));
        const string path = "exports/discovery-diagnostics-latest.json";
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, JsonConvert.SerializeObject(new { Summary = summary, diagnostics.CreatedAtUtc, diagnostics.PageSize, diagnostics.GammaMaxSafeOffset, diagnostics.Sources, diagnostics.Buckets }, Formatting.Indented));
        Console.WriteLine($"[DISCOVERY_DIAGNOSTICS_EXPORTED] Path={path} Sources={diagnostics.Sources.Count} Buckets={diagnostics.Buckets.Count}");
    }

    private static string QueryFingerprint(DiscoveryBucket bucket, int limit, int offset)
    {
        var shape = $"active={bucket.ActiveParam}&closed={bucket.ClosedParam}&archived={bucket.ArchivedParam}&accepting_orders={bucket.AcceptingOrdersParam ?? "<omitted>"}&order={bucket.OrderParam}&ascending={bucket.AscendingParam}&limit={limit}&offset={offset}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(shape)))[..16].ToLowerInvariant();
    }

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
