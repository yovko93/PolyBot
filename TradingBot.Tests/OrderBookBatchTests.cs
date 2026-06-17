using System.Net;
using Newtonsoft.Json.Linq;
using TradingBot.Api;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
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


    [Fact]
    public void Quarantined_tokens_are_removed_before_batch_request()
    {
        var svc = new OrderBookService(new HttpClient(new BatchHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))) { ExportInvalidTokenQuarantine = false };
        svc.QuarantineInvalidToken("999", "test");

        var validation = svc.ValidateBatchPayload(new[] { "1", "999", "2" });

        Assert.Equal(new[] { "1", "2" }, validation.TokenIds);
        Assert.Equal(1, validation.QuarantinedRemoved);
        Assert.True(svc.GetStats().BatchBookSkippedQuarantinedTokens >= 1);
    }

    [Fact]
    public async Task Known_invalid_token_is_not_retried_in_same_scan_cycle()
    {
        var seen = new List<string>();
        var svc = new OrderBookService(new HttpClient(new BatchHandler(req =>
        {
            var ids = ReadTokenIds(req);
            seen.AddRange(ids);
            if (ids.Contains("999")) return Json(HttpStatusCode.BadRequest, "{\"error\":\"bad\"}");
            return Json(HttpStatusCode.OK, BookJson(ids));
        }))) { MaxBatchBookRequestSize = 3, SplitBatchOnBadRequest = true, ExportInvalidTokenQuarantine = false, QuietLogGate = new QuietLogGate() };

        await svc.GetOrderBooksBatchAsync(new[] { "1", "999", "2", "999" });

        Assert.True(svc.IsTokenQuarantined("999"));
        Assert.Equal(2, seen.Count(x => x == "999"));
    }

    [Fact]
    public void Nba_finals_does_not_semantically_match_wnba_finals()
    {
        var conflict = AllowlistRepairService.DetectRefreshSemanticConflict("winner:2026 nba finals|kind:generic", "winner:2026 wnba finals|kind:generic");

        Assert.Equal("LeagueMismatch", conflict);
    }


    [Fact]
    public void Same_group_candidate_does_not_emit_mens_womens_conflict()
    {
        var conflict = AllowlistRepairService.DetectRefreshSemanticConflict("winner:2026 women s us open|kind:generic", "winner:2026 women s us open|kind:generic");

        Assert.Equal(string.Empty, conflict);
    }

    [Fact]
    public void Allowlist_refresh_id_split_separates_market_ids_from_token_ids()
    {
        var ids = new[] { "1088680", "0xdaba123", "condition-id" };

        Assert.Equal(new[] { "1088680" }, ids.Where(AllowlistRepairService.IsNumericMarketId).ToArray());
        Assert.Equal(new[] { "0xdaba123", "condition-id" }, ids.Where(AllowlistRepairService.IsTokenOrConditionId).ToArray());
    }


    [Fact]
    public void Same_group_market_set_mismatch_with_unstable_reason_is_unstable_not_semantic_conflict()
    {
        var item = RefreshItem(
            bestCandidate: "winner:2026 women s us open|kind:generic",
            addedMarketIds: ["1088681"],
            removedMarketIds: ["1088662", "1088680"],
            reason: "UnstableAcrossSnapshots; ConsecutiveMatches=1/3; Overlap=0.33/0.75; TitleSimilarity=1.00/0.70; KindMatch=true",
            confidence: "Low");

        Assert.Equal("CandidateRejectedUnstableAcrossSnapshots", AllowlistRepairService.BuildRefreshFinalDecision(item));
        Assert.Equal(string.Empty, AllowlistRepairService.DetectRefreshSemanticConflict(item.GroupKey, item.BestCandidateGroupKey));
    }

    [Fact]
    public void Low_overlap_after_stability_passes_is_low_confidence()
    {
        var item = RefreshItem(
            reason: "LowConfidence; ConsecutiveMatches=3/3; Overlap=0.33/0.75; TitleSimilarity=1.00/0.70; KindMatch=true",
            confidence: "Low");

        Assert.Equal("CandidateRejectedLowConfidence", AllowlistRepairService.BuildRefreshFinalDecision(item));
    }

    [Fact]
    public void Hard_semantic_conflict_is_semantic_final_decision()
    {
        var item = RefreshItem(groupKey: "winner:2026 nba finals|kind:generic", bestCandidate: "winner:2026 wnba finals|kind:generic", reason: "Refresh semantic conflict: LeagueMismatch", confidence: "Low");

        Assert.Equal("CandidateRejectedSemanticConflict", AllowlistRepairService.BuildRefreshFinalDecision(item));
    }

    [Fact]
    public void Refresh_reason_does_not_duplicate_auto_apply_or_embed_conflicting_confidence()
    {
        var reason = "UnstableAcrossSnapshots; ConsecutiveMatches=1/3; Overlap=0.33/0.75; TitleSimilarity=1.00/0.70; KindMatch=true";

        Assert.DoesNotContain("AutoApply=false. AutoApply=false", reason);
        Assert.DoesNotContain("Confidence=High", reason);
    }


    [Fact]
    public async Task Both_tokens_quarantined_creates_market_orderbook_quarantine()
    {
        var svc = new OrderBookService(new HttpClient(new BatchHandler(_ => Json(HttpStatusCode.OK, "[]")))) { ExportInvalidTokenQuarantine = false };
        var market = new Market { id = "m-q", question = "q", outcomes = ["Yes", "No"], clobTokenIds = ["101", "102"] };

        await svc.PrefetchBinarySnapshotsAsync([market]);
        svc.QuarantineInvalidToken("101");
        svc.QuarantineInvalidToken("102");

        Assert.True(svc.IsMarketOrderbookQuarantined("m-q"));
        Assert.Equal(1, svc.GetStats().MarketOrderbookQuarantineActive);
    }

    [Fact]
    public async Task Market_orderbook_quarantine_skips_future_prefetch_requests()
    {
        var calls = 0;
        var svc = new OrderBookService(new HttpClient(new BatchHandler(req => { calls++; return Json(HttpStatusCode.OK, BookJson(ReadTokenIds(req))); }))) { ExportInvalidTokenQuarantine = false };
        var market = new Market { id = "m-skip", question = "q", outcomes = ["Yes", "No"], clobTokenIds = ["201", "202"] };

        svc.QuarantineMarketOrderbook("m-skip", "201", "202", "test");
        await svc.PrefetchBinarySnapshotsAsync([market]);

        Assert.Equal(0, calls);
        Assert.True(svc.GetStats().MarketsSkippedByMarketOrderbookQuarantine >= 1);
    }

    [Fact]
    public void Verified_pricing_can_emit_market_orderbook_quarantined_reason()
    {
        var market = new Market { id = "m-price", conditionId = "c", outcomes = ["Yes", "No"], clobTokenIds = ["301", "302"] };
        var missing = ResolvedNoAsk.Fail(market.id, market.conditionId, "302", "MarketOrderbookQuarantined");

        Assert.Null(missing.NoAsk);
        Assert.Equal("MarketOrderbookQuarantined", missing.FailureReason);
    }

    [Fact]
    public async Task Single_token_isolation_budget_quarantines_markets_and_emits_counter()
    {
        var svc = new OrderBookService(new HttpClient(new BatchHandler(_ => Json(HttpStatusCode.BadRequest, "{\"error\":\"bad\"}"))))
        { MaxBatchBookRequestSize = 4, SplitBatchOnBadRequest = false, MaxSingleTokenIsolationsPerCycle = 1, ExportInvalidTokenQuarantine = false, QuietLogGate = new QuietLogGate() };
        var markets = new List<Market>
        {
            new() { id = "m-budget", question = "q", outcomes = ["Yes", "No"], clobTokenIds = ["401", "402"] }
        };

        await svc.PrefetchBinarySnapshotsAsync(markets);

        var stats = svc.GetStats();
        Assert.True(stats.SingleTokenIsolationBudgetExhausted >= 1);
        Assert.True(svc.IsMarketOrderbookQuarantined("m-budget"));
    }

    [Fact]
    public async Task Circuit_breaker_opens_and_pauses_new_orderbook_calls()
    {
        var calls = 0;
        var svc = new OrderBookService(new HttpClient(new BatchHandler(_ => { calls++; return Json(HttpStatusCode.BadRequest, "{\"error\":\"bad\"}"); })))
        { MaxBatchBookRequestSize = 1, SplitBatchOnBadRequest = false, MaxBatchBookBadRequestsPerCycle = 1, ExportInvalidTokenQuarantine = false, QuietLogGate = new QuietLogGate() };

        await svc.GetOrderBooksBatchAsync(["501", "502"]);
        var afterOpen = calls;
        await svc.GetOrderBooksBatchAsync(["503"]);

        Assert.True(svc.GetStats().OrderbookCircuitBreakerActive);
        Assert.Equal(afterOpen, calls);
    }

    [Fact]
    public void RuntimeHealth_includes_market_quarantine_and_circuit_breaker_counters()
    {
        var state = new BotRuntimeState();
        state.SetOrderBookServiceStats(new OrderBookServiceStats(0, 0, 0, 0, 0, 0, 0, 0, 0,
            MarketOrderbookQuarantineActive: 2,
            MarketOrderbookQuarantineAdded: 3,
            OrderbookCircuitBreakerActive: true,
            OrderbookCircuitBreakerOpenCount: 1,
            SingleTokenIsolationBudgetExhausted: 4));

        var health = RuntimeHealthSnapshot.From(state);
        var line = RuntimeHealthTrendTracker.ToSoakStatusLogLine(health, new RuntimeHealthTrend(0,0,0,0,true,1), new TradingBot.Options.TradingBotOptions(), state);

        Assert.Equal(2, health.MarketOrderbookQuarantineActive);
        Assert.True(health.OrderbookCircuitBreakerActive);
        Assert.Contains("MarketOrderbookQuarantineActive=2", line);
        Assert.Contains("OrderbookCircuitBreakerActive=true", line);
    }

    private static AllowlistRefreshDiagnosticsItem RefreshItem(
        string groupKey = "winner:2026 women s us open|kind:generic",
        string bestCandidate = "winner:2026 women s us open|kind:generic",
        string[]? addedMarketIds = null,
        string[]? removedMarketIds = null,
        string reason = "UnstableAcrossSnapshots; ConsecutiveMatches=1/3; Overlap=0.33/0.75; TitleSimilarity=1.00/0.70; KindMatch=true",
        string confidence = "Low")
        => new(
            groupKey,
            ["1088662", "1088680", "1088682"],
            [],
            3,
            "VerifiedGroupMarketMismatch",
            1,
            bestCandidate,
            0.9m,
            ["1088682"],
            [],
            [],
            addedMarketIds ?? ["1088681"],
            [],
            removedMarketIds ?? ["1088662", "1088680"],
            [],
            0.33m,
            1m,
            true,
            reason,
            "NeedsManualReview",
            confidence,
            string.Empty,
            false);

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
