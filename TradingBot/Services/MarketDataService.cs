using Newtonsoft.Json;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public record MarketDiscoveryHealth(
    bool Healthy,
    bool Degraded,
    bool Partial,
    string ScanConfidence,
    int PagesFetched,
    int ActiveMarketsAvailable,
    int RawLoadedTotal,
    string StoppedReason,
    string? LastError,
    bool SafetyCapReached,
    int RetriesAttempted,
    int FailedPages,
    DateTime CompletedAtUtc);

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
    bool DiscoveryDegraded = false,
    bool PartialDiscovery = false,
    string ScanConfidence = "PartialDiscovery",
    int RetriesAttempted = 0,
    int FailedPages = 0)
{
    public MarketDiscoveryHealth ToHealth() => new(DiscoveryHealthy, DiscoveryDegraded, PartialDiscovery, ScanConfidence, PagesFetched, ActiveMarketsAvailable, RawLoadedTotal, StoppedReason ?? "Unknown", LastDiscoveryError, SafetyCapReached, RetriesAttempted, FailedPages, LastDiscoveryCompletedAtUtc);
}

public static class MarketDiscoveryHealthEvaluator
{
    public static MarketDiscoveryHealth Evaluate(int pagesFetched, int activeMarketsAvailable, int rawLoadedTotal, bool discoveryCompleted, string? stoppedReason, string? lastError, bool safetyCapReached, int retriesAttempted, int failedPages, MarketDiscoveryOptions options, DateTime completedAtUtc)
    {
        var reason = string.IsNullOrWhiteSpace(stoppedReason) ? (discoveryCompleted ? "Completed" : "Unknown") : stoppedReason!;
        if (options.TreatOperationCanceledAtPageCapAsSafetyCap && reason == "OperationCanceled" && safetyCapReached)
            reason = "SafetyCapReached";
        var fatal = reason is "RequestError" or "Timeout" || (reason == "OperationCanceled" && !safetyCapReached);
        var meetsThresholds = pagesFetched >= options.MinHealthyPagesFetched && activeMarketsAvailable >= options.MinHealthyActiveMarkets;
        var healthy = meetsThresholds && !fatal;
        var partial = !discoveryCompleted && !safetyCapReached;
        var degraded = !healthy;
        var confidence = healthy ? "Full" : "PartialDiscovery";
        return new MarketDiscoveryHealth(healthy, degraded, partial, confidence, pagesFetched, activeMarketsAvailable, rawLoadedTotal, reason, lastError, safetyCapReached, retriesAttempted, failedPages, completedAtUtc);
    }
}

public class MarketDataService
{
    private readonly HttpClient _http;

    public MarketDataService(HttpClient http)
    {
        _http = http;
    }

    public async Task<(List<Market> Markets, MarketDiscoverySummary Summary)> GetMarketsAsync(TradingBotOptions options, CancellationToken ct = default)
    {
        var allMarkets = new List<Market>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pageSize = options.DiscoveryPageSize;
        var cap = Math.Max(1, options.AbsoluteMaxMarketsSafetyCap);
        var softLimit = options.MaxMarketsToDiscover;
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
        var paginationMode = "offset";
        string? lastWarning = null;
        var retriesAttempted = 0;
        var failedPages = 0;

        var discoveryCompleted = false;
        string? stoppedReason = null;
        string? lastError = null;
        var expectedMaxPages = Math.Max(1, (int)Math.Ceiling(cap / (double)Math.Max(1, pageSize)));
        var safetyCapReached = false;
        for (var offset = 0; offset < cap;)
        {
            if (ct.IsCancellationRequested) break;
            if (softLimit > 0 && allMarkets.Count >= softLimit) break;

            var url =
                "https://gamma-api.polymarket.com/markets" +
                "?active=true" +
                "&closed=false" +
                "&archived=false" +
                "&accepting_orders=true" +
                "&order=volume24hr" +
                "&ascending=false" +
                $"&limit={pageSize}" +
                $"&offset={offset}";

            List<Market> batch;
            batch = new List<Market>();
            var loaded = false;
            for (var retry = 0; retry <= Math.Max(0, options.MarketDiscovery.MaxRetriesPerPage); retry++)
            {
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(options.MarketDiscovery.RequestTimeoutMs));
                    var json = await _http.GetStringAsync(url, timeoutCts.Token);
                    batch = JsonConvert.DeserializeObject<List<Market>>(json) ?? new List<Market>();
                    loaded = true;
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    lastError = ex.Message; stoppedReason = "OperationCanceled";
                    if (retry < options.MarketDiscovery.MaxRetriesPerPage) { retriesAttempted++; await Task.Delay(options.MarketDiscovery.RetryBackoffMs * (retry + 1), ct); }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message; stoppedReason = "RequestError";
                    if (retry < options.MarketDiscovery.MaxRetriesPerPage) { retriesAttempted++; await Task.Delay(options.MarketDiscovery.RetryBackoffMs * (retry + 1), ct); }
                }
            }
            if (!loaded)
            {
                failedPages++;
                Console.WriteLine($"[MARKET DATA ERROR] {lastError}");
                if (!options.MarketDiscovery.ContinueWithPartialDiscoveryOnError) break;
                else { break; }
            }

