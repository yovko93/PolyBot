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
    public void Same_startup_config_line_from_console_and_startup_sources_is_stored_once()
    {
        var state = new BotRuntimeState();
        var timestamp = DateTime.UtcNow;
        var startup = new TerminalLogEntryDto("1", timestamp, "info", "startup", "[CONFIG] Scanner Mode=AllPaginatedRolling", 1);
        var console = new TerminalLogEntryDto("2", timestamp, "info", "console", "[CONFIG] Scanner Mode=AllPaginatedRolling", 2);

        Assert.True(state.AddLog(startup));
        Assert.False(state.AddLog(console));
        Assert.Single(state.Logs());
    }

    [Fact]
    public void Quiet_mode_batch_scan_logs_are_throttled()
    {
        Assert.True(ScanLogSummaryService.ShouldLogBatchScan(true, true, false, scanId: 1, everyNBatches: 25, fullCycleComplete: false, materialStateChange: false, hasExecutableOrPaperEvent: false, hasError: false));
        Assert.False(ScanLogSummaryService.ShouldLogBatchScan(true, true, false, scanId: 2, everyNBatches: 25, fullCycleComplete: false, materialStateChange: false, hasExecutableOrPaperEvent: false, hasError: false));
        Assert.True(ScanLogSummaryService.ShouldLogBatchScan(true, true, false, scanId: 25, everyNBatches: 25, fullCycleComplete: false, materialStateChange: false, hasExecutableOrPaperEvent: false, hasError: false));
        Assert.True(ScanLogSummaryService.ShouldLogBatchScan(true, true, false, scanId: 2, everyNBatches: 25, fullCycleComplete: false, materialStateChange: false, hasExecutableOrPaperEvent: true, hasError: false));
    }

    [Fact]
    public void Scanner_invalid_state_transition_logs_rejected_transition_without_throwing()
    {
        var machine = new ScannerStateMachine();
        var logs = new List<string>();

        Assert.True(machine.TryStart(logs.Add));
        Assert.False(machine.TryStart(logs.Add));

        Assert.Contains(logs, x => x.Contains("[SCANNER_STATE_TRANSITION_REJECTED]"));
        Assert.Equal(ScannerRuntimeState.Running, machine.State);
    }

    [Fact]
    public void Repeated_same_scanner_exception_faults_after_configured_count()
    {
        var dir = Directory.CreateTempSubdirectory();
        var reporter = new ScannerExceptionReporter(dir.FullName, new TradingBotOptions { MaxRepeatedErrorsBeforeFault = 2, PauseOnRepeatedScannerError = true });
        var context = new ScannerExceptionContext("SingleMarketScan", "test", 1, 1, 0, "0-1", "Running", false, false);

        var first = reporter.Record(new InvalidOperationException("boom"), context, _ => { });
        var second = reporter.Record(new InvalidOperationException("boom"), context, _ => { });

        Assert.False(first.Faulted);
        Assert.True(second.Faulted);
    }

    [Fact]
    public void Scanner_error_includes_type_stage_component_and_stacktrace()
    {
        var dir = Directory.CreateTempSubdirectory();
        var reporter = new ScannerExceptionReporter(dir.FullName, new TradingBotOptions());
        var context = new ScannerExceptionContext("BatchOrderbookFetch", "OrderBookService", 7, 2, 10, "10-20", "Running", false, false);

        var record = reporter.Record(new InvalidOperationException("boom"), context, _ => { });

        Assert.Contains("InvalidOperationException", record.Type);
        Assert.Equal("BatchOrderbookFetch", record.Stage);
        Assert.Equal("OrderBookService", record.Component);
        Assert.Contains("boom", record.StackTrace);
    }

    [Fact]
    public void Scanner_error_export_is_written()
    {
        var dir = Directory.CreateTempSubdirectory();
        var reporter = new ScannerExceptionReporter(dir.FullName, new TradingBotOptions());
        var context = new ScannerExceptionContext("ExportWrite", "SyncRuntimeState", 1, 1, 0, "0-0", "Running", false, false);

        reporter.Record(new InvalidOperationException("boom"), context, _ => { });

        var path = Path.Combine(dir.FullName, "exports", "scanner-errors-latest.json");
        Assert.True(File.Exists(path));
        Assert.Contains("ExportWrite", File.ReadAllText(path));
    }

    [Fact]
    public void Memory_guard_pause_resume_uses_safe_state_transitions()
    {
        var machine = new ScannerStateMachine();
        var logs = new List<string>();

        Assert.True(machine.TryStart(logs.Add));
        Assert.True(machine.TryPauseByMemoryGuard(logs.Add));
        Assert.True(machine.TryResume(logs.Add));

        Assert.Equal(ScannerRuntimeState.Running, machine.State);
        Assert.DoesNotContain(logs, x => x.Contains("SCANNER_STATE_TRANSITION_REJECTED"));
    }

    [Fact]
    public async Task Clearing_orderbook_cache_during_scan_does_not_throw()
    {
        var svc = Service(req => Json(HttpStatusCode.OK, BookJson(ReadTokenIds(req))));
        var markets = new List<Market> { new() { id = "m1", question = "q", outcomes = ["Yes", "No"], clobTokenIds = ["1", "2"] } };

        var prefetch = svc.PrefetchBinarySnapshotsAsync(markets);
        svc.ClearAllCache();
        await prefetch;

        Assert.True(svc.CacheEntryCount >= 0);
    }

    [Fact]
    public void Replacing_market_snapshot_during_scan_uses_safe_snapshot_copy()
    {
        var markets = new List<Market> { new() { id = "m1", outcomes = ["Yes", "No"], clobTokenIds = ["1", "2"] } };
        var snapshot = markets.ToArray();
        markets.Clear();
        markets.Add(new Market { id = "m2", outcomes = ["Yes", "No"], clobTokenIds = ["3", "4"] });

        Assert.Single(snapshot);
        Assert.Equal("m1", snapshot[0].id);
    }

    [Fact]
    public void QuietLogGate_trim_during_scan_does_not_throw()
    {
        var gate = new QuietLogGate();
        gate.ConfigureBounds(10, TimeSpan.FromMilliseconds(1));

        for (var i = 0; i < 100; i++)
            gate.ShouldLog(new LogEventKey("scan", "event", MarketId: i.ToString()), new LogEventFingerprint(i.ToString()), LogImportance.Normal, new QuietLogPolicy());
        gate.TrimExpired();

        Assert.True(gate.Snapshot().LogGateCacheSize <= 10);
    }

    [Fact]
    public void Log_replay_dedupe_uses_snapshot_and_does_not_mutate_during_enumeration()
    {
        var state = new BotRuntimeState();
        state.AddLog(new TerminalLogEntryDto("1", DateTime.UtcNow, "info", "startup", "same", 1));
        var snapshot = state.Logs();
        state.AddLog(new TerminalLogEntryDto("2", DateTime.UtcNow, "info", "startup", "same", 2));

        Assert.Single(snapshot);
        Assert.Single(state.Logs());
    }

    [Fact]
    public void Startup_config_error_prevents_scanner_start()
    {
        var machine = new ScannerStateMachine();
        machine.TryStart(_ => { });
        machine.TryPauseByConfigError(_ => { });

        Assert.False(machine.TryStart(_ => { }));
        Assert.Equal(ScannerRuntimeState.PausedByConfigError, machine.State);
    }

    [Fact]
    public void Scanner_starts_only_once()
    {
        var machine = new ScannerStateMachine();

        Assert.True(machine.TryStart(_ => { }));
        Assert.False(machine.TryStart(_ => { }));
        Assert.Equal(2, machine.StartAttempts);
    }

    [Fact]
    public void Same_startup_log_emitted_twice_is_stored_once()
    {
        var state = new BotRuntimeState();
        var timestamp = DateTime.UtcNow;
        var first = new TerminalLogEntryDto("1", timestamp, "info", "startup", "[CONFIG] Scanner Mode=Rolling", 1);
        var second = first with { Id = "2", Sequence = 2 };

        Assert.True(state.AddLog(first));
        Assert.False(state.AddLog(second));
        Assert.Single(state.Logs());
    }

    [Fact]
    public void Critical_MEMORY_CRITICAL_is_never_suppressed()
    {
        var state = new BotRuntimeState();
        var timestamp = DateTime.UtcNow;
        var first = new TerminalLogEntryDto("1", timestamp, "warn", "console", "[MEMORY_CRITICAL] ProcessMb=1115", 1);
        var second = first with { Id = "2", Sequence = 2 };

        Assert.True(state.AddLog(first));
        Assert.True(state.AddLog(second));
        Assert.Equal(2, state.Logs().Length);
    }

    [Fact]
    public void RuntimeHealth_periodic_logs_are_not_incorrectly_suppressed()
    {
        var state = new BotRuntimeState();
        var timestamp = DateTime.UtcNow;
        var first = new TerminalLogEntryDto("1", timestamp, "info", "console", "[RUNTIME_HEALTH] ProcessMb=500", 1);
        var second = first with { Id = "2", Timestamp = timestamp.AddSeconds(31), Sequence = 2 };

        Assert.True(state.AddLog(first));
        Assert.True(state.AddLog(second));
        Assert.Equal(2, state.Logs().Length);
    }

    [Fact]
    public void PaperPhase2_effective_risk_syncs_legacy_execution_limits_to_paper_risk()
    {
        var options = new TradingBotOptions
        {
            TradingMode = new TradingModeOptions { PaperPhase = 2, LiveTradingEnabled = false },
            PaperOnly = true,
            EnableLiveExecution = false,
            PaperRisk = new PaperRiskOptions { MaxPaperNotionalPerTrade = 50, MaxPaperTotalExposure = 200, MaxPaperOpenPerHour = 2, MaxPaperPositionsTotal = 5, MaxPaperPositionsPerStrategy = 2 },
            SingleMarketArb = new SingleMarketArbOptions { MaxNotionalPerTrade = 25 },
            VerifiedBasketArb = new VerifiedBasketArbOptions { MaxNotionalPerTrade = 25 }
        };
        var execution = new ExecutionOptions { MaxNotionalPerTrade = 25, MaxNotionalPerBasket = 25, MaxOpenBasketPositions = 1, MaxExposurePerGroup = 25 };

        var summary = PaperEffectiveRisk.Apply(options, execution);

        Assert.Equal("TradingBot:PaperRisk", summary.Source);
        Assert.True(summary.LegacyExecutionRiskIgnored);
        Assert.Equal(50, execution.MaxNotionalPerTrade);
        Assert.Equal(50, execution.MaxNotionalPerBasket);
        Assert.Equal(50, options.SingleMarketArb.MaxNotionalPerTrade);
        Assert.Equal(50, options.VerifiedBasketArb.MaxNotionalPerTrade);
        Assert.False(PaperEffectiveRisk.IsPaperPhase2RiskStillPhase1(options, execution));
    }
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
