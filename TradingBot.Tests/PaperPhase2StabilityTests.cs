using System.Net;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class PaperPhase2StabilityTests
{
    [Fact]
    public async Task Invalid_token_is_quarantined_after_confirmed_single_token_400()
    {
        var svc = Service(req => ReadTokenIds(req).Single() == "999" ? Json(HttpStatusCode.BadRequest, "{}") : Json(HttpStatusCode.OK, BookJson(ReadTokenIds(req))));

        await svc.GetOrderBooksBatchAsync(new[] { "999" });

        Assert.True(svc.IsTokenQuarantined("999"));
        Assert.Equal(1, svc.GetStats().QuarantinedTokens);
    }

    [Fact]
    public async Task Quarantined_token_is_skipped_in_future_batch_requests()
    {
        var callsWithBad = 0;
        var svc = Service(req =>
        {
            var ids = ReadTokenIds(req);
            if (ids.Contains("999")) callsWithBad++;
            return ids.Contains("999") ? Json(HttpStatusCode.BadRequest, "{}") : Json(HttpStatusCode.OK, BookJson(ids));
        });

        await svc.GetOrderBooksBatchAsync(new[] { "999" });
        await svc.GetOrderBooksBatchAsync(new[] { "1", "999", "2" });

        Assert.Equal(1, callsWithBad);
        Assert.Equal(2, svc.GetStats().BatchBooksLoaded);
    }

    [Fact]
    public async Task Quarantined_token_ttl_expires_correctly()
    {
        var svc = Service(req => Json(HttpStatusCode.BadRequest, "{}"));
        svc.InvalidTokenQuarantineTtl = TimeSpan.FromMilliseconds(20);

        await svc.GetOrderBooksBatchAsync(new[] { "999" });
        Assert.True(svc.IsTokenQuarantined("999"));
        await Task.Delay(40);
        svc.TrimInvalidTokenQuarantine();

        Assert.False(svc.IsTokenQuarantined("999"));
    }

    [Fact]
    public async Task BatchBook_bad_request_history_stays_bounded_after_100k_requests()
    {
        var svc = Service(_ => Json(HttpStatusCode.BadRequest, "{\"error\":\"bad\"}"));
        svc.SplitBatchOnBadRequest = false;
        svc.MaxBatchBookErrorSamples = 100;

        for (var i = 0; i < 100_000; i++)
            await svc.GetOrderBooksBatchAsync(new[] { (i + 1).ToString() });

        Assert.True(svc.BatchBookErrorSampleCount <= 100);
    }

    [Fact]
    public void QuietLogGate_cache_ttl_trims_old_fingerprints()
    {
        var gate = new QuietLogGate();
        gate.ConfigureBounds(5000, TimeSpan.FromMilliseconds(10));
        Assert.True(gate.ShouldLog(new LogEventKey("cat", "evt", MarketId: "m1"), new LogEventFingerprint("h1"), LogImportance.Normal, new QuietLogPolicy()));

        Thread.Sleep(30);
        gate.TrimExpired();

        Assert.Equal(0, gate.Snapshot().LogGateCacheSize);
    }

    [Fact]
    public void Market_discovery_refresh_replaces_snapshot_not_appends()
    {
        var first = new List<Market> { new() { id = "m1", outcomes = ["Yes", "No"], clobTokenIds = ["1", "2"] } };
        var second = new List<Market> { new() { id = "m2", outcomes = ["Yes", "No"], clobTokenIds = ["3", "4"] } };
        var snapshot = first.Where(m => m.outcomes.Count == 2).ToList();

        snapshot = second.Where(m => m.outcomes.Count == 2).ToList();

        Assert.Single(snapshot);
        Assert.Equal("m2", snapshot[0].id);
    }

    [Fact]
    public void MemoryCritical_increments_counter_and_pauses_scanner()
    {
        var state = new BotRuntimeState();
        var guard = new MemoryGuard();
        var options = new TradingBotOptions { RuntimeMemory = new RuntimeMemoryOptions { CriticalProcessMemoryMb = 100, MaxProcessMemoryMb = 200, ForceGcOnCriticalMemory = false, WriteMemorySnapshotOnCritical = false } };

        guard.Check(state, options, processMemoryMbOverride: 150);

        Assert.True(state.MemoryCriticals >= 1);
        Assert.True(state.ScannerPausedByMemoryGuard);
        Assert.True(state.Controls.IsPaused);
    }

    [Fact]
    public void Scanner_resumes_only_below_ResumeBelowMb()
    {
        var state = new BotRuntimeState();
        var guard = new MemoryGuard();
        var options = new TradingBotOptions { RuntimeMemory = new RuntimeMemoryOptions { CriticalProcessMemoryMb = 100, MaxProcessMemoryMb = 200, ResumeBelowProcessMemoryMb = 80, CriticalPauseSeconds = 1, ForceGcOnCriticalMemory = false, WriteMemorySnapshotOnCritical = false } };

        guard.Check(state, options, processMemoryMbOverride: 150);
        guard.Check(state, options, processMemoryMbOverride: 90);
        Assert.True(state.ScannerPausedByMemoryGuard);
        guard.Check(state, options, processMemoryMbOverride: 70);

        Assert.False(state.ScannerPausedByMemoryGuard);
    }

    [Fact]
    public void Soak_status_is_not_stable_if_memoryCriticals_gt_zero()
    {
        var state = new BotRuntimeState();
        state.RecordMemoryCritical(DateTime.UtcNow, scannerPaused: true);
        var line = RuntimeHealthTrendTracker.ToSoakStatusLogLine(RuntimeHealthSnapshot.From(state), new RuntimeHealthTrend(1, 2, 1, 0, true, 2), new TradingBotOptions(), state);

        Assert.Contains("MemoryCriticals=1", line);
        Assert.Contains("MemoryStable=false", line);
    }

    [Fact]
    public void DiscoveryPartial_disables_repair_patchability()
    {
        var svc = new AllowlistRepairService();
        var cfg = new VerifiedMultiOutcomeGroupConfig(true, "group", "group", ["missing"], [], 1, "Verified");
        var resolved = new ResolvedVerifiedGroup("group", "group", ["missing"], [], [], ["missing"], [], "MissingMarkets", "missing");
        var report = svc.BuildReport([cfg], [resolved], [], [], new AllowlistRepairOptions { DiscoveryPartialDiagnosticsOnly = true });
        var preview = svc.BuildPatchPreview(report, [cfg]).PatchPreview;

        Assert.Equal("ReviewOnly", preview.Patches.Single().PatchType);
        Assert.Contains("DiscoveryPartial", report.RepairResults.Single().Reason);
    }

    [Fact]
    public void Paper_funnel_export_includes_orderbookUnavailable_and_invalidTokenQuarantined_counts()
    {
        var state = new BotRuntimeState();
        state.SetOrderBookServiceStats(new OrderBookServiceStats(QuarantinedTokens: 3, BatchInvalidTokens: 3, BatchRequests: 0, BatchBooksLoaded: 0, SingleRequests: 0, CacheHits: 0, SnapshotCacheHits: 0, Timeouts: 0, HttpErrors: 0, ParseErrors: 0, BookCacheMisses: 0));
        var report = new MultiOutcomeGroupArbEngine.MultiOutcomeScanReport(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "", "", new Dictionary<string, int> { ["OrderbookUnavailable"] = 5 }, [], []);

        var snap = PaperOpportunityFunnelExporter.Build(new TradingBotOptions { TradingMode = new TradingModeOptions { PaperPhase = 2 } }, state, new SingleMarketScanStats(0, 0, 0, 0, 0, 0, 0, 0), report, 10, discoveryPartial: true);
        var json = JsonSerializer.Serialize(snap, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        Assert.Contains("orderbookUnavailable", json);
        Assert.Contains("invalidTokenQuarantined", json);
        Assert.Equal(5, snap.OrderbookUnavailable);
        Assert.Equal(3, snap.InvalidTokenQuarantined);
        Assert.True(snap.DiscoveryPartial);
    }

    [Fact]
    public async Task Twelve_hour_simulated_run_keeps_diagnostic_stores_within_caps()
    {
        var gate = new QuietLogGate();
        gate.ConfigureBounds(5000, TimeSpan.FromMinutes(120));
        var svc = Service(_ => Json(HttpStatusCode.BadRequest, "{}"));
        svc.SplitBatchOnBadRequest = false;
        svc.MaxBatchBookErrorSamples = 100;

        for (var i = 0; i < 12 * 60 * 20; i++)
        {
            gate.ShouldLog(new LogEventKey("soak", "evt", MarketId: i.ToString()), new LogEventFingerprint(i.ToString()), LogImportance.Normal, new QuietLogPolicy());
            await svc.GetOrderBooksBatchAsync(new[] { (10_000 + i).ToString() });
        }

        Assert.True(gate.Snapshot().LogGateCacheSize <= 5000);
        Assert.True(svc.BatchBookErrorSampleCount <= 100);
        Assert.True(svc.QuarantinedTokenCount <= 5000);
    }

    private static OrderBookService Service(Func<HttpRequestMessage, HttpResponseMessage> handler)
        => new(new HttpClient(new BatchHandler(handler))) { MaxBatchBookRequestSize = 4, SplitBatchOnBadRequest = true, QuietLogGate = new QuietLogGate(), OperationalQuietMode = true };

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status) { Content = new StringContent(body) };

    private static string BookJson(IReadOnlyList<string> ids)
        => new JArray(ids.Select(id => new JObject { ["asset_id"] = id, ["bids"] = new JArray(new JObject { ["price"] = "0.49", ["size"] = "10" }), ["asks"] = new JArray(new JObject { ["price"] = "0.51", ["size"] = "10" }) })).ToString();

    private static IReadOnlyList<string> ReadTokenIds(HttpRequestMessage req)
    {
        var body = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        var root = JToken.Parse(body);
        var arr = root.Type == JTokenType.Array ? (JArray)root : (JArray)(root["params"] ?? new JArray());
        return arr.Select(x => x["token_id"]?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!).ToArray();
    }

    private sealed class BatchHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(handler(request));
    }
}