            pages++;
            rawLoadedTotal += batch.Count;
            if (options.LogDiscoveryPages)
                Console.WriteLine($"[DISCOVERY] Page={pages} Limit={pageSize} Offset={offset} Cursor=<none> Count={batch.Count} RawTotal={rawLoadedTotal} UniqueTotal={seen.Count} ActiveTotal={allMarkets.Count} InactiveSkipped={skippedClosed + skippedArchived + skippedInactive + skippedMissingTokenIds + skippedMissingOutcomes + skippedPastEndDate + skippedInvalidShape + skippedUnknownStatus}");
            if (batch.Count == 0) { discoveryCompleted = true; break; }
            var effectivePageSize = Math.Min(pageSize, batch.Count);

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
                if (softLimit > 0 && allMarkets.Count >= softLimit) break;
                if (allMarkets.Count >= cap) break;
            }

            if (allMarkets.Count >= cap) { safetyCapReached = true; break; }
            offset += Math.Max(1, effectivePageSize);
        }
        if (!discoveryCompleted && (safetyCapReached || pages >= expectedMaxPages || rawLoadedTotal >= cap))
        {
            safetyCapReached = true;
            if (string.IsNullOrWhiteSpace(stoppedReason) || (options.MarketDiscovery.TreatOperationCanceledAtPageCapAsSafetyCap && stoppedReason == "OperationCanceled"))
                stoppedReason = "SafetyCapReached";
        }

        var inactive = skippedClosed + skippedArchived + skippedInactive + skippedMissingTokenIds + skippedMissingOutcomes + skippedPastEndDate + skippedInvalidShape + skippedUnknownStatus;
        if (allMarkets.Count == 0)
        {
            lastWarning = "No active markets discovered. This usually means pagination/filtering/model mapping is wrong or API returned only inactive markets.";
            Console.WriteLine($"[DISCOVERY_WARNING] {lastWarning}");
        }
        if (options.LogDiscoveryPages)
            Console.WriteLine($"[DISCOVERY] Completed Raw={rawLoadedTotal} Unique={seen.Count} Active={allMarkets.Count} Pages={pages} SkippedClosed={skippedClosed} SkippedArchived={skippedArchived} SkippedInactive={skippedInactive} SkippedMissingTokenIds={skippedMissingTokenIds} SkippedMissingOutcomes={skippedMissingOutcomes} SkippedPastEndDate={skippedPastEndDate} SkippedInvalidShape={skippedInvalidShape} SkippedUnknownStatus={skippedUnknownStatus}");
        if (safetyCapReached)
            Console.WriteLine($"[DISCOVERY] StoppedAtConfiguredPageCap Pages={pages} Raw={rawLoadedTotal} Active={allMarkets.Count} Reason=SafetyCapReached");

        var completedAt = DateTime.UtcNow;
        var health = MarketDiscoveryHealthEvaluator.Evaluate(pages, allMarkets.Count, rawLoadedTotal, discoveryCompleted, stoppedReason, lastError, safetyCapReached, retriesAttempted, failedPages, options.MarketDiscovery, completedAt);
        if (!health.Healthy || (health.SafetyCapReached && options.MarketDiscovery.TreatSafetyCapAsWarning))
            Console.WriteLine($"[DISCOVERY_WARNING] Discovery incomplete. PagesFetched={pages} Raw={rawLoadedTotal} Active={allMarkets.Count} Reason={health.StoppedReason}");
        Console.WriteLine($"[DISCOVERY_HEALTH] Healthy={health.Healthy.ToString().ToLowerInvariant()} Degraded={health.Degraded.ToString().ToLowerInvariant()} Partial={health.Partial.ToString().ToLowerInvariant()} Pages={health.PagesFetched} Active={health.ActiveMarketsAvailable} Reason={health.StoppedReason} ScanConfidence={health.ScanConfidence}");
        var summary = new MarketDiscoverySummary(allMarkets.Count, pages, duplicates, inactive, allMarkets.Count, rawLoadedTotal, seen.Count, skippedClosed, skippedArchived, skippedInactive, skippedMissingTokenIds, skippedMissingOutcomes, skippedPastEndDate, skippedInvalidShape, skippedUnknownStatus, health.Healthy, paginationMode, null, lastWarning, completedAt, discoveryCompleted, health.StoppedReason, lastError, expectedMaxPages, safetyCapReached, health.Degraded, health.Partial, health.ScanConfidence, retriesAttempted, failedPages);
        return (allMarkets, summary);
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
