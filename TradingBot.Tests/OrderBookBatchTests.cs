using System.Net;
using Newtonsoft.Json.Linq;
using TradingBot.Api;
using TradingBot.Models;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class OrderBookBatchTests
{
    [Fact]
    public void Empty_token_ids_are_removed_before_request()
    {
        var svc = new OrderBookService(new HttpClient(new BatchHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var validation = svc.ValidateBatchPayload(new string?[] { null, "", "  ", "123" });

        Assert.Equal(new[] { "123" }, validation.TokenIds);
        Assert.Equal(3, validation.NullsRemoved);
    }

    [Fact]
    public void Duplicate_token_ids_are_removed_before_request()
    {
        var svc = new OrderBookService(new HttpClient(new BatchHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var validation = svc.ValidateBatchPayload(new[] { "123", "123", "456" });

        Assert.Equal(new[] { "123", "456" }, validation.TokenIds);
        Assert.Equal(1, validation.DuplicatesRemoved);
    }

    [Fact]
    public void Invalid_token_ids_are_removed_or_quarantined()
    {
        var svc = new OrderBookService(new HttpClient(new BatchHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))));
        var validation = svc.ValidateBatchPayload(new[] { "123", "abc", "12-3", "456" });

        Assert.Equal(new[] { "123", "456" }, validation.TokenIds);
        Assert.Equal(2, validation.InvalidFormatRemoved);
    }

    [Fact]
    public async Task BadRequest_triggers_split_retry()
    {
        var calls = 0;
        var svc = new OrderBookService(new HttpClient(new BatchHandler(req =>
        {
            calls++;
            var ids = ReadTokenIds(req);
            if (ids.Count > 1) return Json(HttpStatusCode.BadRequest, "{\"error\":\"Invalid payload\"}");
            return Json(HttpStatusCode.OK, BookJson(ids));
        }))) { MaxBatchBookRequestSize = 4, SplitBatchOnBadRequest = true, QuietLogGate = new QuietLogGate() };

        var books = await svc.GetOrderBooksBatchAsync(new[] { "1", "2" });

        Assert.Equal(2, books.Count);
        Assert.True(calls >= 3);
        Assert.True(svc.GetStats().BatchBadRequests >= 1);
        Assert.True(svc.GetStats().BatchRetrySuccesses >= 1);
    }

    [Fact]
    public async Task Single_bad_token_does_not_fail_full_batch()
    {
        var svc = new OrderBookService(new HttpClient(new BatchHandler(req =>
        {
            var ids = ReadTokenIds(req);
            if (ids.Contains("999")) return Json(HttpStatusCode.BadRequest, "{\"error\":\"Invalid payload\"}");
            return Json(HttpStatusCode.OK, BookJson(ids));
        }))) { MaxBatchBookRequestSize = 3, SplitBatchOnBadRequest = true, QuietLogGate = new QuietLogGate() };

        var books = await svc.GetOrderBooksBatchAsync(new[] { "1", "999", "2" });

        Assert.True(books.ContainsKey("1"));
        Assert.True(books.ContainsKey("2"));
        Assert.False(books.ContainsKey("999"));
        Assert.True(svc.GetStats().BatchInvalidTokens >= 1);
    }

    [Fact]
    public async Task Scanner_prefetch_continues_after_batch_book_bad_request()
    {
        var svc = new OrderBookService(new HttpClient(new BatchHandler(req =>
        {
            var ids = ReadTokenIds(req);
            if (ids.Count > 1) return Json(HttpStatusCode.BadRequest, "{\"error\":\"Invalid payload\"}");
            return Json(HttpStatusCode.OK, BookJson(ids));
        }))) { MaxBatchBookRequestSize = 4, SplitBatchOnBadRequest = true, QuietLogGate = new QuietLogGate() };

        var markets = new List<Market>
        {
            new() { id = "m1", question = "q1", outcomes = new List<string> { "Yes", "No" }, clobTokenIds = new List<string> { "1", "2" } }
        };

        await svc.PrefetchBinarySnapshotsAsync(markets);
        var snapshot = await svc.GetBinarySnapshotAsync(markets[0]);

        Assert.NotNull(snapshot);
    }

    [Fact]
    public async Task Quiet_mode_suppresses_repeated_batch_book_errors()
    {
        var gate = new QuietLogGate();
        var svc = new OrderBookService(new HttpClient(new BatchHandler(_ => Json(HttpStatusCode.BadRequest, "{\"error\":\"Invalid payload\"}"))))
        {
            MaxBatchBookRequestSize = 2,
            SplitBatchOnBadRequest = false,
            OperationalQuietMode = true,
            QuietLogGate = gate
        };

        await svc.GetOrderBooksBatchAsync(new[] { "1", "2" });
        await svc.GetOrderBooksBatchAsync(new[] { "3", "4" });

        Assert.True(gate.Snapshot().QuietSuppressedTotal > 0 || svc.GetStats().BatchSuppressedErrors > 0);
    }

    [Fact]
    public void RuntimeHealth_exposes_batch_book_error_counters()
    {
        var state = new BotRuntimeState();
        state.SetOrderBookServiceStats(new OrderBookServiceStats(10, 1, 0, 0, 0, 2, 3, 0, 0, BatchBadRequests: 4, BatchTimeouts: 2, BatchRetrySuccesses: 5, BatchInvalidTokens: 6, BatchSuppressedErrors: 7));

        var health = RuntimeHealthSnapshot.From(state);
        var line = health.ToLogLine();

        Assert.Equal(10, health.BatchBookRequests);
        Assert.Equal(4, health.BatchBookBadRequests);
        Assert.Equal(2, health.BatchBookTimeouts);
        Assert.Equal(5, health.BatchBookRetrySuccesses);
        Assert.Equal(6, health.BatchBookInvalidTokens);
        Assert.Equal(7, health.BatchBookSuppressedErrors);
        Assert.Contains("BatchBookBadRequests=4", line);
    }

    [Fact]
    public async Task Confirmed_single_token_400_quarantines_and_skips_future_payload()
    {
        var seen = new List<string>();
        var svc = new OrderBookService(new HttpClient(new BatchHandler(req =>
        {
            var ids = ReadTokenIds(req);
            seen.AddRange(ids);
            if (ids.SequenceEqual(new[] { "999" })) return Json(HttpStatusCode.BadRequest, "{\"error\":\"stale token\"}");
            return Json(HttpStatusCode.OK, BookJson(ids));
        }))) { MaxBatchBookRequestSize = 1, SplitBatchOnBadRequest = true, QuietLogGate = new QuietLogGate(), ExportInvalidTokenQuarantine = false };

        await svc.GetOrderBooksBatchAsync(new[] { "999" });
        Assert.True(svc.IsTokenQuarantined("999"));
        await svc.GetOrderBooksBatchAsync(new[] { "999", "1" });

        Assert.Equal(1, seen.Count(x => x == "999"));
        Assert.True(svc.GetStats().BatchBookSkippedQuarantinedTokens >= 1);
    }

    [Fact]
    public async Task Market_with_quarantined_token_is_marked_orderbook_unavailable()
    {
        var svc = new OrderBookService(new HttpClient(new BatchHandler(req =>
        {
            var ids = ReadTokenIds(req);
            if (ids.Contains("bad")) return Json(HttpStatusCode.BadRequest, "{\"error\":\"bad\"}");
            return Json(HttpStatusCode.OK, BookJson(ids));
        }))) { MaxBatchBookRequestSize = 1, ExportInvalidTokenQuarantine = false };
        var market = new Market { id = "m-bad", question = "q", outcomes = new List<string> { "Yes", "No" }, clobTokenIds = new List<string> { "1", "999" } };

        await svc.PrefetchBinarySnapshotsAsync(new List<Market> { market });
        svc.QuarantineInvalidToken("999");

        Assert.Null(await svc.GetBinarySnapshotAsync(market));
        Assert.True(svc.GetStats().OrderbookUnavailableMarkets >= 1);
    }

    [Fact]
    public async Task Split_retry_counters_track_success_and_failure()
    {
        var svc = new OrderBookService(new HttpClient(new BatchHandler(req =>
        {
            var ids = ReadTokenIds(req);
            if (ids.Count > 1) return Json(HttpStatusCode.BadRequest, "{\"error\":\"split\"}");
            if (ids.Contains("999")) return Json(HttpStatusCode.BadRequest, "{\"error\":\"bad\"}");
            return Json(HttpStatusCode.OK, BookJson(ids));
        }))) { MaxBatchBookRequestSize = 3, SplitBatchOnBadRequest = true, QuietLogGate = new QuietLogGate(), ExportInvalidTokenQuarantine = false };

        await svc.GetOrderBooksBatchAsync(new[] { "1", "999", "2" });
        var stats = svc.GetStats();

        Assert.True(stats.BatchBookSplitRetriesAttempted >= 1);
        Assert.True(stats.BatchBookSplitRetrySucceeded >= 1);
        Assert.True(stats.BatchBookSingleTokenFailures >= 1);
        Assert.True(stats.BatchBookSingleTokenQuarantined >= 1);
        Assert.True(stats.BatchRetrySuccesses >= 1);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body) };

    private static string BookJson(IReadOnlyList<string> ids)
        => new JArray(ids.Select(id => new JObject
        {
            ["asset_id"] = id,
            ["bids"] = new JArray(new JObject { ["price"] = "0.49", ["size"] = "10" }),
            ["asks"] = new JArray(new JObject { ["price"] = "0.51", ["size"] = "10" })
        })).ToString();

    private static IReadOnlyList<string> ReadTokenIds(HttpRequestMessage req)
    {
        var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        var root = JToken.Parse(body);
        var arr = root.Type == JTokenType.Array ? (JArray)root : (JArray)(root["params"] ?? new JArray());
        return arr.Select(x => x["token_id"]?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();
    }

    private sealed class BatchHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
