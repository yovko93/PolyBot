using System.Text;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using TradingBot.Services.CrossExchange;
using TradingBot.Services.Kalshi;
using TradingBot.Models.Normalized;
using TradingBot.Services.MultiOutcome;

var originalOut = Console.Out;
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(o => o.AddPolicy("ui", p => p.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddSignalR();
builder.Services.AddHealthChecks();
builder.Services.AddOptions<TradingBotOptions>()
    .Bind(builder.Configuration.GetSection(TradingBotOptions.SectionName))
    .Bind(builder.Configuration.GetSection(TradingBotOptions.LegacyScannerSectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddOptions<CrossExchangeOptions>().Bind(builder.Configuration.GetSection(CrossExchangeOptions.SectionName)).ValidateDataAnnotations();
builder.Services.AddOptions<ExchangeFeesOptions>().Bind(builder.Configuration.GetSection(ExchangeFeesOptions.SectionName)).ValidateDataAnnotations();
builder.Services.AddOptions<KalshiOptions>().Bind(builder.Configuration.GetSection(KalshiOptions.SectionName)).ValidateDataAnnotations();
builder.Services.AddOptions<OpportunityFilteringOptions>().Bind(builder.Configuration.GetSection(OpportunityFilteringOptions.SectionName));
builder.Services.AddOptions<ExecutionOptions>().Bind(builder.Configuration.GetSection(ExecutionOptions.SectionName)).ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddSingleton<IRiskManager, RiskManager>();
builder.Services.AddSingleton<DuplicateExecutionGuard>();
builder.Services.AddSingleton<PreTradeValidator>();
builder.Services.AddSingleton<OrderPlanBuilder>();
builder.Services.AddSingleton<ExecutionAuditLog>();
builder.Services.AddSingleton<VerifiedBasketExecutionCoordinator>();
builder.Services.AddSingleton<VerifiedBasketDryRunOrderBuilder>();
builder.Services.AddSingleton<DryRunFillSimulator>();
builder.Services.AddSingleton<AllowlistRepairLockProvider>();
builder.Services.AddSingleton<AllowlistRepairService>();
builder.Services.AddSingleton<MemoryGuard>();
builder.Services.AddSingleton<QuietLogGate>();
builder.Services.AddSingleton<IExchangeOrderExecutor, DisabledExchangeOrderExecutor>();
builder.Services.AddSingleton<DryRunLiveExecutor>();
builder.Services.AddSingleton(sp => new BotRuntimeState(sp.GetRequiredService<IOptions<TradingBotOptions>>().Value.RuntimeState));
builder.Services.AddSingleton<TextWriter>(originalOut);
builder.Services.AddSingleton<IBotUiLogger, BotUiLogger>();

var app = builder.Build();
app.UseCors("ui");
var options = app.Services.GetRequiredService<IOptions<TradingBotOptions>>().Value;
var startupDiscoveryMode = ResolveEffectiveDiscoveryMode(options);
var sourceAuditOnlySources = ResolveConfigSource(builder.Configuration, "TradingBot:Discovery:SourceAuditOnly", "TradingBot:MarketDiscovery:SourceAuditOnly", "Scanner:Discovery:SourceAuditOnly", "Scanner:MarketDiscovery:SourceAuditOnly");
var reducedUniverseSources = ResolveConfigSource(builder.Configuration, "TradingBot:Discovery:AllowReducedUniverseDiagnosticsOnly", "TradingBot:MarketDiscovery:AllowReducedUniverseDiagnosticsOnly", "Scanner:Discovery:AllowReducedUniverseDiagnosticsOnly", "Scanner:MarketDiscovery:AllowReducedUniverseDiagnosticsOnly");
if (options.MarketDiscovery.SourceAuditOnly && options.MarketDiscovery.AllowReducedUniverseDiagnosticsOnly)
    Console.WriteLine("[DISCOVERY_MODE_CONFLICT] SourceAuditOnly=true AllowReducedUniverseDiagnosticsOnly=true EffectiveMode=SourceAuditOnly Action=DisableSourceAuditOnlyForReducedUniverseRun");
Console.WriteLine($"[DISCOVERY_EFFECTIVE_MODE] SourceAuditOnly={options.MarketDiscovery.SourceAuditOnly.ToString().ToLowerInvariant()} AllowReducedUniverseDiagnosticsOnly={options.MarketDiscovery.AllowReducedUniverseDiagnosticsOnly.ToString().ToLowerInvariant()} EffectiveMode={startupDiscoveryMode} ConfigSource_SourceAuditOnly={sourceAuditOnlySources} ConfigSource_AllowReducedUniverseDiagnosticsOnly={reducedUniverseSources} ReducedUniverseMaxMarkets={options.MarketDiscovery.ReducedUniverseMaxMarkets} PaperBlocked={options.MarketDiscovery.ReducedUniverseBlockPaper.ToString().ToLowerInvariant()} ScannerEnabled={(!options.MarketDiscovery.SourceAuditOnly).ToString().ToLowerInvariant()} OrderbooksEnabled={(!options.MarketDiscovery.SourceAuditOnly).ToString().ToLowerInvariant()}");
PaperPhaseValidationHarness.LogStartupConfig(options, app.Environment.EnvironmentName, app.Environment.ContentRootPath, builder.Configuration.Sources, args);
PaperPhaseValidationHarness.LogPaperModeStartup(options, app.Environment.EnvironmentName, builder.Configuration.Sources, args);
var listenUrl = options.ListenUrl;

app.MapHealthChecks("/health");
app.MapGet("/api/bot/health", () => Results.Ok(new { ok = true, service = "PolyBot", timestamp = DateTime.UtcNow }));
app.MapGet("/api/bot/status", (BotRuntimeState s) => s.Status);
app.MapGet("/api/bot/opportunities", (BotRuntimeState s, IOptions<OpportunityFilteringOptions> f, bool? includeNegativeEdge, bool? debug, int? limit) =>
{
    var cappedLimit = Math.Clamp(limit ?? 100, 1, 500);
    var include = (includeNegativeEdge ?? false) || (debug ?? false) || f.Value.EnableDebugNegativeEdgeView;
    return s.Opportunities().Where(o => include || OpportunityVisibilityFilter.IsVisibleOpportunity(o, f.Value)).TakeLast(cappedLimit).ToArray();
});
app.MapGet("/api/bot/positions", (BotRuntimeState s) => s.Positions().Select(p=> new { id=p.Id, groupKey=p.Group, strategy=p.Strategy, status=p.Status, openedAt=p.OpenedAt, qty=p.Qty, legs=p.Legs, totalCost=p.Cost, costPerBasket=p.CostPerBasket, guaranteedPayout=p.GuaranteedPayout, maxPayout=p.MaxPayout, grossEdgeAtOpen=p.GrossEdgeAtOpen, netEdgeAtOpen=p.NetEdgeAtOpen, expectedProfitAtOpen=p.ExpectedProfit, lockedCapital=p.LockedCapital, mtmStatus=p.MtmStatus, unrealizedPnl=p.MtmStatus == "Incomplete" ? (decimal?)null : p.UnrealizedPnl, activeProfile=p.ActiveProfile, source=p.Source, openedFromSimulatedFills=p.OpenedFromSimulatedFills, fillSimulationId=p.FillSimulationId }));
app.MapGet("/api/bot/paper-account", (BotRuntimeState s) => Results.Ok(new
{
    lastUpdatedAt = s.Status.LastScanTime,
    cash = s.Status.Cash,
    lockedCapital = s.Status.LockedCapital,
    equity = s.Status.Equity,
    realizedPnl = s.Status.RealizedPnl,
    unrealizedPnl = s.Positions().Where(p => p.MtmStatus != "Incomplete").Sum(p => p.UnrealizedPnl),
    initialCash = 1000m,
    openExposure = s.Status.LockedCapital,
    openPositionsCount = s.Status.OpenPositions,
    openBasketPositionsCount = s.Status.OpenPositions
}));
app.MapGet("/api/bot/paper/account", (BotRuntimeState s) => Results.Ok(new
{
    cash = s.Status.Cash,
    locked = s.Status.LockedCapital,
    equity = s.Status.Equity,
    realizedPnl = s.Status.RealizedPnl,
    openPositions = s.Status.OpenPositions,
    closedPositions = s.PaperClosedPositions,
    totalExposure = s.Status.LockedCapital,
    positionsByStrategy = s.Positions().GroupBy(p => p.Strategy ?? "unknown", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
    hourlyOpenCount = s.PaperOpenCountLastHour,
    lastOpenAt = s.Positions().Select(p => p.OpenedAt).DefaultIfEmpty().Max(),
    blockedCountsByReason = s.PaperPretradeRejectsByReason,
    settlements = s.PaperSettlements,
    settlementRejects = s.PaperSettlementRejects,
    duplicateSettlementSuppressions = s.PaperDuplicateSettlementSuppressions,
    noLiveOrdersSubmitted = true
}));
app.MapGet("/api/bot/paper/positions", (BotRuntimeState s) => s.Positions());
app.MapGet("/api/bot/paper/executions", (BotRuntimeState s) => s.SingleMarketExecutions().Cast<object>().Concat(s.Trades().Where(t => string.Equals(t.Status, "PAPER_EXECUTED", StringComparison.OrdinalIgnoreCase)).Cast<object>()).TakeLast(300).ToArray());
app.MapGet("/api/bot/paper/settlements", (BotRuntimeState s) => s.PaperSettlementsRecords().TakeLast(300).ToArray());
app.MapGet("/api/bot/verified-allowlist-health", (IHostEnvironment env) =>
{
    var path = Path.Combine(env.ContentRootPath, "exports/verified-allowlist-health-latest.json");
    if (!File.Exists(path)) return Results.Ok(Array.Empty<object>());
    return Results.Text(File.ReadAllText(path), "application/json");
});
app.MapGet("/api/bot/verified-unresolved-groups", (IHostEnvironment env) =>
{
    var path = Path.Combine(env.ContentRootPath, "exports/verified-unresolved-groups-latest.json");
    if (!File.Exists(path)) return Results.Ok(new { total = 0, groups = Array.Empty<object>() });
    return Results.Text(File.ReadAllText(path), "application/json");
});
app.MapGet("/api/bot/trade-log", (BotRuntimeState s, int? limit) => s.Trades().TakeLast(Math.Clamp(limit ?? 300, 1, 1000)).ToArray());
app.MapGet("/api/bot/scanner-stats", (BotRuntimeState s) => s.ScannerStats);
app.MapGet("/api/bot/opportunity-diagnostics", (BotRuntimeState s, int? nearMissLimit) =>
{
    var d = s.OpportunityDiagnostics;
    if (d is null) return Results.Ok(null);
    var capped = Math.Clamp(nearMissLimit ?? 25, 1, 100);
    return Results.Ok(d with { NearMissTopN = d.NearMissTopN.Take(capped).ToArray() });
});
app.MapGet("/api/bot/multi-outcome-diagnostics", (BotRuntimeState s) => Results.Ok(s.MultiOutcomeDiagnostics));
app.MapGet("/api/bot/verified-basket-screener", (BotRuntimeState s) => Results.Ok(s.VerifiedBasketScreener));
app.MapGet("/api/bot/verified-basket-opportunity-screener", (BotRuntimeState s) => Results.Ok(s.VerifiedBasketScreener));
app.MapGet("/api/bot/multi-outcome-candidates", (BotRuntimeState s, int? limit, bool? includeMarkets) =>
{
    var capped = Math.Clamp(limit ?? 25, 1, 200);
    var include = includeMarkets ?? true;
    var src = s.MultiOutcomeCandidates.Take(capped).ToArray();
    if (include) return Results.Ok(src);
    var stripped = src.Select(x =>
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(x)!.AsObject();
        node["markets"] = new System.Text.Json.Nodes.JsonArray();
        return node;
    }).ToArray();
    return Results.Ok(stripped);
});
app.MapGet("/api/bot/multi-outcome-review-report", (BotRuntimeState s, int? limit, int? minScore, bool? includeDoNotVerify) =>
{
    var capped = Math.Clamp(limit ?? 20, 1, 200);
    var score = minScore ?? 0;
    var includeRejected = includeDoNotVerify ?? false;
    var filtered = s.MultiOutcomeReviewReport.Where(x =>
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(x)!.AsObject();
        var candidateScore = node["candidateQualityScore"]?.GetValue<int>() ?? int.MinValue;
        var action = node["recommendedAction"]?.GetValue<string>() ?? string.Empty;
        if (candidateScore < score) return false;
        if (!includeRejected && (action == "DoNotVerify" || action == "LikelyFalsePositive")) return false;
        return true;
    }).Take(capped).ToArray();
    return Results.Ok(filtered);
});
app.MapGet("/api/bot/verified-group-triage", (IHostEnvironment env, int? limit) =>
{
    var path = Path.Combine(env.ContentRootPath, "exports/verified-group-triage-latest.json");
    if (!File.Exists(path)) return Results.Ok(Array.Empty<object>());
    var arr = System.Text.Json.JsonSerializer.Deserialize<object[]>(File.ReadAllText(path)) ?? Array.Empty<object>();
    return Results.Ok(arr.Take(Math.Clamp(limit ?? 25, 1, 200)).ToArray());
});
app.MapGet("/api/bot/next-groups-to-verify", (IHostEnvironment env, int? limit) =>
{
    var path = Path.Combine(env.ContentRootPath, "exports/next-groups-to-verify-latest.json");
    if (!File.Exists(path)) return Results.Ok(Array.Empty<object>());
    var arr = System.Text.Json.JsonSerializer.Deserialize<object[]>(File.ReadAllText(path)) ?? Array.Empty<object>();
    return Results.Ok(arr.Take(Math.Clamp(limit ?? 10, 1, 100)).ToArray());
});
app.MapGet("/api/bot/verified-allowlist-suggestion", (IHostEnvironment env) =>
{
    var path = Path.Combine(env.ContentRootPath, "exports/verified-multi-outcome-groups-suggested.json");
    if (!File.Exists(path)) return Results.Ok(new { items = Array.Empty<object>() });
    return Results.Text(File.ReadAllText(path), "application/json");
});
app.MapGet("/api/bot/verified-allowlist-repair-report", (IHostEnvironment env, int? limit) =>
{
    var path = Path.Combine(env.ContentRootPath, "exports/verified-allowlist-repair-report-latest.json");
    if (!File.Exists(path)) return Results.Ok(new { groups = Array.Empty<object>() });
    var node = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(File.ReadAllText(path));
    var capped = Math.Clamp(limit ?? 50, 1, 500);
    if (node?["groups"] is System.Text.Json.Nodes.JsonArray groups)
        node["groups"] = new System.Text.Json.Nodes.JsonArray(groups.Take(capped).Select(x => System.Text.Json.Nodes.JsonNode.Parse(x!.ToJsonString())).ToArray());
    if (node?["repairSuggestions"] is System.Text.Json.Nodes.JsonArray suggestions)
        node["repairSuggestions"] = new System.Text.Json.Nodes.JsonArray(suggestions.Take(capped).Select(x => System.Text.Json.Nodes.JsonNode.Parse(x!.ToJsonString())).ToArray());
    if (node?["snapshot"] is System.Text.Json.Nodes.JsonObject snapshot && snapshot["repairResults"] is System.Text.Json.Nodes.JsonArray repairResults)
        snapshot["repairResults"] = new System.Text.Json.Nodes.JsonArray(repairResults.Take(capped).Select(x => System.Text.Json.Nodes.JsonNode.Parse(x!.ToJsonString())).ToArray());
    return Results.Json(node);
});
app.MapGet("/api/bot/verified-allowlist-suggested-config", (IHostEnvironment env, int? limit) =>
{
    var path = Path.Combine(env.ContentRootPath, "exports/verified-multi-outcome-groups-repair-suggested.json");
    if (!File.Exists(path)) return Results.Ok(new { groups = Array.Empty<object>() });
    var node = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(File.ReadAllText(path));
    if (node?["groups"] is System.Text.Json.Nodes.JsonArray groups)
    {
        var capped = Math.Clamp(limit ?? 50, 1, 500);
        node["groups"] = new System.Text.Json.Nodes.JsonArray(groups.Take(capped).Select(x => System.Text.Json.Nodes.JsonNode.Parse(x!.ToJsonString())).ToArray());
    }
    return Results.Json(node);
});
app.MapGet("/api/bot/allowlist-repair-patch-preview", (IHostEnvironment env) =>
{
    var path = Path.Combine(env.ContentRootPath, "exports/verified-allowlist-repair-patch-preview-latest.json");
    if (!File.Exists(path)) return Results.Ok(new { mode = "ManualPreviewOnly", willOverwriteRealConfig = false, patches = Array.Empty<object>() });
    return Results.Text(File.ReadAllText(path), "application/json");
});
app.MapGet("/api/bot/risk", (BotRuntimeState s, IRiskManager risk) => Results.Ok(new { runtime = s.Risk, executionRisk = risk.GetRiskSnapshot() }));
app.MapGet("/api/bot/execution-audit", (VerifiedBasketExecutionCoordinator audit, int? limit) => audit.ListAudit(Math.Clamp(limit ?? 200, 1, 1000)));
app.MapGet("/api/bot/dry-run-order-plans", (VerifiedBasketExecutionCoordinator audit, int? limit) =>
    Results.Ok(audit.ListDryRunPlanSummaries(Math.Clamp(limit ?? 50, 1, 500))));
app.MapGet("/api/bot/dry-run-fill-simulations", (VerifiedBasketExecutionCoordinator audit, int? limit) => Results.Ok(audit.ListFillSimulations(Math.Clamp(limit ?? 50, 1, 500))));
app.MapGet("/api/bot/execution-plans", (BotRuntimeState s, int? limit) => s.Trades().TakeLast(Math.Clamp(limit ?? 100, 1, 500)).ToArray());
app.MapGet("/api/bot/single-market-arbs", (BotRuntimeState s, int? limit) => s.SingleMarketSnapshot);
app.MapGet("/api/bot/single-market-paper-executions", (BotRuntimeState s, int? limit) => s.SingleMarketExecutions().TakeLast(Math.Clamp(limit ?? 100, 1, 500)).ToArray());
app.MapGet("/api/bot/controls", (BotRuntimeState s) => s.Controls);
app.MapPost("/api/bot/controls/pause", async (BotRuntimeState s, IHubContext<BotHub> hub) =>
{
    s.SetControls(new BotControlStateDto(true, "MANUAL_PAUSE", DateTime.UtcNow, s.NextSeq()));
    s.SetStatus(s.Status with { ScannerActive = false, LastScanTime = DateTime.UtcNow });
    s.AddSignalREvent("controlsUpdated");
    await hub.Clients.All.SendAsync("controlsUpdated", s.Controls);
    s.AddSignalREvent("botStatusUpdated");
    await hub.Clients.All.SendAsync("botStatusUpdated", s.Status);
    return Results.Ok(s.Controls);
});
app.MapPost("/api/bot/controls/kill-switch/enable", (IRiskManager risk) => { risk.SetKillSwitch(true); return Results.Ok(new { killSwitchEnabled = true }); });
app.MapPost("/api/bot/controls/kill-switch/disable", (IRiskManager risk) => { risk.SetKillSwitch(false); return Results.Ok(new { killSwitchEnabled = false }); });
app.MapPost("/api/bot/controls/resume", async (BotRuntimeState s, IHubContext<BotHub> hub) =>
{
    s.SetControls(new BotControlStateDto(false, "RUNNING", DateTime.UtcNow, s.NextSeq()));
    s.SetStatus(s.Status with { ScannerActive = true, LastScanTime = DateTime.UtcNow });
    s.AddSignalREvent("controlsUpdated");
    await hub.Clients.All.SendAsync("controlsUpdated", s.Controls);
    s.AddSignalREvent("botStatusUpdated");
    await hub.Clients.All.SendAsync("botStatusUpdated", s.Status);
    return Results.Ok(s.Controls);
});
app.MapGet("/api/bot/logs/recent", (BotRuntimeState s, int? limit) => s.Logs().TakeLast(Math.Clamp(limit ?? 300, 1, 1000)).ToArray());
app.MapGet("/api/bot/equity", (BotRuntimeState s, int? limit) => s.Equity().TakeLast(Math.Clamp(limit ?? 500, 1, 1000)).ToArray());
app.MapGet("/api/bot/runtime-health", (BotRuntimeState s, QuietLogGate q, IOptions<TradingBotOptions> o) => { s.SetQuietLogGateStats(q.Snapshot()); if (ProcessRunContext.ValidateOrderbookCounters(s.OrderBookServiceStats) is string mismatchReason) Console.WriteLine(ProcessRunContext.FormatMismatchLog(mismatchReason, s.OrderBookServiceStats)); return Results.Ok(RuntimeHealthSnapshot.From(s, o.Value)); });
app.MapHub<BotHub>("/hubs/bot");

var apiTask = app.RunAsync(listenUrl);
var state = app.Services.GetRequiredService<BotRuntimeState>();
state.SetDiscoveryGuardState(
    discoveryHealthy: false,
    discoveryStable: false,
    usingLastHealthySnapshot: false,
    lastHealthySnapshotAgeSeconds: 0,
    partialAttemptCount: 0,
    lastFailureReason: options.MarketDiscovery.SourceAuditOnly ? "DiscoveryMode=Blocked;DiscoveryBlockedReason=SourceAuditOnly" : "DiscoveryMode=Initializing",
    scannerPausedByDiscoveryGuard: options.MarketDiscovery.SourceAuditOnly,
    discoveryGuardSkippedCycles: 0,
    discoveryGuardUsingLastHealthySnapshot: false,
    discoveryGuardBlockedNewMarkets: 0,
    longRunStable: false,
    longRunBlockingReason: options.MarketDiscovery.SourceAuditOnly ? "SourceAuditOnly" : "DiscoveryInitializing",
    orderbookRecoveredAfterDegradation: true,
    lastDegradationUtc: null,
    lastRecoveryUtc: null,
    allowlistEvaluationSkipped: options.MarketDiscovery.SourceAuditOnly,
    allowlistEvaluationSkippedReason: options.MarketDiscovery.SourceAuditOnly ? "SourceAuditOnly" : string.Empty,
    allowlistClassificationBlockedByDiscovery: options.MarketDiscovery.SourceAuditOnly,
    soakReadiness: "Blocked",
    soakReadinessReason: options.MarketDiscovery.SourceAuditOnly ? "SourceAuditOnly" : "DiscoveryInitializing",
    discoveryBlockedReason: options.MarketDiscovery.SourceAuditOnly ? "SourceAuditOnly" : "DiscoveryInitializing",
    discoverySelectedSource: options.MarketDiscovery.SourceAuditOnly ? "Blocked" : "Unknown",
    discoveryScannerSafeSourceAvailable: false,
    discoverySourceAuditOnly: options.MarketDiscovery.SourceAuditOnly,
    discoverySourceAuditRecommendedAction: options.MarketDiscovery.SourceAuditOnly ? "KeepBlocked" : string.Empty);
var quietLogGate = app.Services.GetRequiredService<QuietLogGate>();
var logger = app.Services.GetRequiredService<IBotUiLogger>();
options = app.Services.GetRequiredService<IOptions<TradingBotOptions>>().Value;
foreach (var strategyEntry in options.Strategies.Where(x => x.Value.Enabled && x.Value.Mode != StrategyMode.Disabled))
    state.RecordStrategyResult(new OpportunityStrategyScanResult(strategyEntry.Key, strategyEntry.Value.Mode));
quietLogGate.ConfigureBounds(options.RuntimeMemory.MaxQuietLogGateEntries, TimeSpan.FromMinutes(options.RuntimeMemory.QuietLogGateTtlMinutes));

state.ClearTransientLogBuffers();
Console.SetOut(new MultiTextWriter(originalOut, msg => logger.LogInfo("console", msg)));
Console.WriteLine($"[LOG_BUFFER_RESET] ProcessRunId={ProcessRunContext.ProcessRunId} Reason=FreshProcessStart");
logger.LogSuccess("startup", $"Bot API listening on {listenUrl}");
logger.LogSuccess("startup", $"ExecutionMode={options.ExecutionMode}; EnablePaperTrading={options.EnablePaperTrading}; EnableLiveExecution={options.EnableLiveExecution}");
logger.LogInfo("startup", $"[CONFIG] Scanner Mode={options.Mode} MarketScanLimit={options.MarketScanLimit} MaxMarketsToDiscover={options.MaxMarketsToDiscover} ScanBatchSize={options.ScanBatchSize} MaxOrderbooksPerCycle={options.MaxOrderbooksPerCycle} MaxConcurrentOrderbookRequests={options.MaxConcurrentOrderbookRequests} LogEmptyOpportunityCycles={options.LogEmptyOpportunityCycles}");
logger.LogInfo("startup", $"[DIAGNOSTICS] DebuggerSafeMode={options.Diagnostics.DebuggerSafeMode} DetailedLogs={(!options.Diagnostics.DebuggerSafeMode).ToString().ToLowerInvariant()} MaxRecentLogs={options.RuntimeState.MaxRecentLogs}");
logger.LogInfo("startup", $"[DIAGNOSTICS] OperationalQuietMode={options.Diagnostics.OperationalQuietMode.ToString().ToLowerInvariant()}");
Console.WriteLine($"[DIAGNOSTICS] OperationalQuietMode={options.Diagnostics.OperationalQuietMode.ToString().ToLowerInvariant()}");
Console.WriteLine($"[SOAK_READINESS] OperationalQuietMode={options.Diagnostics.OperationalQuietMode.ToString().ToLowerInvariant()} RuntimeHealth={options.RuntimeHealth.Enabled.ToString().ToLowerInvariant()} MemoryGuard=true BoundedCollections=true SignalRPayloadLimits={(options.SignalR.MaxPayloadItems > 0 && options.SignalR.MaxPayloadBytes > 0).ToString().ToLowerInvariant()} PaperOnly={options.PaperOnly.ToString().ToLowerInvariant()} LiveTrading={options.TradingMode.LiveTradingEnabled.ToString().ToLowerInvariant()} PaperTradingEnabled={options.TradingMode.PaperTradingEnabled.ToString().ToLowerInvariant()} PaperPhase={options.TradingMode.PaperPhase}");
logger.LogInfo("startup", $"[CONFIG] MultiOutcome FeePerLeg={options.MultiOutcomeArbitrage.FeePerLeg} SlippagePerLeg={options.MultiOutcomeArbitrage.SlippageBufferPerLeg} SafetyPerGroup={options.MultiOutcomeArbitrage.SafetyBufferPerGroup} MinNetEdgePerBasket={options.MultiOutcomeArbitrage.MinMultiOutcomeEdge} MinExpectedProfit={options.MultiOutcomeArbitrage.MinExpectedProfit} EnableSensitivityDiagnostics={options.MultiOutcomeArbitrage.EnableSensitivityDiagnostics}");
var executionCfg = app.Services.GetRequiredService<IOptions<ExecutionOptions>>().Value;
var effectivePaperRisk = PaperEffectiveRisk.Apply(options, executionCfg);
logger.LogInfo("startup", $"[PAPER_EFFECTIVE_RISK] PaperPhase={effectivePaperRisk.PaperPhase} Source={effectivePaperRisk.Source} MaxPaperNotionalPerTrade={effectivePaperRisk.MaxPaperNotionalPerTrade:0.####} MaxPaperTotalExposure={effectivePaperRisk.MaxPaperTotalExposure:0.####} MaxPaperOpenPerHour={effectivePaperRisk.MaxPaperOpenPerHour} MaxPaperPositionsTotal={effectivePaperRisk.MaxPaperPositionsTotal} MaxPaperPositionsPerStrategy={effectivePaperRisk.MaxPaperPositionsPerStrategy} SingleMarketMaxNotional={effectivePaperRisk.SingleMarketMaxNotional:0.####} VerifiedBasketMaxNotional={effectivePaperRisk.VerifiedBasketMaxNotional:0.####} LegacyExecutionRiskIgnored={effectivePaperRisk.LegacyExecutionRiskIgnored.ToString().ToLowerInvariant()}");
logger.LogInfo("startup", $"[CONFIG] LegacyExecutionRisk PaperOnly={executionCfg.PaperOnly.ToString().ToLowerInvariant()} MaxNotionalPerBasket={executionCfg.MaxNotionalPerBasket} MaxOpenBasketPositions={executionCfg.MaxOpenBasketPositions} MaxExposurePerGroup={executionCfg.MaxExposurePerGroup} DuplicateCooldownMinutes={executionCfg.DuplicateCooldownMinutes} LegacyExecutionRiskIgnored={effectivePaperRisk.LegacyExecutionRiskIgnored.ToString().ToLowerInvariant()} EffectiveSyncedFromPaperRisk={effectivePaperRisk.SyncedFromPaperRisk.ToString().ToLowerInvariant()}");
logger.LogInfo("startup", $"[CONFIG] ExecutionRiskDeprecated MaxNotionalPerBasket={executionCfg.MaxNotionalPerBasket} MaxNotionalPerTrade={executionCfg.MaxNotionalPerTrade} MinPlannedNotional={executionCfg.MinPlannedNotional} MinPlannedExpectedProfit={executionCfg.MinPlannedExpectedProfit} MinPlannedBasketQty={executionCfg.MinPlannedBasketQty} LegacyExecutionRiskIgnored={effectivePaperRisk.LegacyExecutionRiskIgnored.ToString().ToLowerInvariant()}");
var paperConfigError = PaperEffectiveRisk.IsPaperPhase2RiskStillPhase1(options, executionCfg);
if (paperConfigError)
{
    logger.LogError("startup", "[PAPER_CONFIG_ERROR] Reason=PaperPhase2ButExecutionRiskStillPhase1");
    state.SetControls(state.Controls with { IsPaused = true, Reason = "PAPER_CONFIG_ERROR", UpdatedAtUtc = DateTime.UtcNow, Sequence = state.NextSeq() });
}
var activeProfileName = options.MultiOutcomeArbitrage.CostProfiles.ActiveProfile;
if (!options.MultiOutcomeArbitrage.CostProfiles.Profiles.TryGetValue(activeProfileName, out var activeProfileCfg)) activeProfileCfg = options.MultiOutcomeArbitrage.CostProfiles.Profiles["Conservative"];
if (options.EnableLiveExecution && activeProfileName.Equals("RawOnly", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("RawOnly cost profile cannot be active in live mode.");
var profileNames = string.Join(",", options.MultiOutcomeArbitrage.CostProfiles.Profiles.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
Console.WriteLine($"[COST_PROFILES] Active={activeProfileName} Profiles=[{profileNames}]");
foreach (var p in options.MultiOutcomeArbitrage.CostProfiles.Profiles.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
{
    var diagnosticsOnly = !p.Key.Equals(activeProfileName, StringComparison.OrdinalIgnoreCase);
    Console.WriteLine($"[COST_PROFILE] Name={p.Key} FeeModel={p.Value.FeeModel} FeePerLeg={p.Value.FeePerLeg} SlippagePerLeg={p.Value.SlippageBufferPerLeg} Safety={p.Value.SafetyBufferPerGroup} DiagnosticsOnly={diagnosticsOnly.ToString().ToLowerInvariant()}");
}


_ = Task.Run(async () =>
{
    if (!options.RuntimeHealth.Enabled) return;
    try
    {
        DateTime lastSoakStatusLoggedAt = DateTime.MinValue;
        void LogRuntimeHealthAndSoakStatus()
        {
            state.SetQuietLogGateStats(quietLogGate.Snapshot());
            if (ProcessRunContext.ValidateOrderbookCounters(state.OrderBookServiceStats) is string mismatchReason)
                Console.WriteLine(ProcessRunContext.FormatMismatchLog(mismatchReason, state.OrderBookServiceStats));
            var health = RuntimeHealthSnapshot.From(state, options);
            var trend = RuntimeHealthTrendTracker.RecordAndAnalyze(health, options.RuntimeHealth);
            Console.WriteLine(health.ToLogLine());
            ExportRuntimeSoakStatus(state, options, app.Environment.ContentRootPath);
            lastSoakStatusLoggedAt = DateTime.UtcNow;
            Console.WriteLine(RuntimeHealthTrendTracker.ToSoakStatusLogLine(health, trend, options, state));
        }

        if (options.RuntimeHealth.LogOnStartup)
            LogRuntimeHealthAndSoakStatus();

        var delay = TimeSpan.FromMinutes(Math.Max(1, options.RuntimeHealth.LogEveryMinutes));
        using var timer = new PeriodicTimer(delay);
        while (await timer.WaitForNextTickAsync(app.Lifetime.ApplicationStopping))
            LogRuntimeHealthAndSoakStatus();
    }
    catch (OperationCanceledException) when (app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
    }
});

_ = Task.Run(async () =>
{
    var hub = app.Services.GetRequiredService<IHubContext<BotHub>>();
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        state.SetStatus(state.Status with { LastHeartbeat = DateTime.UtcNow, ConnectionStatus = "CONNECTED" });
        state.AddSignalREvent("heartbeat");
        await hub.Clients.All.SendAsync("heartbeat", new { timestamp = DateTime.UtcNow, sequence = state.NextSeq() });
        await Task.Delay(options.HeartbeatIntervalMs);
    }
});

if (paperConfigError)
{
    var configErrorState = new ScannerStateMachine();
    configErrorState.TryStart(Console.WriteLine);
    configErrorState.TryPauseByConfigError(Console.WriteLine);
    await apiTask;
    return;
}

await RunScannerAsync(state, logger, app.Services.GetRequiredService<IHubContext<BotHub>>(), app.Services.GetRequiredService<VerifiedBasketExecutionCoordinator>(), app.Services.GetRequiredService<VerifiedBasketDryRunOrderBuilder>(), app.Services.GetRequiredService<DryRunFillSimulator>(), app.Services.GetRequiredService<AllowlistRepairService>(), app.Services.GetRequiredService<AllowlistRepairLockProvider>(), app.Services.GetRequiredService<MemoryGuard>(), quietLogGate, app.Services.GetRequiredService<IOptions<ExecutionOptions>>().Value, options, app.Services.GetRequiredService<IOptions<OpportunityFilteringOptions>>().Value, app.Environment.ContentRootPath, app.Lifetime.ApplicationStopping);
await apiTask;

static async Task RunScannerAsync(BotRuntimeState state, IBotUiLogger uiLogger, IHubContext<BotHub> hub, VerifiedBasketExecutionCoordinator verifiedExecution, VerifiedBasketDryRunOrderBuilder dryRunBuilder, DryRunFillSimulator fillSimulator, AllowlistRepairService allowlistRepairService, AllowlistRepairLockProvider lockProvider, MemoryGuard memoryGuard, QuietLogGate quietLogGate, ExecutionOptions executionOptions, TradingBotOptions options, OpportunityFilteringOptions filtering, string contentRootPath, CancellationToken stoppingToken)
{
    var scannerInstanceId = Guid.NewGuid().ToString("N");
    var scannerStartedAt = DateTime.UtcNow;
    Console.WriteLine($"[SCANNER] Background scanner started InstanceId={scannerInstanceId} ScannerInstanceId={ProcessRunContext.ScannerInstanceId}");
    var scannerState = new ScannerStateMachine();
    scannerState.TryStart(Console.WriteLine);
    var scannerErrors = new ScannerExceptionReporter(contentRootPath, options);
    var scannerStage = "Starting";
    var scannerComponent = "Scanner";
    void SetScannerStage(string stage, string component) { scannerStage = stage; scannerComponent = component; }
    var lastScannerSummaryAt = scannerStartedAt;
    var scannerSummaryBatches = 0L;
    var scannerSummaryMarketsScanned = 0L;
    var scannerSummaryDurationMs = 0L;
    var scannerSummaryErrors = 0L;
    var scannerSummaryPositive = 0L;
    var scannerSummaryExecutable = 0L;
    var scannerSummaryPaperOpened = 0L;
    bool ShouldLogScannerChannel(string eventName, string stableHash, LogImportance importance = LogImportance.Normal)
    {
        if (!options.Diagnostics.OperationalQuietMode || options.Logging.LogScannerStartEndInQuietMode) return true;
        return quietLogGate.ShouldLog(
            new LogEventKey("scanner.lifecycle", eventName),
            new LogEventFingerprint(stableHash, stableHash),
            importance,
            new QuietLogPolicy(
                OperationalQuietMode: true,
                EveryNCycles: int.MaxValue,
                EveryMinutes: Math.Max(1, options.Logging.LogScannerSummaryEveryMinutes),
                SuppressRepeatedHash: true,
                MaxSameEventPerHour: 0,
                DebugEnabled: options.Diagnostics.DebuggerSafeMode));
    }
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    http.Timeout = TimeSpan.FromSeconds(options.ExternalApiTimeoutSeconds);

    var marketService = new MarketDataService(http);
    var orderbookService = new OrderBookService(http) { DisableSingleBookHttpFallback = true, LogPrefetchDetails = options.LogPrefetchDetails, LogBookCacheMissDetails = options.Logging.LogBookCacheMissDetails, BookCacheMissSampleSize = options.Logging.BookCacheMissSampleSize, ExportDirectory = Path.Combine(contentRootPath, "exports") };
    orderbookService.StatsUpdated = state.SetOrderBookServiceStats;
    orderbookService.ConfigureCache(TimeSpan.FromMinutes(Math.Max(1, options.RuntimeMemory.OrderbookCacheTtlMinutes)), Math.Min(options.Caches.MaxOrderbookCacheEntries, options.RuntimeMemory.MaxOrderbookCacheEntries));
    orderbookService.ConfigureBatchOptions(options.OrderBook, options.Diagnostics.OperationalQuietMode, options.Logging, quietLogGate);
    orderbookService.MaxInvalidTokenCacheEntries = Math.Max(1, options.RuntimeMemory.MaxInvalidTokenCacheEntries);
    orderbookService.MaxBatchBookErrorSamples = Math.Max(1, options.RuntimeMemory.MaxBatchBookErrorSamples);
    orderbookService.BatchBookErrorSampleTtl = TimeSpan.FromMinutes(Math.Max(1, options.RuntimeMemory.BatchBookErrorSampleTtlMinutes));
    var crossOptions = new CrossExchangeOptions();
    options.GetType();
    var feeOptions = new ExchangeFeesOptions();
    var kalshiOptions = new KalshiOptions();
    var executionPolicy = new ExecutionPolicy
    {
        MaxNotionalPerTrade = options.TradingMode.PaperPhase >= 2 ? options.PaperRisk.MaxPaperNotionalPerTrade : Math.Min(options.MaxNotionalPerTrade, options.PaperRisk.MaxPaperNotionalPerTrade),
        MinNotionalPerTrade = options.MinNotionalPerTrade,
        MinEdgePerShare = options.MinEdgePerShare,
        MinExpectedProfit = options.MinExpectedProfit,
        MaxLockedCapital = Math.Min(options.MaxLockedCapital, options.PaperRisk.MaxPaperTotalExposure),
        MaxOpenPositions = Math.Min(options.MaxOpenPositions, options.PaperRisk.MaxPaperPositionsTotal),
        MaxExposurePerGroup = options.PaperRisk.MaxPaperTotalExposure,
        AllowBasketArbs = true,
        AllowSingleMarketArbs = true,
        AllowCompleteSetSellArbs = true,
        AllowThresholdArbs = true,
        EnableSizingLogs = false
    };

    var sizing = new ExecutionSizingService(executionPolicy);
    var executionDecisionService = new ExecutionDecisionService(executionPolicy);
    var executionJournalPath = Path.Combine(AppContext.BaseDirectory, "data", "execution-journal.csv");
    var executionJournal = new ExecutionJournal(executionJournalPath);
    var positionBook = new PaperPositionBook(Path.Combine(AppContext.BaseDirectory, "data", "paper-positions.csv"));
    var paper = new PaperTradingEngine(executionPolicy, executionJournal, executionDecisionService, positionBook, options);
    var paperValidationHarness = new PaperPhaseValidationHarness();
    paperValidationHarness.TryRun(options, paper, positionBook, state, contentRootPath);
    var paperSettlementValidationHarness = new PaperSettlementValidationHarness();
    paperSettlementValidationHarness.TryRun(options, paper, positionBook, state, contentRootPath);
    var monitor = new OpportunityMonitor(Path.Combine(AppContext.BaseDirectory, "data", "arb-opportunities.csv"), options.MinEdgePerShare, -0.02m, TimeSpan.FromMinutes(2), options.MinExpectedProfit, new DryRunLiveOrderBuilder(minEdgePerShare: -0.01m, maxPlanCost: 100000m, minSize: 1m, tickSize: 0.001m, orderType: LiveOrderType.FOK, policy: executionPolicy));
    var semaphore = new SemaphoreSlim(options.MaxConcurrentRequests);
    var opportunityExecutionQueue = new OpportunityExecutionQueue();
    var singleConfig = StrategyOrchestrator.ResolveConfig(options.Strategies, "SingleMarketBuyBoth");
    var verifiedConfig = StrategyOrchestrator.ResolveConfig(options.Strategies, "VerifiedMultiOutcome");
    var autoConfig = StrategyOrchestrator.ResolveConfig(options.Strategies, "AutoCandidateMultiOutcome");
    var experimentalConfig = StrategyOrchestrator.ResolveConfig(options.Strategies, "ExperimentalMultiOutcome");
    var singleMarketArb = new SingleMarketOrderBookArbEngine(orderbookService, options.MinEdgePerShare, options.SingleMarketFees, options.SingleMarketSlippage, monitor, sizing, options.SingleMarketArb, state, contentRootPath, verifiedExecution, options.Diagnostics.OperationalQuietMode, options.Logging, quietLogGate, opportunityExecutionQueue, singleConfig.Mode);
    var singleMarketStrategy = new SingleMarketBuyBothOpportunityStrategy(singleMarketArb);
    var strategyOrchestrator = new StrategyOrchestrator(new IOpportunityStrategy[] { singleMarketStrategy }, options, state.RecordStrategyResult);
    Console.WriteLine($"[STRATEGY_ORCHESTRATOR] SingleMarketBuyBoth={singleConfig.Mode} VerifiedMultiOutcome={verifiedConfig.Mode} AutoCandidateMultiOutcome={autoConfig.Mode} ExperimentalMultiOutcome={experimentalConfig.Mode}");
    var singleMarketFullCycle = new SingleMarketFullCycleSummaryAggregator(options.SingleMarketArb);

    var config = new ConfigurationBuilder().SetBasePath(AppContext.BaseDirectory).AddJsonFile("appsettings.json", optional: true).Build();
    config.GetSection(CrossExchangeOptions.SectionName).Bind(crossOptions);
    config.GetSection(ExchangeFeesOptions.SectionName).Bind(feeOptions);
    config.GetSection(KalshiOptions.SectionName).Bind(kalshiOptions);
    var crossEngine = new CrossExchangeArbitrageEngine(crossOptions, feeOptions);
    var pairLoader = new MarketPairConfigLoader(Path.Combine(AppContext.BaseDirectory, "config", "market-pairs.json"));
    var kalshiHttp = new HttpClient { Timeout = TimeSpan.FromMilliseconds(kalshiOptions.RequestTimeoutMs) };
    var kalshiBookService = new KalshiOrderbookService(kalshiHttp, kalshiOptions);
    var discoveredMarkets = new List<Market>();
    var rollingOffset = 0;
    var scanId = 0L;
    var fullCoverageCompletedCount = 0;
    var cyclesCompletedSinceDiscovery = 0;
    var totalMarketsScannedSinceStart = 0L;
    var duplicateBatchWarnings = 0;
    var lastBatchFingerprint = "";
    var repeatedBatchCount = 0;
    var lastDiscoveryAt = default(DateTime);
    var discoveryStartedAt = default(DateTime);
    var discoveryCompletedAt = default(DateTime);
    var lastDiscoverySummary = new MarketDiscoverySummary();
    var lastHealthyDiscoverySummary = new MarketDiscoverySummary();
    var lastHealthyDiscoveryMarkets = new List<Market>();
    var lastHealthyDiscoveryAt = default(DateTime);
    var discoveryPartialAttemptCount = 0;
    var discoveryGuardSkippedCycles = 0;
    var discoveryGuardBlockedNewMarkets = 0;
    var discoveryLastFailureReason = string.Empty;
    var discoveryLastFailureKind = "Unknown";
    var discoveryMode = "Blocked";
    var discoveryFallbackAttempted = false;
    var discoveryFallbackReason = "None";
    var discoveryFallbackSucceeded = false;
    var discoveryFallbackActiveMarkets = 0;
    var discoveryBootstrapRetryCount = 0;
    DateTime? discoveryBootstrapLastAttemptUtc = null;
    DateTime? discoveryBootstrapNextRetryUtc = null;
    var discoveryBootstrapBackoffSeconds = 15;
    var discoveryRetriesSuppressedByBackoff = 0;
    var discoveryPersistedSnapshotLoaded = false;
    var discoveryPersistedSnapshotAgeSeconds = 0;
    var discoveryPersistedSnapshotActiveMarkets = 0;
    var discoveryUsingLastHealthySnapshot = false;
    var scannerPausedByDiscoveryGuard = false;
    var discoverySourceAuditExportWritten = false;
    var discoverySourceAuditExportPath = string.Empty;
    var discoveryScannerSafeSourceAvailable = false;
    var discoverySourceAuditSources = 0;
    var discoverySourceAuditScannerSafeSources = 0;
    var reducedUniverseBannerEmitted = false;
    var discoverySourceAuditRecommendedAction = string.Empty;
    DateTime? lastDegradationUtc = null;
    DateTime? lastRecoveryUtc = null;
    var emptyCycles = 0;
    var verifiedBasketCycle = 0;
    var verifiedPricingCycle = 0;
    var verifiedBasketRankingCycle = 0;
    var profileComparisonCycle = 0;
    var lastRankingFingerprint = string.Empty;
    var nearExecutableFingerprint = string.Empty;
    var allowlistHealthCycle = 0;
    var candidateScanCycle = 0;
    var verifiedScanCycle = 0;
    var portfolioCycle = 0;
    var mtmCycle = 0;
    var lastAllowlistHealthFingerprint = string.Empty;
    var lastCandidateScanFingerprint = string.Empty;
    var lastRejectedOnlyCandidateScanFingerprint = string.Empty;
    var lastPortfolioFingerprint = string.Empty;
    var lastMtmFingerprint = string.Empty;
    var mismatchCycle = 0;
    var experimentalCandidateCycle = 0;
    var lastExperimentalFingerprintByGroup = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    var emittedAllowlistRefreshFinalDecisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var emittedAllowlistRefreshActionExplanations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var emittedAllowlistRefreshSemanticConflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var emittedAllowlistRefreshMarketSetMismatches = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var emittedAllowlistUnstableManualReviewLocks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var lastProfileComparisonFingerprint = string.Empty;
    var mismatchFingerprintByGroup = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    QuietLogPolicy QuietPolicy(int? everyNCycles = null, int? maxPerHour = null) => new(
        options.Diagnostics.OperationalQuietMode,
        everyNCycles ?? Math.Max(1, options.Logging.QuietModeDefaultEveryNCycles),
        everyNCycles == 0 ? 0 : Math.Max(1, options.Logging.QuietModeDefaultEveryMinutes),
        options.Logging.QuietModeSuppressRepeatedHash,
        maxPerHour ?? Math.Max(1, options.Logging.QuietModeMaxSameEventPerHour),
        options.Diagnostics.DebuggerSafeMode);

    bool ShouldQuietLog(string category, string eventName, string stableHash, LogImportance importance = LogImportance.Normal, string? bucketHash = null, string? groupKey = null, string? marketId = null, string? strategy = null, int? everyNCycles = null, int? maxPerHour = null)
        => quietLogGate.ShouldLog(
            new LogEventKey(category, eventName, groupKey, marketId, strategy),
            new LogEventFingerprint(stableHash, bucketHash),
            importance,
            QuietPolicy(everyNCycles, maxPerHour));

    string PersistedDiscoverySnapshotPath()
    {
        var configured = options.MarketDiscovery.PersistedSnapshotPath;
        if (string.IsNullOrWhiteSpace(configured)) configured = Path.Combine("exports", "discovery-last-healthy-snapshot.json");
        return Path.IsPathRooted(configured) ? configured : Path.Combine(contentRootPath, configured);
    }

    void PersistHealthyDiscoverySnapshot(IReadOnlyList<Market> markets, MarketDiscoverySummary summary)
    {
        if (!options.MarketDiscovery.EnablePersistedHealthySnapshot || !summary.DiscoveryHealthy || summary.ActiveMarketsAvailable < options.MarketDiscovery.MinPersistedSnapshotActiveMarkets || string.Equals(summary.StoppedReason, "RequestError", StringComparison.OrdinalIgnoreCase) || string.Equals(summary.StoppedReason, "OperationCanceled", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(summary.LastDiscoveryError)) return;
        var path = PersistedDiscoverySnapshotPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var snapshot = new PersistedHealthyDiscoverySnapshot(
            ProcessRunContext.ProcessRunId,
            DateTime.UtcNow,
            markets.Count,
            summary.RawLoadedTotal,
            markets.Select(m => new PersistedHealthyMarket(m.id, m.question, m.conditionId, m.outcomes, m.clobTokenIds, m.active, m.closed, m.archived, m.accepting_orders, m.acceptingOrders, m.liquidity, m.volume24hr)).ToArray());
        File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(snapshot, Newtonsoft.Json.Formatting.Indented));
        Console.WriteLine($"[DISCOVERY_PERSISTED_SNAPSHOT_WRITTEN] Path={path} ActiveMarkets={markets.Count} CreatedAtUtc={snapshot.CreatedAtUtc:O}");
    }

    bool TryLoadPersistedHealthyDiscoverySnapshot()
    {
        var path = PersistedDiscoverySnapshotPath();
        if (!options.MarketDiscovery.EnablePersistedHealthySnapshot)
        {
            Console.WriteLine($"[DISCOVERY_PERSISTED_SNAPSHOT_LOAD_SKIPPED] Reason=Disabled Path={path}");
            return false;
        }
        if (!File.Exists(path))
        {
            Console.WriteLine($"[DISCOVERY_PERSISTED_SNAPSHOT_LOAD_SKIPPED] Reason=FileMissing Path={path}");
            return false;
        }
        try
        {
            var snapshot = Newtonsoft.Json.JsonConvert.DeserializeObject<PersistedHealthyDiscoverySnapshot>(File.ReadAllText(path));
            if (snapshot is null)
            {
                Console.WriteLine($"[DISCOVERY_PERSISTED_SNAPSHOT_LOAD_SKIPPED] Reason=InvalidSchema Path={path}");
                return false;
            }
            var age = DateTime.UtcNow - snapshot.CreatedAtUtc;
            if (age > TimeSpan.FromMinutes(Math.Max(1, options.MarketDiscovery.PersistedSnapshotTtlMinutes)))
            {
                Console.WriteLine($"[DISCOVERY_PERSISTED_SNAPSHOT_LOAD_SKIPPED] Reason=Expired Path={path}");
                return false;
            }
            if (snapshot.ActiveCount < options.MarketDiscovery.MinPersistedSnapshotActiveMarkets)
            {
                Console.WriteLine($"[DISCOVERY_PERSISTED_SNAPSHOT_LOAD_SKIPPED] Reason=TooFewMarkets Path={path}");
                return false;
            }
            lastHealthyDiscoveryMarkets = snapshot.Markets.Select(m => new Market
            {
                id = m.Id, question = m.Question, conditionId = m.ConditionId, outcomes = (m.Outcomes ?? Array.Empty<string>()).ToList(), clobTokenIds = (m.ClobTokenIds ?? Array.Empty<string>()).ToList(), active = m.Active, closed = m.Closed, archived = m.Archived, accepting_orders = m.AcceptingOrdersSnake, acceptingOrders = m.AcceptingOrders, liquidity = m.Liquidity, volume24hr = m.Volume24hr
            }).Where(m => !string.IsNullOrWhiteSpace(m.id) && m.clobTokenIds.Count >= 2).ToList();
            if (lastHealthyDiscoveryMarkets.Count < options.MarketDiscovery.MinPersistedSnapshotActiveMarkets)
            {
                Console.WriteLine($"[DISCOVERY_PERSISTED_SNAPSHOT_LOAD_SKIPPED] Reason=TooFewMarkets Path={path}");
                return false;
            }
            lastHealthyDiscoveryAt = snapshot.CreatedAtUtc;
            lastHealthyDiscoverySummary = new MarketDiscoverySummary(lastHealthyDiscoveryMarkets.Count, 0, 0, 0, lastHealthyDiscoveryMarkets.Count, snapshot.RawCount, lastHealthyDiscoveryMarkets.Count, DiscoveryHealthy: true, LastDiscoveryCompletedAtUtc: snapshot.CreatedAtUtc, DiscoveryCompleted: true, StoppedReason: "PersistedHealthySnapshot");
            discoveredMarkets = lastHealthyDiscoveryMarkets.ToList();
            lastDiscoverySummary = lastHealthyDiscoverySummary;
            discoveryUsingLastHealthySnapshot = true;
            discoveryPersistedSnapshotLoaded = true;
            discoveryPersistedSnapshotAgeSeconds = (int)Math.Max(0, age.TotalSeconds);
            discoveryPersistedSnapshotActiveMarkets = lastHealthyDiscoveryMarkets.Count;
            Console.WriteLine($"[DISCOVERY_PERSISTED_SNAPSHOT_LOADED] Active={discoveryPersistedSnapshotActiveMarkets} AgeSeconds={discoveryPersistedSnapshotAgeSeconds} Path={path}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DISCOVERY_PERSISTED_SNAPSHOT_LOAD_SKIPPED] Reason=ReadError Path={path} Message={ex.Message.Replace(' ', '_')}");
            return false;
        }
    }

    int NextDiscoveryBackoffSeconds(int current) => current <= 15 ? 30 : current <= 30 ? 60 : current <= 60 ? 120 : 300;


    void UpdateDiscoveryGuardRuntimeState()
    {
        var stats = orderbookService.GetStats();
        var now = DateTime.UtcNow;
        var ageSeconds = lastHealthyDiscoveryAt == default ? 0 : (int)Math.Max(0, (now - lastHealthyDiscoveryAt).TotalSeconds);
        var discoveryBootstrapHealthy = lastHealthyDiscoveryMarkets.Count >= options.MarketDiscovery.MinHealthyActiveMarkets;
        var reducedUniverseActive = options.MarketDiscovery.AllowReducedUniverseDiagnosticsOnly && string.Equals(discoveryMode, "ReducedUniverseDiagnosticsOnly", StringComparison.OrdinalIgnoreCase);
        discoveryScannerSafeSourceAvailable = !reducedUniverseActive && (discoveryUsingLastHealthySnapshot || (lastDiscoverySummary.DiscoveryHealthy && !string.Equals(discoveryMode, "Blocked", StringComparison.OrdinalIgnoreCase)));
        var discoveryBlocked = reducedUniverseActive || options.MarketDiscovery.SourceAuditOnly || !lastDiscoverySummary.DiscoveryHealthy || string.Equals(discoveryMode, "Blocked", StringComparison.OrdinalIgnoreCase) || !discoveryBootstrapHealthy || !discoveryScannerSafeSourceAvailable;
        var discoveryBlockedReason = options.MarketDiscovery.SourceAuditOnly
            ? "SourceAuditOnly"
            : reducedUniverseActive
                ? "BlockedReducedUniverseDiagnosticsOnly"
            : !discoveryScannerSafeSourceAvailable
                ? "NoScannerSafeDiscoverySource"
                : !discoveryBootstrapHealthy
                    ? "DiscoveryBootstrapUnavailable"
                    : "None";
        var discoveryStable = !discoveryBlocked && (lastDiscoverySummary.DiscoveryHealthy || discoveryUsingLastHealthySnapshot);
        var effectiveScannerPausedByDiscoveryGuard = scannerPausedByDiscoveryGuard || discoveryBlocked;
        var allowlistSkippedByDiscovery = discoveryBlocked;
        var orderbookRecovered = stats.OrderbookCircuitBreakerState.Equals("Closed", StringComparison.OrdinalIgnoreCase) && stats.BatchBookNormalBadRequestsAfterBreakerOpen == 0;
        var longRunReasons = new List<string>();
        if (reducedUniverseActive) longRunReasons.Add("BlockedReducedUniverseDiagnosticsOnly");
        else if (!discoveryStable) longRunReasons.Add("DiscoveryUnavailable");
        if (stats.OrderbookCircuitBreakerActive && stats.OrderbookRecoverySucceededCount <= 0) longRunReasons.Add("OrderbookBreakerActive");
        if (stats.BatchBookNormalBadRequestsAfterBreakerOpen > 0) longRunReasons.Add("PostBreakerBadRequests");
        if (stats.InvalidTokenQuarantineActive + stats.MarketOrderbookQuarantineActive > Math.Max(25, options.OrderBook.CircuitBreakerInvalidTokensPerHourThreshold)) longRunReasons.Add("QuarantineStormActive");
        state.SetDiscoveryGuardState(
            lastDiscoverySummary.DiscoveryHealthy,
            discoveryStable,
            discoveryUsingLastHealthySnapshot,
            ageSeconds,
            discoveryPartialAttemptCount,
            ($"{discoveryLastFailureReason};DiscoveryLastFailureKind={discoveryLastFailureKind};DiscoveryMode={discoveryMode};DiscoveryFallbackAttempted={discoveryFallbackAttempted.ToString().ToLowerInvariant()};DiscoveryFallbackReason={discoveryFallbackReason};DiscoveryFallbackSucceeded={discoveryFallbackSucceeded.ToString().ToLowerInvariant()};DiscoveryFallbackActiveMarkets={discoveryFallbackActiveMarkets}"),
            effectiveScannerPausedByDiscoveryGuard,
            discoveryGuardSkippedCycles,
            discoveryUsingLastHealthySnapshot,
            discoveryGuardBlockedNewMarkets,
            longRunReasons.Count == 0,
            longRunReasons.Count == 0 ? "None" : string.Join("|", longRunReasons),
            orderbookRecovered,
            lastDegradationUtc,
            lastRecoveryUtc,
            discoveryBootstrapHealthy: discoveryBootstrapHealthy,
            discoveryBootstrapRetryCount: discoveryBootstrapRetryCount,
            discoveryBootstrapLastAttemptUtc: discoveryBootstrapLastAttemptUtc,
            discoveryBootstrapNextRetryUtc: discoveryBootstrapNextRetryUtc,
            discoveryBootstrapBackoffSeconds: discoveryBootstrapBackoffSeconds,
            discoveryBootstrapFailureReason: $"{discoveryLastFailureReason};DiscoveryLastFailureKind={discoveryLastFailureKind};DiscoveryMode={discoveryMode}",
            discoveryRetryBackoffSeconds: discoveryBootstrapBackoffSeconds,
            discoveryRetriesSuppressedByBackoff: discoveryRetriesSuppressedByBackoff,
            discoveryPersistedSnapshotLoaded: discoveryPersistedSnapshotLoaded,
            discoveryPersistedSnapshotAgeSeconds: discoveryPersistedSnapshotAgeSeconds,
            discoveryPersistedSnapshotActiveMarkets: discoveryPersistedSnapshotActiveMarkets,
            allowlistEvaluationSkipped: allowlistSkippedByDiscovery,
            allowlistEvaluationSkippedReason: reducedUniverseActive ? "ReducedUniverseDiagnosticsOnly" : allowlistSkippedByDiscovery ? discoveryBlockedReason : string.Empty,
            allowlistClassificationBlockedByDiscovery: allowlistSkippedByDiscovery,
            soakReadiness: discoveryBlocked ? "Blocked" : "Ready",
            soakReadinessReason: discoveryBlocked ? discoveryBlockedReason : "None",
            discoveryBlockedReason: discoveryBlockedReason,
            discoverySelectedSource: reducedUniverseActive ? "ReducedUniverseDiagnosticsOnly" : discoveryScannerSafeSourceAvailable ? (discoveryUsingLastHealthySnapshot ? "PersistedHealthySnapshot" : discoveryMode) : "Blocked",
            discoveryScannerSafeSourceAvailable: discoveryScannerSafeSourceAvailable,
            discoverySourceAuditOnly: options.MarketDiscovery.SourceAuditOnly,
            discoverySourceAuditExportWritten: discoverySourceAuditExportWritten,
            discoverySourceAuditExportPath: discoverySourceAuditExportPath,
            discoverySourceAuditSources: discoverySourceAuditSources,
            discoverySourceAuditScannerSafeSources: discoverySourceAuditScannerSafeSources,
            discoverySourceAuditRecommendedAction: discoverySourceAuditRecommendedAction,
            discoveryReducedUniverse: reducedUniverseActive,
            reducedUniverseMarkets: reducedUniverseActive ? discoveredMarkets.Count : 0,
            reducedUniverseMaxMarkets: options.MarketDiscovery.ReducedUniverseMaxMarkets,
            reducedUniverseSource: options.MarketDiscovery.ReducedUniverseSource,
            paperExecutionGloballyBlockedByDiscovery: reducedUniverseActive || !lastDiscoverySummary.DiscoveryHealthy || !discoveryScannerSafeSourceAvailable || string.Equals(discoveryMode, "Blocked", StringComparison.OrdinalIgnoreCase),
            strategyExecutionGloballyBlocked: reducedUniverseActive || discoveryBlocked,
            diagnosticsUniverse: reducedUniverseActive ? "Reduced" : "Full",
            tradingReadiness: !discoveryBlocked);
    }

    var basketStateByGroup = new Dictionary<string, VerifiedBasketState>(StringComparer.OrdinalIgnoreCase);
    var stability = new VerifiedOpportunityStabilityTracker();
    var edgeStabilityLogThrottle = new EdgeStabilityLogThrottle();
    var logThrottle = new LogThrottle();
    var verifiedBasketLastFingerprint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var verifiedBasketLastExecutable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    var verifiedPricingLastFingerprint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var exportService = new MultiOutcomeCandidateExportService(options.MultiOutcomeReview, contentRootPath);
    var multiOutcomeValidator = new MutuallyExclusiveGroupValidator(options.MultiOutcomeArbitrage, contentRootPath);
    var verifiedResolver = new VerifiedMultiOutcomeGroupResolver();
    var preTradeResults = new List<VerifiedBasketPreTradeValidationResult>();
    var promotedVerifiedOpportunities = new List<VerifiedMultiOutcomeOpportunity>();
    Console.WriteLine($"[DISCOVERY_PERSISTED_SNAPSHOT_CONFIG] Enabled={options.MarketDiscovery.EnablePersistedHealthySnapshot.ToString().ToLowerInvariant()} Path={PersistedDiscoverySnapshotPath()} TtlMinutes={options.MarketDiscovery.PersistedSnapshotTtlMinutes} MinActiveMarkets={options.MarketDiscovery.MinPersistedSnapshotActiveMarkets}");
    var persistedSnapshotLoadedAtStartup = TryLoadPersistedHealthyDiscoverySnapshot();
    Console.WriteLine($"[DISCOVERY_PERSISTED_SNAPSHOT_LOAD_RESULT] Loaded={persistedSnapshotLoadedAtStartup.ToString().ToLowerInvariant()} ActiveMarkets={discoveryPersistedSnapshotActiveMarkets} AgeSeconds={discoveryPersistedSnapshotAgeSeconds}");
    if (options.MarketDiscovery.SourceAuditOnly && discoveryGuardSkippedCycles == 0) discoveryGuardSkippedCycles++;
    UpdateDiscoveryGuardRuntimeState();
    if (options.MarketDiscovery.SourceAuditOnly)
    {
        Console.WriteLine("[DISCOVERY_SOURCE_AUDIT_ONLY] Enabled=true Action=ExportAuditAndExit Scanner=false Orderbooks=false Paper=false Live=false");
        var audit = MarketDataService.ExportSourceAudit(options, contentRootPath);
        discoverySourceAuditExportWritten = true;
        discoverySourceAuditExportPath = audit.Path;
        discoveryScannerSafeSourceAvailable = audit.ScannerSafeSources > 0;
        discoverySourceAuditSources = audit.Sources;
        discoverySourceAuditScannerSafeSources = audit.ScannerSafeSources;
        discoverySourceAuditRecommendedAction = audit.RecommendedAction;
        UpdateDiscoveryGuardRuntimeState();
        Console.WriteLine(RuntimeHealthSnapshot.From(state, options).ToLogLine());
        Console.WriteLine(RuntimeHealthTrendTracker.ToSoakStatusLogLine(RuntimeHealthSnapshot.From(state, options), RuntimeHealthTrendTracker.Current(options.RuntimeHealth), options, state));
        return;
    }
    Console.WriteLine($"[ALLOWLIST] Loaded verified multi-outcome groups: {multiOutcomeValidator.LoadedAllowlistCount}");
    var startupAllowlistValidation = ScanLogSummaryService.AllowlistConfigValidation(multiOutcomeValidator.GetAllowlistedGroups());
    Console.WriteLine(startupAllowlistValidation.ToLogLine());
    if (options.MarketDiscovery.DiagnosticsOnly)
    {
        Console.WriteLine("[DISCOVERY_DIAGNOSTICS_ONLY] Enabled=true Action=RunDiscoveryOnceAndExitScanner OrderbookRequests=false Paper=false Live=false");
        var discovery = await marketService.GetMarketsAsync(options, stoppingToken, discoveryBootstrapBackoffSeconds);
        var summary = discovery.Summary;
        Console.WriteLine($"[DISCOVERY_DIAGNOSTICS_ONLY_RESULT] Healthy={summary.DiscoveryHealthy.ToString().ToLowerInvariant()} Pages={summary.PagesFetched} Raw={summary.RawLoadedTotal} Active={summary.ActiveMarketsAvailable} StoppedReason={summary.StoppedReason ?? "None"} LastError={summary.LastDiscoveryError ?? "None"}");
        if (summary.DiscoveryHealthy && summary.ActiveMarketsAvailable >= options.MarketDiscovery.MinHealthyActiveMarkets) PersistHealthyDiscoverySnapshot(discovery.Markets, summary);
        return;
    }
    while (!stoppingToken.IsCancellationRequested)
    {
        var started = DateTime.UtcNow;
        string? lastError = null;
        long singleMarketFullCycleId = 0;
        var batchStartIndex = 0;
        var batchEndIndex = 0;
        try
        {
            SetScannerStage("MemoryGuard", "MemoryGuard");
            memoryGuard.Check(state, options, () => { orderbookService.ClearAllCache(); orderbookService.TrimAllBoundedStores(); quietLogGate.TrimExpired(); }, contentRootPath);
            state.SetRuntimeCounts(repairHistoryCount: allowlistRepairService.RepairHistorySnapshotCount, dryRunOrderPlansCount: verifiedExecution.DryRunPlanCount, fillSimulationsCount: verifiedExecution.FillSimulationCount, executionAuditCount: verifiedExecution.AuditCount, orderbookCacheCount: orderbookService.CacheEntryCount, marketCacheCount: discoveredMarkets.Count);
            if (memoryGuard.ShouldSkipScannerCycle())
            {
                scannerState.TryPauseByMemoryGuard(Console.WriteLine);
                state.SetStatus(state.Status with { ScannerActive = false, LastScanTime = DateTime.UtcNow });
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
                continue;
            }
            if (!state.Controls.IsPaused && scannerState.State != ScannerRuntimeState.Running) scannerState.TryResume(Console.WriteLine);
            var paperOpenBeforeScan = state.PaperExecutionsCount;
            if (ShouldLogScannerChannel("scan_start", "scan_start"))
                uiLogger.LogInfo("scanner", "{\"event\":\"scan_start\",\"timestamp\":\"" + started.ToString("O") + "\"}");
            if (state.Controls.IsPaused)
            {
                if (state.Controls.Reason == "MEMORY_CRITICAL") scannerState.TryPauseByMemoryGuard(Console.WriteLine);
                if (state.Controls.Reason == "PAPER_CONFIG_ERROR") scannerState.TryPauseByConfigError(Console.WriteLine);
                state.SetStatus(state.Status with { ScannerActive = false, LastScanTime = DateTime.UtcNow });
                if (options.Caches.ClearOrderbookCacheAfterScan) orderbookService.ClearExpiredCache();
            state.SetRuntimeCounts(repairHistoryCount: allowlistRepairService.RepairHistorySnapshotCount, dryRunOrderPlansCount: verifiedExecution.DryRunPlanCount, fillSimulationsCount: verifiedExecution.FillSimulationCount, executionAuditCount: verifiedExecution.AuditCount, orderbookCacheCount: orderbookService.CacheEntryCount, marketCacheCount: discoveredMarkets.Count);
            memoryGuard.Check(state, options, () => { orderbookService.ClearAllCache(); orderbookService.TrimAllBoundedStores(); quietLogGate.TrimExpired(); }, contentRootPath);
            SetScannerStage("SignalRPublish", "PushUiUpdates");
            await PushUiUpdates(state, hub, uiLogger, options, verifiedExecution, contentRootPath);
                if (ShouldLogScannerChannel("scan_skipped", $"scan_skipped|{state.Controls.Reason}"))
                    uiLogger.LogInfo("scanner", "{\"event\":\"scan_skipped\",\"reason\":\"PAUSED\"}");
                await Task.Delay(options.ScanIntervalMs, stoppingToken);
                continue;
            }
            monitor.BeginCycle();
            if (lastHealthyDiscoveryMarkets.Count == 0 && discoveryBootstrapNextRetryUtc.HasValue && DateTime.UtcNow < discoveryBootstrapNextRetryUtc.Value)
            {
                discoveryRetriesSuppressedByBackoff++;
                scannerPausedByDiscoveryGuard = true;
                UpdateDiscoveryGuardRuntimeState();
                await Task.Delay(options.ScanIntervalMs, stoppingToken);
                continue;
            }
            if (lastDiscoveryAt == default || DateTime.UtcNow - lastDiscoveryAt >= TimeSpan.FromMinutes(options.FullDiscoveryIntervalMinutes) || discoveredMarkets.Count == 0)
            {
                SetScannerStage("Discovery", "MarketDataService");
                discoveryStartedAt = DateTime.UtcNow;
                discoveryBootstrapLastAttemptUtc = discoveryStartedAt;
                var discovery = await marketService.GetMarketsAsync(options, stoppingToken, discoveryBootstrapBackoffSeconds);
                var discoveredCandidateMarkets = discovery.Markets.Where(m => m?.outcomes?.Count == 2 && m.clobTokenIds?.Count >= 2).Take(Math.Max(1, options.RuntimeMemory.MaxMarketCacheEntries)).ToList();
                var attemptSummary = discovery.Summary;
                var discoveryHealth = ScanLogSummaryService.DiscoveryHealth(attemptSummary, options.MarketDiscovery.MinHealthyActiveMarkets);
                lastDiscoveryAt = DateTime.UtcNow;
                discoveryCompletedAt = lastDiscoveryAt;
                cyclesCompletedSinceDiscovery = 0;
                discoveryUsingLastHealthySnapshot = false;
                scannerPausedByDiscoveryGuard = false;
                if (options.LogPrefetchSummary)
                    Console.WriteLine($"[DISCOVERY] marketsDiscovered={attemptSummary.MarketsDiscovered}, pagesFetched={attemptSummary.PagesFetched}, duplicatesRemoved={attemptSummary.DuplicatesRemoved}, inactiveSkipped={attemptSummary.InactiveSkipped}, activeMarketsAvailable={attemptSummary.ActiveMarketsAvailable}, rawLoadedTotal={attemptSummary.RawLoadedTotal}, uniqueMarketsTotal={attemptSummary.UniqueMarketsTotal}, skippedClosed={attemptSummary.SkippedClosed}, skippedArchived={attemptSummary.SkippedArchived}, skippedMissingTokenIds={attemptSummary.SkippedMissingTokenIds}, skippedInvalidShape={attemptSummary.SkippedInvalidShape}");
                Console.WriteLine(discoveryHealth.ToLogLine());
                if (!discoveryHealth.Degraded)
                {
                    discoveredMarkets = discoveredCandidateMarkets;
                    lastDiscoverySummary = attemptSummary;
                    lastHealthyDiscoveryMarkets = discoveredCandidateMarkets.ToList();
                    lastHealthyDiscoverySummary = attemptSummary;
                    options.DiscoveryPartialDiagnosticsOnly = false;
                    lastHealthyDiscoveryAt = lastDiscoveryAt;
                    discoveryLastFailureReason = string.Empty;
                    discoveryLastFailureKind = "Unknown";
                    discoveryMode = attemptSummary.DiscoveryMode;
                    discoveryFallbackAttempted = attemptSummary.DiscoveryFallbackAttempted;
                    discoveryFallbackReason = attemptSummary.DiscoveryFallbackReason;
                    discoveryFallbackSucceeded = attemptSummary.DiscoveryFallbackSucceeded;
                    discoveryFallbackActiveMarkets = attemptSummary.DiscoveryFallbackActiveMarkets;
                    lastRecoveryUtc = DateTime.UtcNow;
                    discoveryBootstrapBackoffSeconds = 15;
                    discoveryBootstrapNextRetryUtc = null;
                    PersistHealthyDiscoverySnapshot(discoveredMarkets, attemptSummary);
                }
                else
                {
                    discoveryPartialAttemptCount++;
                    discoveryBootstrapRetryCount++;
                    discoveryLastFailureReason = string.IsNullOrWhiteSpace(attemptSummary.LastDiscoveryError) ? discoveryHealth.Reason : attemptSummary.LastDiscoveryError!;
                    discoveryLastFailureKind = attemptSummary.DiscoveryLastFailureKind;
                    discoveryMode = attemptSummary.DiscoveryMode;
                    discoveryFallbackAttempted = attemptSummary.DiscoveryFallbackAttempted;
                    discoveryFallbackReason = attemptSummary.DiscoveryFallbackReason;
                    discoveryFallbackSucceeded = attemptSummary.DiscoveryFallbackSucceeded;
                    discoveryFallbackActiveMarkets = attemptSummary.DiscoveryFallbackActiveMarkets;
                    lastDegradationUtc = DateTime.UtcNow;
                    var degradedReason = attemptSummary.ActiveMarketsAvailable < options.MarketDiscovery.MinHealthyActiveMarkets
                        ? (string.IsNullOrWhiteSpace(discoveryHealth.Reason) ? "ActiveBelowExpectedMin" : discoveryHealth.Reason.Contains("OperationCanceled", StringComparison.OrdinalIgnoreCase) ? $"{discoveryHealth.Reason};ActiveBelowExpectedMin" : "ActiveBelowExpectedMin")
                        : (string.IsNullOrWhiteSpace(discoveryHealth.Reason) || discoveryHealth.Reason.Equals("ActiveBelowExpectedMin", StringComparison.OrdinalIgnoreCase) ? "DiscoveryOperationCanceled" : discoveryHealth.Reason);
                    Console.WriteLine($"[DISCOVERY_DEGRADED_MODE] Reason={degradedReason} Active={attemptSummary.ActiveMarketsAvailable} ExpectedMinActive={options.MarketDiscovery.MinHealthyActiveMarkets}");
                    discoveryBootstrapNextRetryUtc = DateTime.UtcNow.AddSeconds(Math.Max(15, discoveryBootstrapBackoffSeconds));
                    if (lastHealthyDiscoveryMarkets.Count == 0) discoveryBootstrapBackoffSeconds = NextDiscoveryBackoffSeconds(discoveryBootstrapBackoffSeconds);
                    if (lastHealthyDiscoveryMarkets.Count > 0)
                    {
                        var healthyIds = lastHealthyDiscoveryMarkets.Select(m => m.id).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        discoveryGuardBlockedNewMarkets += discoveredCandidateMarkets.Count(m => !healthyIds.Contains(m.id));
                        discoveredMarkets = lastHealthyDiscoveryMarkets.ToList();
                        lastDiscoverySummary = lastHealthyDiscoverySummary;
                        discoveryUsingLastHealthySnapshot = true;
                        Console.WriteLine($"[DISCOVERY_GUARD] Action=UseLastHealthySnapshot HealthyMarkets={discoveredMarkets.Count} PartialMarkets={discoveredCandidateMarkets.Count} BlockedNewMarkets={discoveryGuardBlockedNewMarkets} Reason={degradedReason}");
                    }
                    else
                    {
                        if (options.MarketDiscovery.AllowReducedUniverseDiagnosticsOnly && discoveredCandidateMarkets.Count > 0)
                        {
                            discoveredMarkets = discoveredCandidateMarkets.Take(Math.Max(1, options.MarketDiscovery.ReducedUniverseMaxMarkets)).ToList();
                            lastDiscoverySummary = attemptSummary with { DiscoveryHealthy = false, DiscoveryMode = "ReducedUniverseDiagnosticsOnly", StoppedReason = "ReducedUniverseDiagnosticsOnly" };
                            discoveryMode = "ReducedUniverseDiagnosticsOnly";
                            options.DiscoveryPartialDiagnosticsOnly = true;
                            scannerPausedByDiscoveryGuard = false;
                            if (!reducedUniverseBannerEmitted)
                            {
                                Console.WriteLine($"[REDUCED_UNIVERSE_DIAGNOSTICS_ONLY] Enabled=true Source={options.MarketDiscovery.ReducedUniverseSource} MaxMarkets={options.MarketDiscovery.ReducedUniverseMaxMarkets} PaperBlocked={options.MarketDiscovery.ReducedUniverseBlockPaper.ToString().ToLowerInvariant()} TradingReady=false Reason=NoScannerSafeDiscoverySource");
                                reducedUniverseBannerEmitted = true;
                            }
                            Console.WriteLine($"[DISCOVERY_GUARD] Action=ReducedUniverseDiagnosticsOnly Reason=NoScannerSafeDiscoverySource PartialMarkets={discoveredCandidateMarkets.Count} PaperExecutionGloballyBlockedByDiscovery=true LiveTrading=false");
                        }
                        else
                        {
                            discoveredMarkets = new List<Market>();
                            lastDiscoverySummary = attemptSummary;
                            scannerPausedByDiscoveryGuard = true;
                            discoveryGuardSkippedCycles++;
                            Console.WriteLine($"[DISCOVERY_GUARD] Action=PauseScanner Reason=NoScannerSafeDiscoverySource PartialMarkets={discoveredCandidateMarkets.Count}");
                        }
                    }
                }
                UpdateDiscoveryGuardRuntimeState();
                if (discoveryHealth.Degraded)
                {
                    var postDegradationHealth = RuntimeHealthSnapshot.From(state, options);
                    Console.WriteLine(RuntimeHealthTrendTracker.ToSoakStatusLogLine(postDegradationHealth, RuntimeHealthTrendTracker.Current(options.RuntimeHealth), options, state));
                }
            }

            if (scannerPausedByDiscoveryGuard && discoveredMarkets.Count == 0)
            {
                UpdateDiscoveryGuardRuntimeState();
                state.SetRuntimeCounts(repairHistoryCount: allowlistRepairService.RepairHistorySnapshotCount, dryRunOrderPlansCount: verifiedExecution.DryRunPlanCount, fillSimulationsCount: verifiedExecution.FillSimulationCount, executionAuditCount: verifiedExecution.AuditCount, orderbookCacheCount: orderbookService.CacheEntryCount, marketCacheCount: discoveredMarkets.Count);
                await Task.Delay(options.ScanIntervalMs, stoppingToken);
                continue;
            }

            var configuredPoolLimit = options.UseAllDiscoveredMarkets ? options.MaxMarketsInPool : (options.MaxMarketsInPool > 0 ? options.MaxMarketsInPool : options.MarketScanLimit);
            var effectiveMarketLimit = configuredPoolLimit <= 0 ? discoveredMarkets.Count : Math.Min(configuredPoolLimit, discoveredMarkets.Count);
            var poolLimitReason = effectiveMarketLimit < discoveredMarkets.Count ? "ConfiguredMaxMarketsToScan" : "AllDiscoveredMarkets";
            if (options.Logging.LogScanConfigEachCycle) Console.WriteLine($"[SCAN_CONFIG] ActiveMarkets={discoveredMarkets.Count} PoolLimit={effectiveMarketLimit} Reason={poolLimitReason}");
            var scanPool = discoveredMarkets.Take(effectiveMarketLimit).ToList();
            var batchSize = options.Mode == "AllAtOnce" ? scanPool.Count : Math.Min(options.ScanBatchSize, scanPool.Count);
            batchSize = Math.Min(batchSize, options.MaxOrderbooksPerCycle);
            var currentRollingOffsetBefore = rollingOffset;
            var filtered = BuildRollingBatch(scanPool, ref rollingOffset, batchSize, options);
            var currentRollingOffsetAfter = rollingOffset;
            batchStartIndex = filtered.Count == 0 ? 0 : currentRollingOffsetBefore;
            batchEndIndex = filtered.Count == 0 ? 0 : ((currentRollingOffsetBefore + filtered.Count - 1) % Math.Max(1, scanPool.Count));
            var fullCoverageCompletedThisBatch = scanPool.Count > 0 && filtered.Count > 0 && (currentRollingOffsetAfter == 0 || currentRollingOffsetAfter < currentRollingOffsetBefore);
            if (fullCoverageCompletedThisBatch) fullCoverageCompletedCount++;
            cyclesCompletedSinceDiscovery++;
            totalMarketsScannedSinceStart += filtered.Count;
            scanId++;
            singleMarketFullCycleId = scanPool.Count == 0 ? scanId : (fullCoverageCompletedThisBatch ? fullCoverageCompletedCount : fullCoverageCompletedCount + 1);
            var singleMarketFullCycleComplete = scanPool.Count > 0 && (options.Mode == "AllAtOnce" || currentRollingOffsetAfter == 0 || filtered.Count >= scanPool.Count || fullCoverageCompletedThisBatch);
            var orderbookSemaphore = new SemaphoreSlim(options.MaxConcurrentOrderbookRequests);
            SetScannerStage("BatchOrderbookFetch", "OrderBookService");
            await orderbookService.PrefetchBinarySnapshotsAsync(filtered);
            SetScannerStage("StrategyOrchestrator", "StrategyOrchestrator");
            var strategyResults = await strategyOrchestrator.RunEnabledAsync(new OpportunityStrategyContext(filtered!, new PaperTradingEngineFacade { Engine = paper }, orderbookSemaphore, singleMarketFullCycleId, singleMarketFullCycleComplete, options.Diagnostics.OperationalQuietMode || !options.SingleMarketArb.LogBatchSummaries, stoppingToken));
            var singleResult = strategyResults.FirstOrDefault(x => x.StrategyName.Equals("SingleMarketBuyBoth", StringComparison.OrdinalIgnoreCase));
            var scanStats = new SingleMarketScanStats(
                (int)(singleResult?.Scanned ?? 0),
                (int)(singleResult?.Books ?? 0),
                (int)(singleResult?.BothAsks ?? 0),
                (int)(singleResult?.Candidates ?? 0),
                (int)(singleResult?.PaperOpened ?? 0),
                (int)(singleResult?.PositiveEdges ?? 0),
                0,
                0,
                singleResult?.RejectedByReason?.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase),
                null,
                singleResult?.BestEdge,
                null,
                (int)(singleResult?.ExecutionReady ?? 0),
                0,
                (int)(singleResult?.SingleMarketCircuitBreakerSkippedMarkets ?? 0),
                (int)(singleResult?.SingleMarketCircuitBreakerSkippedCycles ?? 0));
            var singleMarketFullSummary = singleMarketFullCycle.AddBatch(singleMarketFullCycleId, state.SingleMarketSnapshot.Summary, state.SingleMarketSnapshot.DataQualityRejectSamples);
            if (options.Diagnostics.OperationalQuietMode && singleMarketFullCycle.ShouldLog(singleMarketFullSummary, options.Logging, singleMarketFullCycleComplete, options.SingleMarketArb.LogCycleProgress))
                Console.WriteLine(SingleMarketFullCycleSummaryAggregator.ToLogLine(singleMarketFullSummary));

            MultiOutcomeGroupArbEngine.MultiOutcomeScanReport multiOutcomeReport = new(0,0,0,0,0,0,0,0m,0m,0m,"","NotEvaluated",new Dictionary<string,int>(),Array.Empty<MultiOutcomeGroupArbEngine.RejectedSample>(),Array.Empty<MultiOutcomeGroupArbEngine.CandidateGroupReview>());
            SetScannerStage("MultiCandidateScan", "MultiOutcomeGroupArbEngine");
            if (options.MultiOutcomeArbitrage.Enabled)
            {
                var multiEngine = new MultiOutcomeGroupArbEngine(
                    orderbookService,
                    minEdgePerShare: options.MultiOutcomeArbitrage.MinMultiOutcomeEdge,
                    feeBufferPerLeg: options.SingleMarketFees,
                    slippageBufferPerLeg: options.MultiOutcomeArbitrage.SlippageBufferPerLeg,
                    enableYesBasket: false,
                    monitor: monitor,
                    decisionService: executionDecisionService,
                    logRejectedCandidates: options.Logging.LogRejectedMultiOutcomeCandidates,
                    logRejectedSummary: false,
                    rejectedSampleSize: options.Logging.RejectedMultiOutcomeSampleSize > 0 ? options.Logging.RejectedMultiOutcomeSampleSize : options.Logging.RejectedCandidateSampleSize,
                    validator: multiOutcomeValidator,
                    operationalQuietMode: options.Diagnostics.OperationalQuietMode,
                    logging: options.Logging,
                    quietLogGate: quietLogGate);
                multiOutcomeReport = await multiEngine.ScanAsync(filtered!, paper, orderbookSemaphore, stoppingToken);
                var boundedCandidates = exportService.BuildBoundedCandidates(multiOutcomeReport.CandidateGroupsForReview, options.MultiOutcomeReview.TopCandidateGroupsForReview, options.MultiOutcomeReview.MaxMarketsPerCandidateGroup, includeMarkets: true);
                var reviewReport = exportService.BuildReviewReport(multiOutcomeReport.CandidateGroupsForReview, options.MultiOutcomeReview.AllowUnpricedLegsInTemplate);
                state.SetMultiOutcomeCandidates(boundedCandidates);
                state.SetMultiOutcomeReviewReport(reviewReport);
                exportService.ExportIfDue(multiOutcomeReport.CandidateGroupsForReview);

                if (options.MultiOutcomeArbitrage.EvaluateVerifiedGroupsAgainstFullPool)
                {
                    var allByMarketId = GroupKeyDictionaryBuilder.BuildUniqueByGroupKey(discoveredMarkets.Where(m => !string.IsNullOrWhiteSpace(m.id)), m => m.id, "Scanner.DiscoveredMarketsById", DuplicateGroupKeyPolicy.KeepLatest);
                    var allowlistedGroups = multiOutcomeValidator.GetAllowlistedGroups();
                    var resolved = verifiedResolver.ResolveVerifiedGroups(allowlistedGroups, allByMarketId, options.MultiOutcomeArbitrage, lastDiscoverySummary.DiscoveryHealthy);
                    var verifiedMismatch = resolved.Count(x => x.ValidationStatus != "VerifiedGroupResolved");
                    var verifiedResolved = resolved.Count - verifiedMismatch;
                    var verifiedEvaluated = 0;
                    var verifiedExecutable = 0;
                    var activeExecutable = 0;
                    var experimentalCandidates = 0;
                    var diagnosticsOnlyPositive = 0;
                    var verifiedActivePositive = 0;
                    var verifiedRawPositiveOnly = 0;
                    var verifiedAlternatePositive = 0;
                    var verifiedWouldOpenIfPaperEligible = 0;
                    var verifiedRejectedByCostProfile = 0;
                    var verifiedRejectedByStability = 0;
                    var verifiedPricingBlockedByMissingNoAsk = 0;
                    var verifiedPricingBlockedByOrderbookUnavailable = 0;
                    var verifiedPricingBlockedByQuarantinedToken = 0;
                    var verifiedPricingBlockedByEmptyBook = 0;
                    var verifiedPricingBlockedByCircuitBreakerActive = 0;
                    var verifiedPricingBlockedByMarketOrderbookQuarantined = 0;
                    var verifiedPricingBlockedByTokenQuarantined = 0;
                    var verifiedRejectedByMissingNoAsk = 0;
                    var verifiedRejectedByUnresolvedGroup = 0;
                    var verifiedRejectedByRisk = 0;
                    var verifiedBlockedByFill = 0;
                    var verifiedBlockedByDepth = 0;
                    var verifiedBlockedByUnknown = 0;
                    var paperOpenedCount = 0;
                    var suppressedDuplicateCount = 0;
                    var skipReason = "None";
                    var groupDiagnostics = new List<VerifiedGroupDiagnosticDto>();
                    var pricingDiagnostics = new List<VerifiedGroupPricingDto>();
                    var basketCostDiagnostics = new List<VerifiedBasketCostBreakdownDto>();
                    var verifiedPricingExport = new List<object>();
                    var verifiedScreenResults = new List<VerifiedBasketScreener.ScreenResult>();
                    foreach (var g in resolved)
                    {
                        if (g.ValidationStatus != "VerifiedGroupResolved")
                        {
                            mismatchCycle++;
                            if (options.Logging.LogVerifiedMismatchDetails)
                            {
                                var missingIdsHash = string.Join(",", g.MissingMarketIds.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)).GetHashCode();
                                var fp = $"{g.GroupKey}|{g.RejectionReason}|{g.ResolvedMarkets.Count}|{g.MissingMarketIds.Count}|{missingIdsHash}";
                                var changed = !mismatchFingerprintByGroup.TryGetValue(g.GroupKey, out var prev) || prev != fp;
                                mismatchFingerprintByGroup[g.GroupKey] = fp;
                                var periodic = options.Logging.LogVerifiedMismatchEveryNCycles > 0 && mismatchCycle % options.Logging.LogVerifiedMismatchEveryNCycles == 0;
                                var should = options.Logging.DebugVerifiedMismatch || changed || periodic || !options.Logging.LogVerifiedMismatchOnChangeOnly;
                                if (should) Console.WriteLine($"[VERIFY_GROUP_MISMATCH] Group={g.GroupKey} RequiredMarkets={g.MarketIds.Count} FoundMarkets={g.ResolvedMarkets.Count} MissingMarkets={g.MissingMarketIds.Count} Source=FullDiscoveredPool MissingSample=[{string.Join(",", g.MissingMarketIds.Take(options.MultiOutcomeArbitrage.VerifiedGroupMismatchSampleSize))}] FoundSample=[{string.Join(",", g.ResolvedMarkets.Select(x=>x.id).Take(options.MultiOutcomeArbitrage.VerifiedGroupMismatchSampleSize))}]");
                            }
                            verifiedRejectedByUnresolvedGroup++;
                            if (ShouldQuietLog("multi-verified", "VERIFIED_STRATEGY_CLASSIFICATION", $"{g.GroupKey}|Unresolved|{g.RejectionReason}", LogImportance.Normal, "Unresolved", groupKey: g.GroupKey, everyNCycles: options.Logging.LogVerifiedScanEveryNCycles, maxPerHour: options.Logging.MaxMultiVerifiedScanLogsPerHour))
                                Console.WriteLine($"[VERIFIED_STRATEGY_CLASSIFICATION] Group={g.GroupKey} ActiveNet=0 RawNet=0 AlternateProfileNet=0 ConservativeNet=0 CostProfile=Unknown Classification=Unresolved WouldOpenIfPaperEligible=false PaperEligible=false Reason={g.RejectionReason}");
                            groupDiagnostics.Add(new VerifiedGroupDiagnosticDto(g.GroupKey, g.MarketIds.Count, g.ResolvedMarkets.Count, g.MissingMarketIds.Count, g.ValidationStatus, g.RejectionReason, null, 0, 0, g.MissingMarketIds.Take(5).ToArray(), g.MissingConditionIds.Take(5).ToArray()));
                            continue;
                        }
                        if (g.MissingMarketIds.Count > 0 && options.MultiOutcomeArbitrage.AllowPartialVerifiedGroupEvaluation && !options.MultiOutcomeArbitrage.RequireExactOutcomeCount)
                        {
                            Console.WriteLine($"[VERIFY_GROUP_PARTIAL] Group={g.GroupKey} Expected={g.MarketIds.Count} Resolved={g.ResolvedMarkets.Count} Missing={g.MissingMarketIds.Count} EvaluatingPartial=true");
                        }
                        verifiedEvaluated++;
                        var markets = g.ResolvedMarkets.Take(options.MultiOutcomeArbitrage.MaxVerifiedGroupLegs).ToList();
                        if (markets.Count < options.MultiOutcomeArbitrage.MinResolvedMarketsForVerifiedGroup)
                        {
                            groupDiagnostics.Add(new VerifiedGroupDiagnosticDto(g.GroupKey, g.MarketIds.Count, g.ResolvedMarkets.Count, g.MissingMarketIds.Count, "Rejected", "InsufficientResolvedMarkets", null, 0, 0, g.MissingMarketIds.Take(5).ToArray(), g.MissingConditionIds.Take(5).ToArray()));
                            continue;
                        }
                        if (options.MultiOutcomeArbitrage.VerifiedGroupOrderbookPrefetchEnabled)
                            await orderbookService.PrefetchBinarySnapshotsAsync(markets.Take(options.MultiOutcomeArbitrage.MaxVerifiedGroupOrderbookRequestsPerCycle).ToList(), stoppingToken);
                        var resolvedNoAsks = new List<ResolvedNoAsk>();
                        foreach (var m in markets.Take(options.MultiOutcomeArbitrage.MaxVerifiedGroupOrderbookRequestsPerCycle))
                        {
                            var tokens = VerifiedGroupPricingService.ResolveBinaryTokens(m);
                            if (orderbookService.GetStats().OrderbookCircuitBreakerActive)
                            {
                                resolvedNoAsks.Add(ResolvedNoAsk.Fail(m.id, m.conditionId, tokens.NoTokenId, "CircuitBreakerActive"));
                                continue;
                            }
                            if (orderbookService.IsMarketOrderbookQuarantined(m.id))
                            {
                                resolvedNoAsks.Add(ResolvedNoAsk.Fail(m.id, m.conditionId, tokens.NoTokenId, "MarketOrderbookQuarantined"));
                                continue;
                            }
                            var s = await orderbookService.GetBinarySnapshotAsync(m, stoppingToken);
                            if ((tokens.YesTokenId is not null && orderbookService.IsTokenQuarantined(tokens.YesTokenId)) || (tokens.NoTokenId is not null && orderbookService.IsTokenQuarantined(tokens.NoTokenId)))
                                resolvedNoAsks.Add(ResolvedNoAsk.Fail(m.id, m.conditionId, tokens.NoTokenId, "TokenQuarantined"));
                            else
                            {
                                var noAskResolution = VerifiedGroupPricingService.ResolveNoAsk(m, s, DateTime.UtcNow, options.MultiOutcomeArbitrage.VerifiedGroupOrderbookMaxAgeMs);
                                if (noAskResolution.FailureReason == "OrderbookFetchFailed") noAskResolution = noAskResolution with { FailureReason = "OrderbookUnavailable" };
                                if (noAskResolution.FailureReason == "EmptyBook") noAskResolution = noAskResolution with { FailureReason = "EmptyBook" };
                                resolvedNoAsks.Add(noAskResolution);
                            }
                        }
                        var missingNoAskLegs = resolvedNoAsks.Where(x => !x.NoAsk.HasValue).ToList();
                        var noAskResolvedCount = resolvedNoAsks.Count - missingNoAskLegs.Count;
                        if (missingNoAskLegs.Count > 0)
                        {
                            skipReason = "MissingNoAsk";
                            verifiedPricingCycle++;
                            var missingReasonBreakdown = missingNoAskLegs
                                .GroupBy(x => x.FailureReason ?? "Unknown", StringComparer.OrdinalIgnoreCase)
                                .ToDictionary(x => x.Key, x => x.Count(), StringComparer.OrdinalIgnoreCase);
                            var pricingFingerprint = $"{g.GroupKey}|{resolvedNoAsks.Count}|{noAskResolvedCount}|{missingNoAskLegs.Count}|{string.Join(",", missingReasonBreakdown.OrderBy(x => x.Key).Select(x => x.Key + ":" + x.Value))}|{string.Join(",", missingNoAskLegs.Select(x => x.MarketId).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}";
                            var shouldLogPricing = !verifiedPricingLastFingerprint.TryGetValue(g.GroupKey, out var prevPricing)
                                || prevPricing != pricingFingerprint
                                || (options.Logging.LogVerifiedGroupPricingEveryNCycles > 0 && verifiedPricingCycle % options.Logging.LogVerifiedGroupPricingEveryNCycles == 0);
                            verifiedPricingLastFingerprint[g.GroupKey] = pricingFingerprint;
                            if (shouldLogPricing)
                            {
                                var breakdownText = string.Join(",", missingReasonBreakdown.OrderBy(x => x.Key).Select(x => $"{x.Key}:{x.Value}"));
                                var topReason = missingReasonBreakdown.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault().Key ?? "MissingNoAsk";
                                Console.WriteLine($"[VERIFIED_GROUP_PRICING] Group={g.GroupKey} Legs={resolvedNoAsks.Count} NoAskResolved={noAskResolvedCount} MissingNoAsk={missingNoAskLegs.Count} MissingLiquidity=0 TopReason=MissingNoAsk_{topReason} ReasonBreakdown={{{breakdownText}}}");
                                Console.WriteLine($"[VERIFIED_GROUP_MISSING_NO_ASK] Group={g.GroupKey} Missing={missingNoAskLegs.Count} Samples=[{string.Join(", ", missingNoAskLegs.Take(5).Select(x => $"{x.MarketId}:{x.FailureReason}"))}]");
                                if (missingReasonBreakdown.TryGetValue("OrderbookFetchFailed", out var unavailable) && unavailable > 0)
                                    Console.WriteLine($"[VERIFIED_GROUP_ORDERBOOK_UNAVAILABLE] Group={g.GroupKey} MissingNoAsk={unavailable} Reason=OrderbookFetchFailed Action=MonitorOnly");
                            }
                            var pricedLegs = resolvedNoAsks.Where(x => x.NoAsk.HasValue).ToList();
                            var missingIds = missingNoAskLegs.Select(x => x.MarketId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                            var suggestedMarketIds = markets.Where(x => !missingIds.Contains(x.id) && x.active != false).Select(x => x.id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                            var suggestedConditionIds = markets.Where(x => !missingIds.Contains(x.id) && x.active != false && !string.IsNullOrWhiteSpace(x.conditionId)).Select(x => x.conditionId!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                            var fetchFailureOnly = missingReasonBreakdown.Count == 1 && missingReasonBreakdown.ContainsKey("OrderbookFetchFailed");
                            var suggestion = options.MultiOutcomeReview.IncludeSuggestedPrunedAllowlist && !fetchFailureOnly ? new { enabled = true, groupKey = g.GroupKey, title = g.Title, verificationStatus = "Verified", groupType = "MutuallyExclusiveWinner", allowedStrategy = "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", marketIds = suggestedMarketIds, conditionIds = suggestedConditionIds, requiredOutcomeCount = suggestedMarketIds.Length, requireExactOutcomeCount = false, suggestionReason = "MissingNoAsk legs excluded", settlementNotes = "Pruned to priced mutually exclusive subset. Missing NO ask leg excluded." } : null;
                            var missingMarketIds = missingNoAskLegs.Select(x => x.MarketId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                            var currentLegList = resolvedNoAsks.Select(x => new VerifiedPruneDryRunLeg(x.MarketId, x.ConditionId, x.NoTokenId, x.NoAsk, x.NoAskQuantity, x.Source, x.FailureReason)).ToArray();
                            var suggestedPrunedLegList = pricedLegs.Select(x => new VerifiedPruneDryRunLeg(x.MarketId, x.ConditionId, x.NoTokenId, x.NoAsk, x.NoAskQuantity, x.Source, x.FailureReason)).ToArray();
                            var removedLegMetadata = missingNoAskLegs.Select(x => new VerifiedPruneDryRunLeg(x.MarketId, x.ConditionId, x.NoTokenId, x.NoAsk, x.NoAskQuantity, x.Source, x.FailureReason)).ToArray();
                            var pruneActiveProfileName = options.MultiOutcomeArbitrage.CostProfiles.ActiveProfile;
                            if (!options.MultiOutcomeArbitrage.CostProfiles.Profiles.TryGetValue(pruneActiveProfileName, out var pruneActiveProfile)) pruneActiveProfile = options.MultiOutcomeArbitrage.CostProfiles.Profiles["Conservative"];
                            var originalFormula = VerifiedBasketFormulaService.Evaluate(resolvedNoAsks, pruneActiveProfile.FeePerLeg, pruneActiveProfile.SlippageBufferPerLeg, pruneActiveProfile.SafetyBufferPerGroup, options.MultiOutcomeArbitrage.RequireAllNoPrices);
                            var prunedFormula = VerifiedBasketFormulaService.Evaluate(pricedLegs, pruneActiveProfile.FeePerLeg, pruneActiveProfile.SlippageBufferPerLeg, pruneActiveProfile.SafetyBufferPerGroup, options.MultiOutcomeArbitrage.RequireAllNoPrices);
                            var originalRawFormula = VerifiedBasketFormulaService.Evaluate(resolvedNoAsks, 0m, 0m, 0m, options.MultiOutcomeArbitrage.RequireAllNoPrices);
                            var prunedRawFormula = VerifiedBasketFormulaService.Evaluate(pricedLegs, 0m, 0m, 0m, options.MultiOutcomeArbitrage.RequireAllNoPrices);
                            var wouldBecomeActivePositive = prunedFormula.IsValid && prunedFormula.NetEdge >= options.VerifiedBasketArb.MinNetEdgePerBasket;
                            var pruneEligibility = VerifiedPaperEligibilityDryRun.Evaluate(new VerifiedPaperEligibilityInput(g.GroupKey, prunedFormula.NetEdge, prunedFormula.NetEdge, prunedRawFormula.NetEdge, prunedFormula.NetEdge, 0m, 0m, StabilityPassed: wouldBecomeActivePositive, RiskPassed: true, FillPassed: true, DepthPassed: true, MissingNoAsk: false, OrderbookUnavailable: false, CostProfilePassed: prunedFormula.IsValid, Mode: StrategyMode.DiagnosticsOnly));
                            var pruneDryRun = new { result = new VerifiedPruneDryRunResult(g.GroupKey, missingMarketIds, resolvedNoAsks.Count, pricedLegs.Count, originalFormula.NetEdge, prunedFormula.NetEdge, originalRawFormula.NetEdge, prunedRawFormula.NetEdge, wouldBecomeActivePositive, pruneEligibility.WouldOpenIfPaperEligible, "ReviewOnly", false, currentLegList, suggestedPrunedLegList, removedLegMetadata), beforeAfterCostProfileComparison = new { activeProfile = pruneActiveProfileName, original = originalFormula, pruned = prunedFormula } };
                            if (shouldLogPricing && suggestion is not null) Console.WriteLine(pruneDryRun.result.ToLogLine());
                            verifiedPricingExport.Add(new
                            {
                                groupKey = g.GroupKey,
                                totalLegs = resolvedNoAsks.Count,
                                noAskResolvedCount = pricedLegs.Count,
                                missingNoAskCount = missingNoAskLegs.Count,
                                pricedLegs = pricedLegs.Select(x => new { marketId = x.MarketId, conditionId = x.ConditionId, noAsk = x.NoAsk, noAskQuantity = x.NoAskQuantity, noAskSource = x.Source, yesBid = x.YesBid, yesBidQuantity = x.YesBidQuantity, noTokenId = x.NoTokenId, priceResolutionFailureReason = x.FailureReason }).ToArray(),
                                missingPriceLegs = missingNoAskLegs.Select(x => new { marketId = x.MarketId, conditionId = x.ConditionId, noAsk = x.NoAsk, noAskQuantity = x.NoAskQuantity, noAskSource = x.Source, yesBid = x.YesBid, yesBidQuantity = x.YesBidQuantity, noTokenId = x.NoTokenId, priceResolutionFailureReason = x.FailureReason }).ToArray(),
                                missingNoAskReasonBreakdown = missingReasonBreakdown,
                                pricingHealthCategory = fetchFailureOnly ? "PricingUnavailable" : "ReviewOnly",
                                suggestedPrunedAllowlistTemplate = suggestion,
                                pruneDryRun
                            });
                            if (shouldLogPricing && suggestion is not null) Console.WriteLine($"[VERIFIED_GROUP_PRICING_SUGGESTION] Group={g.GroupKey} MissingNoAsk={missingNoAskLegs.Count} SuggestedPrunedLegs={suggestedMarketIds.Length}");
                            var missingSkipReason = fetchFailureOnly ? "OrderbookFetchFailed" : missingReasonBreakdown.ContainsKey("MarketOrderbookQuarantined") ? "MissingNoAsk_MarketOrderbookQuarantined" : missingReasonBreakdown.ContainsKey("TokenQuarantined") ? "MissingNoAsk_TokenQuarantined" : missingReasonBreakdown.ContainsKey("CircuitBreakerActive") ? "MissingNoAsk_CircuitBreakerActive" : missingReasonBreakdown.ContainsKey("OrderbookUnavailable") ? "MissingNoAsk_OrderbookUnavailable" : missingReasonBreakdown.ContainsKey("InvalidToken") ? "InvalidToken" : missingReasonBreakdown.ContainsKey("EmptyBook") ? "MissingNoAsk_EmptyBook" : "MissingNoAsk";
                            verifiedPricingBlockedByMissingNoAsk++;
                            verifiedPricingBlockedByOrderbookUnavailable += missingReasonBreakdown.GetValueOrDefault("OrderbookFetchFailed") + missingReasonBreakdown.GetValueOrDefault("OrderbookUnavailable");
                            verifiedPricingBlockedByCircuitBreakerActive += missingReasonBreakdown.GetValueOrDefault("CircuitBreakerActive");
                            verifiedPricingBlockedByMarketOrderbookQuarantined += missingReasonBreakdown.GetValueOrDefault("MarketOrderbookQuarantined");
                            verifiedPricingBlockedByTokenQuarantined += missingReasonBreakdown.GetValueOrDefault("TokenQuarantined") + missingReasonBreakdown.GetValueOrDefault("QuarantinedToken");
                            verifiedPricingBlockedByQuarantinedToken += missingReasonBreakdown.GetValueOrDefault("QuarantinedToken") + missingReasonBreakdown.GetValueOrDefault("TokenQuarantined") + missingReasonBreakdown.GetValueOrDefault("MarketOrderbookQuarantined");
                            verifiedPricingBlockedByEmptyBook += missingReasonBreakdown.GetValueOrDefault("EmptyBook");
                            if (ShouldQuietLog("multi-verified", "VERIFIED_STRATEGY_CLASSIFICATION", $"{g.GroupKey}|MissingNoAsk|{missingSkipReason}", LogImportance.Normal, "MissingNoAsk", groupKey: g.GroupKey, everyNCycles: options.Logging.LogVerifiedScanEveryNCycles, maxPerHour: options.Logging.MaxMultiVerifiedScanLogsPerHour))
                                Console.WriteLine($"[VERIFIED_STRATEGY_CLASSIFICATION] Group={g.GroupKey} ActiveNet=0 RawNet=0 AlternateProfileNet=0 ConservativeNet=0 CostProfile=Unknown Classification=MissingNoAsk WouldOpenIfPaperEligible=false PaperEligible=false Reason={missingSkipReason}");
                            groupDiagnostics.Add(new VerifiedGroupDiagnosticDto(g.GroupKey, g.MarketIds.Count, g.ResolvedMarkets.Count, g.MissingMarketIds.Count, "VerifiedResolved", missingSkipReason, null, missingNoAskLegs.Count, 0, missingNoAskLegs.Select(x => x.MarketId).Take(5).ToArray(), Array.Empty<string>()));
                            continue;
                        }
                        var activeCostProfileName = options.MultiOutcomeArbitrage.CostProfiles.ActiveProfile;
                        if (!options.MultiOutcomeArbitrage.CostProfiles.Profiles.TryGetValue(activeCostProfileName, out var activeCostProfile)) activeCostProfile = options.MultiOutcomeArbitrage.CostProfiles.Profiles["Conservative"];
                        var formula = VerifiedBasketFormulaService.Evaluate(resolvedNoAsks, activeCostProfile.FeePerLeg, activeCostProfile.SlippageBufferPerLeg, activeCostProfile.SafetyBufferPerGroup, options.MultiOutcomeArbitrage.RequireAllNoPrices);
                        var breakdown = VerifiedBasketDiagnostics.Compute(g.GroupKey, resolvedNoAsks.Count, formula, activeCostProfile.FeePerLeg, activeCostProfile.SlippageBufferPerLeg, options.MultiOutcomeArbitrage.NearExecutableCostReductionThreshold, options.MultiOutcomeArbitrage.FarFromExecutableCostReductionThreshold);
                        var screen = VerifiedBasketScreener.Evaluate(g.GroupKey, resolvedNoAsks, options.MultiOutcomeArbitrage);
                        verifiedScreenResults.Add(screen);
                        var initialClassification = VerifiedStrategyClassifier.Classify(screen, options.MultiOutcomeArbitrage);
                        if (initialClassification.ActiveConservativePositive) verifiedActivePositive++;
                        if (initialClassification.RawPositiveOnly) verifiedRawPositiveOnly++;
                        if (initialClassification.AlternateProfilePositive) verifiedAlternatePositive++;
                        var classificationFingerprint = $"{g.GroupKey}|{initialClassification.Classification}|active:{EdgeBucket(initialClassification.ActiveNet, 0.002m)}|raw:{EdgeBucket(initialClassification.RawNet, 0.002m)}|alt:{EdgeBucket(initialClassification.AlternateProfileNet, 0.002m)}|would:false";
                        if (ShouldQuietLog("multi-verified", "VERIFIED_STRATEGY_CLASSIFICATION", classificationFingerprint, LogImportance.Normal, $"{initialClassification.Classification}|{EdgeBucket(initialClassification.ActiveNet, 0.002m)}", groupKey: g.GroupKey, everyNCycles: options.Logging.LogVerifiedScanEveryNCycles, maxPerHour: options.Logging.MaxMultiVerifiedScanLogsPerHour))
                            Console.WriteLine($"[VERIFIED_STRATEGY_CLASSIFICATION] Group={g.GroupKey} ActiveNet={initialClassification.ActiveNet:0.####} RawNet={initialClassification.RawNet:0.####} AlternateProfileNet={initialClassification.AlternateProfileNet:0.####} ConservativeNet={initialClassification.ConservativeNet:0.####} CostProfile={initialClassification.CostProfile} Classification={initialClassification.Classification} WouldOpenIfPaperEligible=false PaperEligible=false Reason={initialClassification.Reason}");
                        basketCostDiagnostics.Add(new VerifiedBasketCostBreakdownDto(g.GroupKey, resolvedNoAsks.Count, formula.GuaranteedPayout, formula.NoAskSum, formula.GrossEdge, formula.Fees, formula.Slippage, formula.SafetyBuffer, formula.NetEdge, breakdown.CurrentTotalCosts, breakdown.CostReductionNeeded, breakdown.Classification, breakdown.DominantCostComponent, new VerifiedBasketSensitivityDto(breakdown.SensitivityScenarios.Actual, breakdown.SensitivityScenarios.ZeroFees, breakdown.SensitivityScenarios.ZeroSlippage, breakdown.SensitivityScenarios.ZeroFeesZeroSlippage, breakdown.SensitivityScenarios.RawOnly, breakdown.SensitivityScenarios.HalfFeesHalfSlippage), new VerifiedBasketBreakEvenDto(formula.GrossEdge, breakdown.CurrentTotalCosts, breakdown.CostReductionNeeded, breakdown.BreakEvenFeeLimit, breakdown.BreakEvenSlippageLimit)));
                        var currentExec = formula.NetEdge > options.MultiOutcomeArbitrage.MinMultiOutcomeEdge;
                        var fingerprint = $"{formula.GrossEdge}|{formula.Fees}|{formula.Slippage}|{formula.SafetyBuffer}|{formula.NetEdge}";
                        verifiedBasketCycle++;
                        var shouldLog = !options.Logging.LogVerifiedBasketOnlyOnChange
                            || !verifiedBasketLastFingerprint.TryGetValue(g.GroupKey, out var prevFingerprint) || prevFingerprint != fingerprint
                            || !verifiedBasketLastExecutable.TryGetValue(g.GroupKey, out var prevExec) || prevExec != currentExec
                            || (options.Logging.LogVerifiedBasketEveryNCycles > 0 && verifiedBasketCycle % options.Logging.LogVerifiedBasketEveryNCycles == 0);
                        verifiedBasketLastFingerprint[g.GroupKey] = fingerprint;
                        verifiedBasketLastExecutable[g.GroupKey] = currentExec;
                        if (shouldLog && options.Logging.LogVerifiedBasketDetails)
                        {
                            Console.WriteLine($"[VERIFIED_BASKET_EDGE] Group={g.GroupKey} Legs={resolvedNoAsks.Count} GuaranteedPayout={formula.GuaranteedPayout} NoAskSum={formula.NoAskSum} MinNoAsk={formula.MinNoAsk} MaxNoAsk={formula.MaxNoAsk} AverageNoAsk={formula.AverageNoAsk} GrossEdge={formula.GrossEdge} Fees={formula.Fees} Slippage={formula.Slippage} Safety={formula.SafetyBuffer} NetEdge={formula.NetEdge} ExecutableQty=1 ExpectedProfit={formula.NetEdge} formulaVersion=v2");
                            Console.WriteLine($"[VERIFIED_BASKET_COSTS] Group={g.GroupKey} GrossEdge={breakdown.GrossEdge} Fees={breakdown.Fees} Slippage={breakdown.Slippage} Safety={breakdown.Safety} NetEdge={breakdown.NetEdge} DominantCost={breakdown.DominantCostComponent} BreakEvenFeeLimit={breakdown.BreakEvenFeeLimit} BreakEvenSlippageLimit={breakdown.BreakEvenSlippageLimit} RequiredCostReduction={breakdown.CostReductionNeeded}");
                            Console.WriteLine($"[VERIFIED_FEE_DIAG] Group={g.GroupKey} Legs={resolvedNoAsks.Count} FeeModel=PerLeg FeePerLeg={options.MultiOutcomeArbitrage.FeePerLeg} PercentageFee=0 FixedFee=0 FeeAppliedPerLeg={options.MultiOutcomeArbitrage.FeePerLeg} TotalFees={formula.Fees} Source=Config");
                            var slipPct = formula.GrossEdge == 0 ? 0 : (formula.Slippage / formula.GrossEdge) * 100m;
                            Console.WriteLine($"[VERIFIED_SLIPPAGE_DIAG] Group={g.GroupKey} Legs={resolvedNoAsks.Count} SlippagePerLeg={options.MultiOutcomeArbitrage.SlippageBufferPerLeg} TotalSlippage={formula.Slippage} GrossEdge={formula.GrossEdge} SlippageConsumes={slipPct:0.##}% Source=Config");
                            Console.WriteLine($"[VERIFIED_BREAK_EVEN] Group={g.GroupKey} MaxTotalCosts={formula.GrossEdge} CurrentCosts={breakdown.CurrentTotalCosts} ReductionNeeded={breakdown.CostReductionNeeded} MaxFeePerLegAtCurrentSlippage={breakdown.BreakEvenFeeLimit} MaxSlippagePerLegAtCurrentFees={breakdown.BreakEvenSlippageLimit}");
                            if (options.MultiOutcomeArbitrage.EnableSensitivityDiagnostics)
                                Console.WriteLine($"[VERIFIED_SENSITIVITY] Group={g.GroupKey} Actual={breakdown.SensitivityScenarios.Actual} ZeroFees={breakdown.SensitivityScenarios.ZeroFees} ZeroSlippage={breakdown.SensitivityScenarios.ZeroSlippage} ZeroFeesZeroSlippage={breakdown.SensitivityScenarios.ZeroFeesZeroSlippage} RawOnly={breakdown.SensitivityScenarios.RawOnly} HalfCosts={breakdown.SensitivityScenarios.HalfFeesHalfSlippage}");
                        }
                        if (formula.FormulaWarnings.Count > 0)
                            Console.WriteLine($"[FORMULA_WARNING] {string.Join(" | ", formula.FormulaWarnings)}");
                        if (!formula.IsValid)
                        {
                            skipReason = formula.SkipReason is "InvalidGuaranteedPayoutFormula" or "InvalidPriceNormalization" or "InvalidBasketCost" ? "FormulaOrNormalizationError" : formula.SkipReason;
                            pricingDiagnostics.Add(new VerifiedGroupPricingDto(g.GroupKey, resolvedNoAsks.Count, formula.GuaranteedPayout, formula.NoAskSum, formula.MinNoAsk, formula.MaxNoAsk, formula.AverageNoAsk, formula.GrossEdge, formula.Fees, formula.Slippage, formula.SafetyBuffer, formula.NetEdge, 0, 0, skipReason, formula.FormulaWarnings));
                            groupDiagnostics.Add(new VerifiedGroupDiagnosticDto(g.GroupKey, g.MarketIds.Count, g.ResolvedMarkets.Count, g.MissingMarketIds.Count, "VerifiedResolved", skipReason, null, 0, 0, Array.Empty<string>(), Array.Empty<string>()));
                            continue;
                        }
                        var edge = formula.NetEdge;
                        var trackedState = stability.Track(g.GroupKey, screen, options.RuntimeState.MaxVerifiedBasketEdgeHistoryPerGroup, executionOptions.RequiredConsecutiveExecutableScans, executionOptions.MinStableNetEdgePerBasket, executionOptions.MaxNetEdgeVolatility);
                        basketStateByGroup[g.GroupKey] = trackedState;
                        var isExec = edge >= options.VerifiedBasketArb.MinNetEdgePerBasket;
                        var strategyName = "BUY_ALL_NO_MUTUALLY_EXCLUSIVE";
                        var hasOpenPosition = positionBook.GetOpenPositions().Any(x => x.GroupKey.Equals(g.GroupKey, StringComparison.OrdinalIgnoreCase) && x.Strategy == strategyName);
                        if (hasOpenPosition)
                        {
                            paperOpenedCount++;
                            suppressedDuplicateCount++;
                            stability.MarkSuppressedDuplicate(g.GroupKey);
                            var suppressedCount = verifiedExecution.MarkDuplicateSuppressed(g.GroupKey);
                            var duplicateId = $"verified-{g.GroupKey}-{DateTime.UtcNow:yyyyMMddHHmmss}";
                            verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, duplicateId, g.GroupKey, strategyName, "DuplicateSuppressed", "Suppressed", "DuplicateOpenPosition", formula.NetEdge, 0m, formula.NoAskSum, 0m, $"Count={suppressedCount}"));
                            state.AddOpportunity(new OpportunityDto($"{duplicateId}-dup-{suppressedCount}", DateTime.UtcNow, 1, strategyName, g.GroupKey, g.Title, "NO", formula.NetEdge, 0m, 0m, formula.GuaranteedPayout, 0m, false, "ALREADY_OPEN", "AlreadyOpen", state.NextSeq()));
                            if (options.Logging.LogExecutionSuppressionSummary && (suppressedCount == 1 || options.Logging.LogDuplicatePositionEveryNCycles <= 1 || suppressedCount % options.Logging.LogDuplicatePositionEveryNCycles == 0))
                                Console.WriteLine($"[VERIFIED_EXECUTION_SUPPRESSED] Group={g.GroupKey} Reason=DuplicateOpenPosition Count={suppressedCount} LastNetEdge={formula.NetEdge}");
                        }
                        if (isExec && !hasOpenPosition)
                        {
                            verifiedExecutable++;
                            activeExecutable++;
                            var maxLiquidityQty = Math.Max(options.MultiOutcomeArbitrage.MinExecutableQty, resolvedNoAsks.Min(x => x.NoAskQuantity ?? 0m));
                            var maxLiquidityExpectedProfit = maxLiquidityQty * formula.NetEdge;
                            var questionByMarket = GroupKeyDictionaryBuilder.BuildUniqueByGroupKey(markets.Where(m => !string.IsNullOrWhiteSpace(m.id)), m => m.id, "Scanner.QuestionByMarket", DuplicateGroupKeyPolicy.KeepLatest)
                                .ToDictionary(kv => kv.Key, kv => kv.Value.question ?? kv.Value.id, StringComparer.OrdinalIgnoreCase);
                            var legs = resolvedNoAsks.Select(x => new VerifiedMultiOutcomeOpportunityLeg(x.MarketId, x.ConditionId ?? x.MarketId, questionByMarket.TryGetValue(x.MarketId, out var q) ? q : x.MarketId, "NO", x.NoTokenId ?? x.MarketId, x.NoAsk ?? 0m, x.NoAskQuantity ?? 0m, x.Source, maxLiquidityQty, maxLiquidityQty * (x.NoAsk ?? 0m))).ToArray();
                            var opp = new VerifiedMultiOutcomeOpportunity($"verified-{g.GroupKey}-{DateTime.UtcNow:yyyyMMddHHmmss}", strategyName, g.GroupKey, g.Title, "Verified", legs.Length, formula.GuaranteedPayout, formula.NoAskSum, formula.GrossEdge, formula.NetEdge, activeCostProfileName, maxLiquidityQty, maxLiquidityExpectedProfit, options.MultiOutcomeArbitrage.MaxNotionalPerGroup, maxLiquidityQty * formula.NoAskSum, "PaperExecutable", legs);
                            promotedVerifiedOpportunities.Add(opp);

                            verifiedExecution.AuditQuiet(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "Detected", "Ok", "VerifiedExecutable", opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, ""), options.Logging.MaxVerifiedArbDetectedAuditPerHour, true, 100);
                            verifiedExecution.AuditQuiet(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "PromotedToOpportunity", "Ok", "Actionable", opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, ""), options.Logging.MaxVerifiedArbDetectedAuditPerHour, true, 100);
                            var st = stability.State(opp.GroupKey);
                            var edgeDecision = edgeStabilityLogThrottle.Evaluate(opp.GroupKey, st, stability.Consecutive(opp.GroupKey), executionOptions.RequiredConsecutiveExecutableScans, opp.NetEdge, stability.StateAge(opp.GroupKey), stability.LastResetReason(opp.GroupKey));
                            if (st == VerifiedBasketState.EdgeExecutablePending)
                            {
                                verifiedExecution.AuditQuiet(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "EdgeStabilityPending", "Pending", "WaitingForStableEdge", opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, $"ConsecutiveEdgeScans={edgeDecision.ConsecutiveEdgeScans};RequiredEdgeScans={edgeDecision.RequiredEdgeScans};StateAgeSeconds={edgeDecision.StateAge.TotalSeconds:0};LastResetReason={edgeDecision.LastResetReason}"), options.Logging.MaxVerifiedPretradeBlockedAuditPerHour, options.Logging.SuppressRepeatedVerifiedPretradeBlockedAudit, 100);
                                if (edgeDecision.LogPending) Console.WriteLine($"[EDGE_STABILITY_PENDING] Group={opp.GroupKey} Consecutive={edgeDecision.ConsecutiveEdgeScans} Required={edgeDecision.RequiredEdgeScans} NetEdge={opp.NetEdge:0.####} StateAgeSeconds={edgeDecision.StateAge.TotalSeconds:0} LastResetReason={edgeDecision.LastResetReason}");
                                if (edgeDecision.LogStalled) Console.WriteLine($"[EDGE_STABILITY_STALLED] Group={opp.GroupKey} Reason={edgeDecision.LastResetReason} Consecutive={edgeDecision.ConsecutiveEdgeScans} Required={edgeDecision.RequiredEdgeScans} StateAgeSeconds={edgeDecision.StateAge.TotalSeconds:0}");
                            }
                            if (st == VerifiedBasketState.EdgeStable)
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "EdgeStable", "Ok", "EdgeStable", opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, ""));
                            if (st != VerifiedBasketState.EdgeExecutablePending && ShouldQuietLog("multi-verified", "VERIFIED_ARB_DETECTED", $"{opp.GroupKey}|{Math.Round(opp.NetEdge, 4)}|{st}", LogImportance.Important, $"{Math.Round(opp.NetEdge, 4)}|{st}", opp.GroupKey, everyNCycles: 100, maxPerHour: options.Logging.MaxVerifiedArbDetectedAuditPerHour)) Console.WriteLine($"[VERIFIED_ARB_DETECTED] Group={opp.GroupKey} NetEdge={opp.NetEdge} MaxLiquidityQty={maxLiquidityQty} MaxLiquidityExpectedProfit={maxLiquidityExpectedProfit} Status=RequiresSizing");
                            if (options.EnablePaperTrading && options.MultiOutcomeArbitrage.Enabled && options.ExecutionMode == "PAPER" && st is not (VerifiedBasketState.EdgeStable or VerifiedBasketState.ExecutionReadinessPending or VerifiedBasketState.ExecutionStable))
                            {
                                var stableEnough = edgeDecision.ConsecutiveEdgeScans >= edgeDecision.RequiredEdgeScans;
                                var blocker = stableEnough ? (st == VerifiedBasketState.DiagnosticsOnly ? "DiagnosticsOnlyProfile" : "WaitingForExecutionReadiness") : "WaitingForStableEdge";
                                var resetDetail = stableEnough ? $"CurrentBlocker={blocker}" : $"LastResetReason={edgeDecision.LastResetReason}";
                                verifiedExecution.AuditQuiet(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "PreTradeBlocked", "Blocked", blocker, opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, $"ConsecutiveEdgeScans={edgeDecision.ConsecutiveEdgeScans};RequiredEdgeScans={edgeDecision.RequiredEdgeScans};StateAgeSeconds={edgeDecision.StateAge.TotalSeconds:0};{resetDetail}"), options.Logging.MaxVerifiedPretradeBlockedAuditPerHour, options.Logging.SuppressRepeatedVerifiedPretradeBlockedAudit, 100);
                                if ((st != VerifiedBasketState.EdgeExecutablePending || edgeDecision.LogPending || edgeDecision.LogStalled) && ShouldQuietLog("multi-verified", "VERIFIED_PRETRADE_BLOCKED", $"{opp.GroupKey}|{Math.Round(opp.NetEdge, 4)}|{blocker}|{st}|{resetDetail}", LogImportance.Important, $"{Math.Round(opp.NetEdge, 4)}|{blocker}", opp.GroupKey, everyNCycles: 100, maxPerHour: options.Logging.MaxVerifiedPretradeBlockedAuditPerHour)) Console.WriteLine($"[VERIFIED_PRETRADE_BLOCKED] Group={opp.GroupKey} Reason={blocker} State={st} ConsecutiveEdgeScans={edgeDecision.ConsecutiveEdgeScans} RequiredEdgeScans={edgeDecision.RequiredEdgeScans} StateAgeSeconds={edgeDecision.StateAge.TotalSeconds:0} {resetDetail}");
                                continue;
                            }
                            verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "ExecutionReadinessStarted", "Started", "SizingReadinessSample", opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, ""));
                            var readiness = stability.TrackExecutionReadiness(opp, executionOptions, hasOpenPosition, options.RuntimeState.MaxVerifiedBasketEdgeHistoryPerGroup);
                            var readinessState = readiness.State;
                            if (st != readinessState)
                            {
                                Console.WriteLine($"[VERIFIED_BASKET_STATE_CHANGE] Group={opp.GroupKey} From={st} To={readinessState} Reason=ExecutionReadiness");
                                basketStateByGroup[opp.GroupKey] = readinessState;
                            }
                            if (readiness.Reset)
                            {
                                var resetReason = readiness.NotReadyReason == "PlannedQtyBelowMinimum" ? "LiquidityDropped" : readiness.NotReadyReason ?? "ReadinessDropped";
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "ExecutionReadinessReset", "Reset", resetReason, opp.NetEdge, readiness.PlannedExpectedProfit, readiness.PlannedCost, readiness.PlannedQty, $"PreviousReady={readiness.PreviousReadyScans}"));
                                Console.WriteLine($"[EXECUTION_READINESS_RESET] Group={opp.GroupKey} Reason={resetReason} PreviousReady={readiness.PreviousReadyScans} CurrentPlannedQty={readiness.PlannedQty:0.####}");
                            }
                            if (!readiness.Ready)
                            {
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "ExecutionReadinessRejected", "Rejected", readiness.NotReadyReason ?? "NotReady", opp.NetEdge, readiness.PlannedExpectedProfit, readiness.PlannedCost, readiness.PlannedQty, $"MinQty={executionOptions.MinPlannedBasketQty}"));
                                verifiedRejectedByStability++;
                                Console.WriteLine($"[EXECUTION_READINESS_REJECTED] Group={opp.GroupKey} Reason={readiness.NotReadyReason} PlannedQty={readiness.PlannedQty:0.####} MinQty={executionOptions.MinPlannedBasketQty} MaxQtyByLiquidity={readiness.MaxQtyByLiquidity:0.####}");
                                verifiedExecution.AuditQuiet(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "PreTradeBlocked", "Blocked", "WaitingForExecutionReadiness", opp.NetEdge, readiness.PlannedExpectedProfit, readiness.PlannedCost, readiness.PlannedQty, ""), options.Logging.MaxVerifiedPretradeBlockedAuditPerHour, options.Logging.SuppressRepeatedVerifiedPretradeBlockedAudit, 100);
                                if (ShouldQuietLog("multi-verified", "VERIFIED_PRETRADE_BLOCKED", $"{opp.GroupKey}|{Math.Round(opp.NetEdge, 4)}|WaitingForExecutionReadiness|{st}", LogImportance.Important, $"{Math.Round(opp.NetEdge, 4)}|WaitingForExecutionReadiness", opp.GroupKey, everyNCycles: 100, maxPerHour: options.Logging.MaxVerifiedPretradeBlockedAuditPerHour)) Console.WriteLine($"[VERIFIED_PRETRADE_BLOCKED] Group={opp.GroupKey} Reason=WaitingForExecutionReadiness State={st}");
                                state.AddOpportunity(new OpportunityDto(opp.Id, DateTime.UtcNow, 1, opp.Strategy, opp.GroupKey, opp.Title, "NO", opp.NetEdge, readiness.PlannedExpectedProfit, readiness.PlannedCost, opp.GuaranteedPayout, readiness.PlannedQty, false, "WAITING_FOR_EXECUTION_READINESS", readiness.NotReadyReason, state.NextSeq()));
                                continue;
                            }
                            if (readinessState != VerifiedBasketState.ExecutionStable)
                            {
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "ExecutionReadinessPending", "Pending", "WaitingConsecutiveExecutionReadyScans", opp.NetEdge, readiness.PlannedExpectedProfit, readiness.PlannedCost, readiness.PlannedQty, $"ConsecutiveReady={readiness.ConsecutiveReadyScans};Required={readiness.RequiredConsecutiveReadyScans}"));
                                verifiedRejectedByStability++;
                                Console.WriteLine($"[EXECUTION_READINESS_PENDING] Group={opp.GroupKey} ConsecutiveReady={readiness.ConsecutiveReadyScans} Required={readiness.RequiredConsecutiveReadyScans} PlannedQty={readiness.PlannedQty:0.####} PlannedCost={readiness.PlannedCost:0.####} ExpectedProfit={readiness.PlannedExpectedProfit:0.####}");
                                verifiedExecution.AuditQuiet(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "PreTradeBlocked", "Blocked", "WaitingForExecutionReadiness", opp.NetEdge, readiness.PlannedExpectedProfit, readiness.PlannedCost, readiness.PlannedQty, ""), options.Logging.MaxVerifiedPretradeBlockedAuditPerHour, options.Logging.SuppressRepeatedVerifiedPretradeBlockedAudit, 100);
                                if (ShouldQuietLog("multi-verified", "VERIFIED_PRETRADE_BLOCKED", $"{opp.GroupKey}|{Math.Round(opp.NetEdge, 4)}|WaitingForExecutionReadiness|{st}", LogImportance.Important, $"{Math.Round(opp.NetEdge, 4)}|WaitingForExecutionReadiness", opp.GroupKey, everyNCycles: 100, maxPerHour: options.Logging.MaxVerifiedPretradeBlockedAuditPerHour)) Console.WriteLine($"[VERIFIED_PRETRADE_BLOCKED] Group={opp.GroupKey} Reason=WaitingForExecutionReadiness State={st}");
                                state.AddOpportunity(new OpportunityDto(opp.Id, DateTime.UtcNow, 1, opp.Strategy, opp.GroupKey, opp.Title, "NO", opp.NetEdge, readiness.PlannedExpectedProfit, readiness.PlannedCost, opp.GuaranteedPayout, readiness.PlannedQty, false, "WAITING_FOR_EXECUTION_READINESS", "WaitingForExecutionReadiness", state.NextSeq()));
                                continue;
                            }
                            verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "ExecutionReadinessStable", "Ok", "ExecutionStable", opp.NetEdge, readiness.PlannedExpectedProfit, readiness.PlannedCost, readiness.PlannedQty, $"ConsecutiveReady={readiness.ConsecutiveReadyScans};Required={readiness.RequiredConsecutiveReadyScans}"));
                            Console.WriteLine($"[EXECUTION_READINESS_STABLE] Group={opp.GroupKey} ConsecutiveReady={readiness.ConsecutiveReadyScans} Required={readiness.RequiredConsecutiveReadyScans} PlannedQty={readiness.PlannedQty:0.####} PlannedCost={readiness.PlannedCost:0.####} ExpectedProfit={readiness.PlannedExpectedProfit:0.####}");
                            var pre = verifiedExecution.Validate(opp, positionBook);
                            preTradeResults.Add(pre);
                            verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "SizingCalculated", "Ok", "PlannedSizing", pre.NetEdge, pre.ExpectedProfit, pre.EstimatedCost, pre.Quantity, ""));
                            state.AddOpportunity(new OpportunityDto(opp.Id, DateTime.UtcNow, 1, opp.Strategy, opp.GroupKey, opp.Title, "NO", pre.NetEdge, pre.ExpectedProfit, pre.EstimatedCost, opp.GuaranteedPayout, pre.Quantity, true, "ACTIONABLE", null, state.NextSeq()));
                            if (!pre.Approved)
                            {
                                verifiedRejectedByRisk++;
                                Console.WriteLine($"[VERIFIED_PRETRADE_REJECTED] Group={opp.GroupKey} Reason={pre.Reason} Cost={pre.EstimatedCost} MaxNotional={options.MultiOutcomeArbitrage.MaxNotionalPerGroup}");
                            }
                            else
                            {
                                Console.WriteLine($"[VERIFIED_PRETRADE_APPROVED] Group={opp.GroupKey} NetEdge={pre.NetEdge} Qty={pre.Quantity} EstimatedCost={pre.EstimatedCost} ExpectedProfit={pre.ExpectedProfit}");
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "DryRunOrderBuildStarted", "Started", "DryRunBuildStarted", pre.NetEdge, pre.ExpectedProfit, pre.EstimatedCost, pre.Quantity, ""));
                                var plan = dryRunBuilder.Build(opp, pre, executionOptions);
                                if (plan.Status == BasketOrderPlanStatus.Rejected)
                                {
                                    var reason = plan.ValidationErrors.FirstOrDefault() ?? "Unknown";
                                    verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "DryRunOrderPlanRejected", "Rejected", reason, pre.NetEdge, pre.ExpectedProfit, pre.EstimatedCost, pre.Quantity, ""));
                                    verifiedRejectedByCostProfile++;
                                    Console.WriteLine($"[DRY_RUN_ORDER_PLAN_REJECTED] Group={opp.GroupKey} Reason={reason}");
                                    continue;
                                }
                                verifiedExecution.RecordDryRunPlan(plan);
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "DryRunOrderPlanCreated", "Ok", "DryRunOnly", pre.NetEdge, pre.ExpectedProfit, pre.EstimatedCost, pre.Quantity, $"Orders={plan.Orders.Count}"));
                                Console.WriteLine($"[DRY_RUN_ORDER_PLAN_CREATED] Group={opp.GroupKey} Orders={plan.Orders.Count} Qty={pre.Quantity:0.####} TotalCost={plan.TotalEstimatedCost:0.####} ExpectedProfit={plan.ExpectedProfit:0.####} DryRunOnly=true");
                                var marketById = GroupKeyDictionaryBuilder.BuildUniqueByGroupKey(g.ResolvedMarkets.Where(x => !string.IsNullOrWhiteSpace(x.id)), x => x.id, "Scanner.ResolvedMarketsById", DuplicateGroupKeyPolicy.KeepLatest);
                                var snapshotsByMarket = new Dictionary<string, BinaryOrderBookSnapshot?>(StringComparer.OrdinalIgnoreCase);
                                foreach (var order in plan.Orders)
                                {
                                    if (marketById.TryGetValue(order.MarketId, out var market)) snapshotsByMarket[order.MarketId] = await orderbookService.GetBinarySnapshotAsync(market, stoppingToken);
                                    else snapshotsByMarket[order.MarketId] = null;
                                }
                                var booksByToken = GroupKeyDictionaryBuilder.BuildUniqueByGroupKey(plan.Orders.Where(o => !string.IsNullOrWhiteSpace(o.TokenId)), o => o.TokenId, "Scanner.DryRunOrdersByToken", DuplicateGroupKeyPolicy.KeepLatest)
                                            .ToDictionary(kv => kv.Key, kv => orderbookService.GetCachedOrderBookSnapshot(kv.Value.TokenId, kv.Value.MarketId), StringComparer.OrdinalIgnoreCase);
                                var fill = fillSimulator.Simulate(plan, booksByToken, snapshotsByMarket, executionOptions, profileUsed: activeCostProfileName, estimatedFees: formula.Fees, estimatedSlippage: formula.Slippage, safetyBuffer: formula.SafetyBuffer);
                                verifiedExecution.RecordFillSimulation(fill);
                                Console.WriteLine($"[DRY_RUN_FILL_SIMULATION] Group={opp.GroupKey} PlannedQty={fill.RequestedQty:0.####} FullyFillableQty={fill.FullyFillableQty:0.####} Status={fill.Status}");
                                if (fill.Status != FillSimulationStatus.FullyFillable)
                                {
                                    var reason = fill.Status == FillSimulationStatus.PartiallyFillable ? "PartialFillRisk" : fill.Status.ToString();
                                    verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "DryRunFillSimulationRejected", "Rejected", reason, pre.NetEdge, fill.EstimatedExpectedProfit, fill.EstimatedFilledCost, fill.SafeExecutableQty, $"FullyFillableQty={fill.FullyFillableQty};PlannedQty={fill.RequestedQty};WorstLeg={fill.WorstLeg}"));
                                    Console.WriteLine($"[DRY_RUN_FILL_REJECTED] Group={opp.GroupKey} Reason={reason} PlannedQty={fill.RequestedQty:0.####} FullyFillableQty={fill.FullyFillableQty:0.####} WorstLeg={fill.WorstLeg}");
                                }
                                else
                                {
                                    var simulatedNetEdge = fill.FillAdjustedNetEdgePerBasket;
                                    verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "DryRunFillSimulationPassed", "Ok", "FullyFillable", simulatedNetEdge, fill.FillAdjustedExpectedProfit, fill.EstimatedFilledCost, fill.SafeExecutableQty, $"Orders={fill.RequestedOrdersCount};FillAdjustedNetEdge={fill.FillAdjustedNetEdgePerBasket};FillAdjustedExpectedProfit={fill.FillAdjustedExpectedProfit}"));
                                    Console.WriteLine($"[DRY_RUN_FILL_SIMULATION_PASSED] Group={opp.GroupKey} Orders={plan.Orders.Count} PlannedQty={pre.Quantity:0.####} FullyFillableQty={fill.FullyFillableQty:0.####} PlannedNet={fill.PlannedNetEdgePerBasket:0.####} FillAdjustedNet={fill.FillAdjustedNetEdgePerBasket:0.####} PlannedExpectedProfit={fill.PlannedExpectedProfit:0.####} FillAdjustedExpectedProfit={fill.FillAdjustedExpectedProfit:0.####} EstimatedCost={fill.EstimatedFilledCost:0.####}");
                                }
                                PaperPosition? opened = null;
                                var openPositionsForGate = positionBook.GetOpenPositions();
                                var paperGate = new PaperPreTradeGate(options).Validate(
                                    new PaperPreTradeOpportunity(opp.Strategy, opp.GroupKey, PaperStrategyKind.VerifiedBasket, options.VerifiedBasketArb.PaperOnly, fill.EstimatedFilledCost, fill.FillAdjustedExpectedProfit, true, true, fill.Status == FillSimulationStatus.FullyFillable, true, openPositionsForGate.Any(p => p.GroupKey.Equals(opp.GroupKey, StringComparison.OrdinalIgnoreCase) && p.Strategy.Equals(opp.Strategy, StringComparison.OrdinalIgnoreCase)), false),
                                    new PaperAccountSnapshotForGate(paper.Balance, openPositionsForGate.Sum(p => p.TotalCost), openPositionsForGate.Count, openPositionsForGate.GroupBy(p => p.Strategy, StringComparer.OrdinalIgnoreCase).ToDictionary(gp => gp.Key, gp => gp.Count(), StringComparer.OrdinalIgnoreCase), paper.HourlyOpenCount));
                                var verifiedPaperConfig = StrategyOrchestrator.ResolveConfig(options.Strategies, "VerifiedMultiOutcome");
                                var verifiedMode = verifiedPaperConfig.Mode;
                                var eligibilityInput = new VerifiedPaperEligibilityInput(opp.GroupKey, opp.NetEdge, opp.NetEdge, formula.GrossEdge, screen.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m, fill.SafeExecutableQty, fill.EstimatedFilledCost, StabilityPassed: true, RiskPassed: paperGate.Approved, FillPassed: fill.Status == FillSimulationStatus.FullyFillable, DepthPassed: fill.Status != FillSimulationStatus.MissingOrderbook, MissingNoAsk: false, OrderbookUnavailable: fill.Status == FillSimulationStatus.MissingOrderbook, CostProfilePassed: true, Mode: verifiedMode);
                                var eligibility = VerifiedPaperEligibilityDryRun.Evaluate(eligibilityInput);
                                var wouldOpenIfPaperEligible = eligibility.WouldOpenIfPaperEligible;
                                if (wouldOpenIfPaperEligible) verifiedWouldOpenIfPaperEligible++;
                                if (eligibility.BlockedReason == VerifiedPaperBlockedReason.Risk) verifiedRejectedByRisk++;
                                else if (eligibility.BlockedReason == VerifiedPaperBlockedReason.Fill) verifiedBlockedByFill++;
                                else if (eligibility.BlockedReason == VerifiedPaperBlockedReason.Depth) verifiedBlockedByDepth++;
                                else if (eligibility.BlockedReason == VerifiedPaperBlockedReason.CostProfile) verifiedRejectedByCostProfile++;
                                else if (eligibility.BlockedReason == VerifiedPaperBlockedReason.MissingNoAsk) verifiedPricingBlockedByMissingNoAsk++;
                                else if (eligibility.BlockedReason == VerifiedPaperBlockedReason.Unknown && !VerifiedPaperEligibilityDryRun.CanOpenPaper(eligibility, verifiedPaperConfig)) verifiedBlockedByUnknown++;
                                if (ShouldQuietLog("multi-verified", "VERIFIED_PAPER_ELIGIBILITY_DRY_RUN", $"{opp.GroupKey}|{eligibility.BlockedReason}|{wouldOpenIfPaperEligible}|{Math.Round(opp.NetEdge,4)}", LogImportance.Important, $"{eligibility.BlockedReason}|{wouldOpenIfPaperEligible}", opp.GroupKey, everyNCycles: 1, maxPerHour: options.Logging.MaxVerifiedPretradeBlockedAuditPerHour)) Console.WriteLine(eligibility.ToLogLine(eligibilityInput));
                                if (!paperGate.Approved)
                                {
                                    state.RecordPaperPretradeReject(paperGate.Reason);
                                    if (paperGate.Reason is "DuplicateOpenPosition" or "CooldownActive" or "MaxPaperOpenPerHourReached") Console.WriteLine($"[PAPER_OPEN_SUPPRESSED] Reason={paperGate.Reason} Strategy={opp.Strategy} MarketOrGroup={opp.GroupKey}");
                                }
                                else if (state.PaperExecutionGloballyBlockedByDiscovery || state.DiscoveryReducedUniverse || !state.DiscoveryHealthy || !state.DiscoveryScannerSafeSourceAvailable || state.DiscoverySelectedSource.Equals("Blocked", StringComparison.OrdinalIgnoreCase) || state.DiscoverySelectedSource.Equals("ReducedUniverseDiagnosticsOnly", StringComparison.OrdinalIgnoreCase))
                                {
                                    state.RecordPaperPretradeReject("PaperBlockedByDiscoveryMode");
                                    if (ShouldQuietLog("paper", "PAPER_BLOCKED_BY_DISCOVERY_MODE", $"{opp.Strategy}|{state.DiscoverySelectedSource}|{state.DiscoveryReducedUniverse}", LogImportance.Important, "PaperBlockedByDiscoveryMode", opp.GroupKey, strategy: opp.Strategy, everyNCycles: 1, maxPerHour: options.Logging.MaxVerifiedPretradeBlockedAuditPerHour))
                                        Console.WriteLine($"[PAPER_BLOCKED_BY_DISCOVERY_MODE] Strategy={opp.Strategy} DiscoveryMode={state.DiscoverySelectedSource} DiscoveryHealthy={state.DiscoveryHealthy.ToString().ToLowerInvariant()} DiscoveryReducedUniverse={state.DiscoveryReducedUniverse.ToString().ToLowerInvariant()} Reason=ReducedUniverseDiagnosticsOnly");
                                }
                                else if (VerifiedPaperEligibilityDryRun.CanOpenPaper(eligibility, verifiedPaperConfig)) opened = verifiedExecution.OpenPaperPosition(opp, pre, positionBook, plan, fill);
                                if (opened is null) Console.WriteLine($"[PAPER BASKET SKIPPED] Group={opp.GroupKey} Reason={(verifiedMode == StrategyMode.DiagnosticsOnly ? "ModeDiagnosticsOnly" : paperGate.Approved ? (fill.Status == FillSimulationStatus.FullyFillable ? "DuplicateOpenPosition" : "FillSimulationFailed") : paperGate.Reason)}");
                                else { stability.MarkPaperOpened(opp.GroupKey); paper.RegisterExternalBasketOpen(opened, opened.TotalCost, opened.ExpectedProfit); Console.WriteLine($"[PAPER_POSITION_OPENED] ID={opened.PositionId}"); Console.WriteLine($"[PAPER_VERIFIED_BASKET_OPENED] Group={opp.GroupKey} Legs={opp.LegsCount} Cost={opened.TotalCost:0.####} ExpectedProfit={opened.ExpectedProfit:0.####}"); Console.WriteLine($"[PAPER BASKET OPENED] Group={opp.GroupKey} Legs={opp.LegsCount} Qty={opened.Quantity:0.####} Cost={opened.TotalCost:0.####} GrossEdge={opened.GrossEdgeAtOpen:0.####} NetEdge={opened.NetEdgeAtOpen:0.####} FillAdjustedNetEdge={fill.FillAdjustedNetEdgePerBasket:0.####} ExpectedProfit={opened.ExpectedProfit:0.####} CostSource=FillSimulation Profile={opened.ActiveProfile}"); Console.WriteLine($"[PAPER ACCOUNT] Cash={paper.Balance:0.####} Locked={paper.LockedCapital:0.####} OpenExposure={paper.LockedCapital:0.####} UnrealizedPnl={paper.UnrealizedPnl:0.####} RealizedPnl={paper.RealizedPnl:0.####} Equity={paper.Equity:0.####} OpenPositions={positionBook.OpenPositions.Count}"); var mtmFingerprint=$"{opp.GroupKey}|Incomplete|{opp.LegsCount}"; mtmCycle++; var logMtm = !options.Logging.LogPaperMtmOnChangeOnly || mtmFingerprint != lastMtmFingerprint || (options.Logging.LogPaperMtmEveryNCycles > 0 && mtmCycle % options.Logging.LogPaperMtmEveryNCycles == 0); lastMtmFingerprint = mtmFingerprint; if (logMtm) Console.WriteLine($"[PAPER_BASKET_MTM] Group={opp.GroupKey} MtMStatus=Incomplete MissingExitPrices={opp.LegsCount}"); verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "MtMUpdated", "Ok", "Incomplete", opened.NetEdgeAtOpen, 0m, opened.TotalCost, opened.Quantity, $"MissingExitPrices={opp.LegsCount}")); }
                            }
                        }

                        if (!isExec && !hasOpenPosition && screen.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.ExperimentalPaperCandidate)
                        {
                            experimentalCandidates++;
                            var expState = stability.TrackExperimental(g.GroupKey, screen.ActiveProfileNetEdge, screen.ExperimentalProfileNetEdge, options.RequiredConsecutiveExperimentalScans, options.MinExperimentalNetEdgePerBasket);
                            experimentalCandidateCycle++;
                            var expFingerprint = $"{g.GroupKey}|{screen.ActiveProfileNetEdge}|{screen.ExperimentalProfileNetEdge}|{expState}|{screen.ExecutionStatus}";
                            var expChanged = !lastExperimentalFingerprintByGroup.TryGetValue(g.GroupKey, out var prevExp) || prevExp != expFingerprint;
                            lastExperimentalFingerprintByGroup[g.GroupKey] = expFingerprint;
                            var expPeriodic = options.Logging.LogExperimentalCandidateEveryNCycles > 0 && experimentalCandidateCycle % options.Logging.LogExperimentalCandidateEveryNCycles == 0;
                            if (!options.Logging.LogExperimentalCandidateOnChangeOnly || expChanged || expPeriodic)
                                Console.WriteLine($"[EXPERIMENTAL_PROFILE_PENDING] Group={g.GroupKey} Consecutive={stability.ConsecutiveExperimental(g.GroupKey)} Required={options.RequiredConsecutiveExperimentalScans} ExperimentalNet={screen.ExperimentalProfileNetEdge:0.####} ActiveNet={screen.ActiveProfileNetEdge:0.####} State={expState}");
                            if (expState == VerifiedBasketState.ExperimentalProfileStable && !options.EnableExperimentalProfilePaper && (expChanged || expPeriodic))
                                Console.WriteLine($"[EXPERIMENTAL_PROFILE_BLOCKED] Group={g.GroupKey} Reason=ExperimentalPaperDisabled");
                        }
                        if (!isExec && screen.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.DiagnosticsOnlyPositive)
                        {
                            diagnosticsOnlyPositive++;
                            stability.MarkDiagnosticsOnly(g.GroupKey);
                        }
                        skipReason = hasOpenPosition ? "AlreadyOpen" : isExec ? "Executable" : screen.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.ExperimentalPaperCandidate ? "ExperimentalProfileCandidate" : "NegativeEdge";
                        groupDiagnostics.Add(new VerifiedGroupDiagnosticDto(g.GroupKey, g.MarketIds.Count, g.ResolvedMarkets.Count, g.MissingMarketIds.Count, "VerifiedResolved", skipReason, edge, 0, 0, Array.Empty<string>(), Array.Empty<string>()));
                        pricingDiagnostics.Add(new VerifiedGroupPricingDto(g.GroupKey, resolvedNoAsks.Count, formula.GuaranteedPayout, formula.NoAskSum, formula.MinNoAsk, formula.MaxNoAsk, formula.AverageNoAsk, formula.GrossEdge, formula.Fees, formula.Slippage, formula.SafetyBuffer, formula.NetEdge, 1, formula.NetEdge, skipReason, formula.FormulaWarnings));
                        verifiedPricingExport.Add(new
                        {
                            groupKey = g.GroupKey,
                            totalLegs = resolvedNoAsks.Count,
                            noAskResolvedCount = resolvedNoAsks.Count,
                            missingNoAskCount = 0,
                            pricedLegs = resolvedNoAsks.Select(x => new { marketId = x.MarketId, conditionId = x.ConditionId, noAsk = x.NoAsk, noAskQuantity = x.NoAskQuantity, noAskSource = x.Source, yesBid = x.YesBid, yesBidQuantity = x.YesBidQuantity, noTokenId = x.NoTokenId, priceResolutionFailureReason = x.FailureReason }).ToArray(),
                            missingPriceLegs = Array.Empty<object>(),
                            suggestedPrunedAllowlistTemplate = (object?)null
                        });
                    }
                    var rankedScreen = verifiedScreenResults.OrderByDescending(x => x.ActiveProfileNetEdge).ToList();
                    var recommendedActions = new List<string>();
                    if (rankedScreen.Count <= 1) recommendedActions.Add("Add more verified groups; only one allowlisted group exists.");
                    if (rankedScreen.Any(x => x.GrossEdge > 0 && x.ActiveProfileNetEdge <= 0)) recommendedActions.Add("FIFA basket has raw edge but not enough after costs.");
                    recommendedActions.Add("Review candidate groups with higher gross edge.");
                    recommendedActions.Add("Check whether FeePerLeg config is realistic.");
                    var screenerPath = Path.Combine(contentRootPath, "exports/verified-basket-opportunity-screener-latest.json");
                    var unresolvedConfiguredGroups = resolved.Where(x => x.ValidationStatus != "VerifiedGroupResolved").Select(x => (object)new { x.GroupKey, Reason = x.RejectionReason, x.ValidationStatus }).ToArray();
                    var repairReportPath = Path.Combine(contentRootPath, options.MultiOutcomeReview.ExportAllowlistRepairReportPath);
                    var repairSuggestedPath = Path.Combine(contentRootPath, options.MultiOutcomeReview.ExportAllowlistRepairSuggestedConfigPath);
                    var patchPreviewPath = Path.Combine(contentRootPath, "exports/verified-allowlist-repair-patch-preview-latest.json");
                    var patchedPreviewPath = Path.Combine(contentRootPath, "exports/verified-multi-outcome-groups-patched-preview.json");
                    var patchedPreviewMetadataPath = Path.Combine(contentRootPath, "exports/verified-multi-outcome-groups-patched-preview.with-metadata.json");
                    AllowlistRepairReport repairReport;
                    AllowlistRepairPatchPreview patchPreview;
                    try
                    {
                        SetScannerStage("AllowlistRepair", "AllowlistRepairService");
                        options.AllowlistRepair.DiscoveryPartialDiagnosticsOnly = !lastDiscoverySummary.DiscoveryHealthy && lastDiscoverySummary.ActiveMarketsAvailable < options.MarketDiscovery.MinHealthyActiveMarkets;
                        var repairExports = allowlistRepairService.Export(repairReportPath, repairSuggestedPath, allowlistedGroups, resolved, verifiedPricingExport, boundedCandidates, options.AllowlistRepair, contentRootPath, options.Logging);
                        repairReport = repairExports.Report;
                        var refreshDiagnosticsPath = Path.Combine(contentRootPath, "exports/verified-allowlist-refresh-diagnostics-latest.json");
                        allowlistRepairService.ExportRefreshDiagnostics(refreshDiagnosticsPath, repairReport, allowlistedGroups);
                        patchPreview = allowlistRepairService.ExportPatchPreview(patchPreviewPath, patchedPreviewPath, patchedPreviewMetadataPath, repairReport, allowlistedGroups, contentRootPath).PatchPreview;
                    }
                    catch (Exception ex)
                    {
                        var safeMessage = ex.Message.Replace("\"", "'");
                        Console.WriteLine($"[ALLOWLIST_REPAIR_ERROR] Message={safeMessage}");
                        uiLogger.LogWarn("allowlist-repair", $"{{\"event\":\"allowlist_repair_error\",\"message\":\"{safeMessage}\"}}");
                        repairReport = BuildEmptyAllowlistRepairReport(allowlistedGroups.Count, safeMessage);
                        patchPreview = BuildEmptyAllowlistRepairPatchPreview(repairReport.SnapshotId, "config/verified-multi-outcome-groups.json");
                    }
                    state.SetRuntimeCounts(patchPreviewItemsCount: patchPreview.Patches.Count);
                    var summary = repairReport.Summary;
                    var allowlistHealth = repairReport.Groups.Select(g => new
                    {
                        groupKey = g.GroupKey,
                        enabled = g.Enabled,
                        configuredMarketCount = g.ConfiguredMarketCount,
                        resolvedMarketCount = g.ResolvedMarketCount,
                        missingMarketCount = g.MissingMarketCount,
                        missingMarketIds = g.MissingMarketIds,
                        foundMarketIds = g.SuggestedPrunedTemplate?["marketIds"] is System.Text.Json.Nodes.JsonArray ids ? ids.Select(x => x?.GetValue<string>() ?? string.Empty).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() : Array.Empty<string>(),
                        status = g.Status,
                        healthCategory = g.HealthCategory,
                        reason = g.Reason,
                        recommendedAction = g.RecommendedAction,
                        repairConfidence = g.RepairConfidence
                    }).ToArray();
                    var finalClassification = ScanLogSummaryService.AllowlistPrimaryClassification(allowlistedGroups, repairReport.Groups);
                    var healthyCount = finalClassification.Healthy + finalClassification.MonitoringOnly;
                    var brokenCount = finalClassification.NeedsPricingPrune + finalClassification.NeedsRefresh + finalClassification.ReviewOnly + finalClassification.BrokenConfig;
                    var unresolvedCount = brokenCount;
                    var disabledCount = finalClassification.Disabled; var ignoredCount = finalClassification.Ignored;
                    var needsRefreshCount = finalClassification.NeedsRefresh;
                    allowlistHealthCycle++;
                    var classificationTotal = finalClassification.PrimaryCategorySum;
                    var classificationValid = finalClassification.Valid;
                    var allowlistFingerprint = $"{finalClassification.Configured}|{healthyCount}|{brokenCount}|{disabledCount}|{ignoredCount}|{needsRefreshCount}|{finalClassification.NeedsPricingPrune}|{finalClassification.NeedsRefresh}|{finalClassification.ReviewOnly}|{finalClassification.BrokenConfig}|{classificationTotal}|{classificationValid}";
                    var shouldLogAllowlist = !options.Logging.LogAllowlistHealthOnChangeOnly || allowlistFingerprint != lastAllowlistHealthFingerprint || (options.Logging.LogAllowlistHealthEveryNCycles > 0 && allowlistHealthCycle % options.Logging.LogAllowlistHealthEveryNCycles == 0);
                    lastAllowlistHealthFingerprint = allowlistFingerprint;
                    if (shouldLogAllowlist)
                    {
                        Console.WriteLine($"[ALLOWLIST_HEALTH] Configured={finalClassification.Configured} Healthy={finalClassification.Healthy} MonitoringOnly={finalClassification.MonitoringOnly} NeedsPricingPrune={finalClassification.NeedsPricingPrune} NeedsRefresh={finalClassification.NeedsRefresh} ReviewOnly={finalClassification.ReviewOnly} BrokenConfig={finalClassification.BrokenConfig} Disabled={finalClassification.Disabled} Ignored={finalClassification.Ignored} BrokenTotal={brokenCount} ClassificationTotal={classificationTotal} ClassificationValid={classificationValid.ToString().ToLowerInvariant()}");
                        foreach (var conflict in finalClassification.ResolvedConflictGroups.Take(20))
                            Console.WriteLine($"[ALLOWLIST_CLASSIFICATION_CONFLICT_RESOLVED] Group={conflict.GroupKey} RawCategoryCandidates=[{string.Join(",", conflict.RawCategoryCandidates)}] FinalPrimaryCategory={conflict.FinalPrimaryCategory} Resolution={conflict.FinalReason}");
                        foreach (var duplicate in finalClassification.DuplicateFinalPrimaryCategoryGroups.Take(20))
                            Console.WriteLine($"[ALLOWLIST_CLASSIFICATION_DUPLICATE] Group={duplicate.GroupKey} FinalPrimaryCategories=[{duplicate.FinalPrimaryCategory}] RawCategoryCandidates=[{string.Join(",", duplicate.RawCategoryCandidates)}] FinalPrimaryCategory={duplicate.FinalPrimaryCategory} Reasons=[{string.Join(",", duplicate.SecondaryReasons)}] Action=UseFinalPrimaryCategoryOnly");
                        if (!classificationValid)
                            Console.WriteLine($"[ALLOWLIST_CLASSIFICATION_ERROR] Configured={finalClassification.Configured} PrimaryCategorySum={classificationTotal} MissingPrimaryCategoryGroupCount={finalClassification.MissingPrimaryCategoryGroupCount} DuplicateFinalPrimaryCategoryGroupCount={finalClassification.DuplicateFinalPrimaryCategoryGroupCount} DuplicateGroups=[{string.Join(",", finalClassification.DuplicateFinalPrimaryCategoryGroups.Select(x => x.GroupKey).Take(20))}] MissingGroups=[{string.Join(",", finalClassification.MissingGroups.Take(20))}]");
                    }
                    foreach (var remaining in repairReport.Groups.Where(x => x.RecommendedAction == "PruneMissingNoAskLegs" && x.MissingNoAskMarketIds.Count > 0))
                    {
                        var missingIds = string.Join(",", remaining.MissingNoAskMarketIds);
                        var remainingFingerprint = $"{remaining.GroupKey}|{missingIds}|{remaining.RepairSnapshotId}";
                        if (logThrottle.ShouldLog($"ALLOWLIST_CONFIG_REPAIR_REMAINING:{remaining.GroupKey}", remainingFingerprint, options.Logging.LogAllowlistRepairOnChangeOnly, options.Logging.LogAllowlistRepairEveryNCycles))
                            Console.WriteLine($"[ALLOWLIST_CONFIG_REPAIR_REMAINING] Group={remaining.GroupKey} Action=PruneMissingNoAskLegs MissingMarketIds=[{missingIds}]");
                    }
                    var healthPath = Path.Combine(contentRootPath, "exports/verified-allowlist-health-latest.json"); Directory.CreateDirectory(Path.GetDirectoryName(healthPath)!); File.WriteAllText(healthPath, System.Text.Json.JsonSerializer.Serialize(new { configured = finalClassification.Configured, healthy = finalClassification.Healthy, monitoringOnly = finalClassification.MonitoringOnly, needsPricingPrune = finalClassification.NeedsPricingPrune, needsRefresh = finalClassification.NeedsRefresh, reviewOnly = finalClassification.ReviewOnly, brokenConfig = finalClassification.BrokenConfig, disabled = finalClassification.Disabled, ignored = finalClassification.Ignored, brokenTotal = brokenCount, classificationTotal, classificationValid, invariantResult = classificationValid, categoryCounts = repairReport.CategoryCounts, groups = allowlistHealth }, new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
                    var cleanup = new { metadata = new { generatedAtUtc = DateTime.UtcNow, note = "suggested only" }, groups = allowlistRepairService.BuildSuggestedConfig(repairReport).Groups };
                    var cleanupPath = Path.Combine(contentRootPath, "exports/verified-allowlist-cleanup-suggested.json"); File.WriteAllText(cleanupPath, System.Text.Json.JsonSerializer.Serialize(cleanup, new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
                    var repairLogEveryNCycles = options.Logging.SuppressRepeatedRepairSnapshotLogs && !options.Diagnostics.DebuggerSafeMode ? 0 : options.Logging.LogAllowlistRepairEveryNCycles;
                    var repairSuggestionEveryNCycles = options.Logging.SuppressRepeatedRepairSnapshotLogs && !options.Diagnostics.DebuggerSafeMode ? 0 : options.Logging.LogRepairSuggestionsEveryNCycles;
                    var repairSuggestionMaxPerHour = options.Diagnostics.OperationalQuietMode ? Math.Min(3, Math.Max(1, options.Logging.MaxRepairSuggestionLogsPerHour)) : options.Logging.MaxRepairSuggestionLogsPerHour;
                    var repairSnapshotFingerprint = options.Logging.SuppressRepeatedRepairSnapshotLogs
                        ? $"{repairReport.Snapshot.CandidateGroupsCount}|{repairReport.Snapshot.VerifiedGroupsCount}|{repairReport.Snapshot.Source}"
                        : $"{repairReport.SnapshotId}|{repairReport.Snapshot.CandidateGroupsCount}|{repairReport.Snapshot.VerifiedGroupsCount}|{repairReport.Snapshot.Source}";
                    if (ShouldQuietLog("allowlist-repair", "ALLOWLIST_REPAIR_SNAPSHOT", repairSnapshotFingerprint, LogImportance.Normal, everyNCycles: repairLogEveryNCycles))
                        Console.WriteLine($"[ALLOWLIST_REPAIR_SNAPSHOT] Id={repairReport.SnapshotId} CandidateGroups={repairReport.Snapshot.CandidateGroupsCount} VerifiedGroups={repairReport.Snapshot.VerifiedGroupsCount} Source={repairReport.Snapshot.Source}");
                    var repairReportFingerprint = options.Logging.SuppressRepeatedRepairSnapshotLogs
                        ? $"{finalClassification.Configured}|{finalClassification.Healthy}|{finalClassification.MonitoringOnly}|{finalClassification.NeedsPricingPrune}|{finalClassification.NeedsRefresh}|{finalClassification.ReviewOnly}|{finalClassification.BrokenConfig}|{finalClassification.Disabled}|{finalClassification.Ignored}|{string.Join(";", finalClassification.Groups.Select(x => $"{x.GroupKey}:{x.FinalPrimaryCategory}"))}"
                        : $"{repairReport.SnapshotId}|{finalClassification.Configured}|{finalClassification.Healthy}|{finalClassification.MonitoringOnly}|{finalClassification.NeedsPricingPrune}|{finalClassification.NeedsRefresh}|{finalClassification.ReviewOnly}|{finalClassification.BrokenConfig}|{finalClassification.Disabled}|{finalClassification.Ignored}|{string.Join(";", finalClassification.Groups.Select(x => $"{x.GroupKey}:{x.FinalPrimaryCategory}"))}";
                    if (ShouldQuietLog("allowlist-repair", "ALLOWLIST_REPAIR_REPORT", repairReportFingerprint, LogImportance.Normal, everyNCycles: repairLogEveryNCycles))
                        Console.WriteLine($"[ALLOWLIST_REPAIR_REPORT] Groups={finalClassification.Configured} Healthy={finalClassification.Healthy} MonitoringOnly={finalClassification.MonitoringOnly} NeedsPricingPrune={finalClassification.NeedsPricingPrune} NeedsRefresh={finalClassification.NeedsRefresh} ReviewOnly={finalClassification.ReviewOnly} BrokenConfig={finalClassification.BrokenConfig} Disabled={finalClassification.Disabled} Ignored={finalClassification.Ignored} BrokenTotal={brokenCount} Path={options.MultiOutcomeReview.ExportAllowlistRepairReportPath}");

                    var refreshDiag = allowlistRepairService.BuildRefreshDiagnostics(repairReport, allowlistedGroups);
                    var actionExplainedSuppressedThisCycle = 0;
                    var unstableSummaries = refreshDiag.Items
                        .Select(x => allowlistRepairService.GetInstabilitySummary(x.GroupKey))
                        .Where(x => x.IsUnstable)
                        .GroupBy(x => x.GroupKey, StringComparer.OrdinalIgnoreCase)
                        .Select(x => x.First())
                        .ToArray();
                    var repairReportGroupKeys = repairReport.Groups.Select(x => x.GroupKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var activeUnstableLocks = allowlistRepairService.GetUnstableManualReviewLocks()
                        .Where(x => repairReportGroupKeys.Contains(x.GroupKey))
                        .ToArray();
                    var activeUnstableLockKeys = activeUnstableLocks.Select(x => x.GroupKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var finalLockedManualReviewCount = refreshDiag.Items.Count(x => x.FinalDecision.Equals("LockedManualReview", StringComparison.OrdinalIgnoreCase))
                        + activeUnstableLockKeys.Where(x => refreshDiag.Items.All(item => !item.GroupKey.Equals(x, StringComparison.OrdinalIgnoreCase))).Count();
                    var effectiveBrokenConfig = finalClassification.BrokenConfig;
                    var effectiveReviewOnly = finalClassification.ReviewOnly;
                    state.SetAllowlistRefreshCounters(
                        finalClassification.NeedsRefresh,
                        effectiveReviewOnly,
                        refreshDiag.Items.Count(x => x.ResolverStatus.Contains("Mismatch", StringComparison.OrdinalIgnoreCase)),
                        refreshDiag.Items.Count(x => x.RecommendedAction.Equals("RefreshFromCandidateExport", StringComparison.OrdinalIgnoreCase)),
                        refreshDiag.Items.Count(x => x.Confidence.Equals("High", StringComparison.OrdinalIgnoreCase)),
                        refreshDiag.Items.Count(x => x.FinalDecision.Equals("NoCandidate", StringComparison.OrdinalIgnoreCase)),
                        refreshDiag.Items.Count(x => x.FinalDecision.Equals("CandidateRejectedSemanticConflict", StringComparison.OrdinalIgnoreCase)),
                        refreshDiag.Items.Count(x => x.FinalDecision.Equals("CandidateRejectedLowConfidence", StringComparison.OrdinalIgnoreCase)),
                        refreshDiag.Items.Count(x => x.FinalDecision.Equals("CandidateRejectedUnstableAcrossSnapshots", StringComparison.OrdinalIgnoreCase)),
                        refreshDiag.Items.Count(x => x.FinalDecision.Equals("CandidateAcceptedPreviewOnly", StringComparison.OrdinalIgnoreCase)),
                        finalLockedManualReviewCount,
                        0,
                        Math.Max(unstableSummaries.Length, activeUnstableLocks.Length),
                        Math.Max(unstableSummaries.Length, activeUnstableLocks.Length),
                        finalClassification.Healthy,
                        finalClassification.MonitoringOnly,
                        finalClassification.NeedsPricingPrune,
                        effectiveBrokenConfig,
                        finalClassification.Disabled,
                        finalClassification.Ignored,
                        classificationTotal,
                        classificationValid);
                    foreach (var item in refreshDiag.Items)
                    {
                        var status = item.ResolverStatus.Contains("Mismatch", StringComparison.OrdinalIgnoreCase) ? "Mismatch"
                            : item.ResolverStatus.Contains("NotFound", StringComparison.OrdinalIgnoreCase) ? "NotFound"
                            : item.RecommendedAction.Equals("NeedsManualReview", StringComparison.OrdinalIgnoreCase) ? "ReviewOnly"
                            : "NeedsRefresh";
                        var action = item.RecommendedAction.Equals("RefreshFromCandidateExport", StringComparison.OrdinalIgnoreCase) ? "RefreshPreview"
                            : item.RecommendedAction.Equals("KeepMonitoring", StringComparison.OrdinalIgnoreCase) ? "NoOp"
                            : "ManualReview";
                        var candidateForLog = string.IsNullOrWhiteSpace(item.BestCandidateGroupKey) ? "None" : item.BestCandidateGroupKey;
                        var fp = $"{item.GroupKey}|{status}|{candidateForLog}|{item.BestCandidateScore}|{item.OverlapRatio}|{string.Join(",", item.MissingMarketIds)}|{string.Join(",", item.MissingTokenIds)}|{string.Join(",", item.AddedMarketIds)}|{string.Join(",", item.AddedTokenIds)}|{string.Join(",", item.RemovedMarketIds)}|{string.Join(",", item.RemovedTokenIds)}|{item.Confidence}|{action}|{item.FinalDecision}";
                        if (ShouldQuietLog("allowlist-refresh", "ALLOWLIST_REFRESH_DIAG", fp, LogImportance.Normal, groupKey: item.GroupKey, everyNCycles: repairLogEveryNCycles, maxPerHour: repairSuggestionMaxPerHour))
                            Console.WriteLine($"[ALLOWLIST_REFRESH_DIAG] Group={item.GroupKey} Status={status} ConfiguredLegs={item.ConfiguredLegCount} BestCandidate={candidateForLog} Score={item.BestCandidateScore:0.##} Overlap={item.OverlapRatio:0.##} MissingMarketIds=[{string.Join(",", item.MissingMarketIds)}] MissingTokenIds=[{string.Join(",", item.MissingTokenIds)}] AddedMarketIds=[{string.Join(",", item.AddedMarketIds)}] AddedTokenIds=[{string.Join(",", item.AddedTokenIds)}] RemovedMarketIds=[{string.Join(",", item.RemovedMarketIds)}] RemovedTokenIds=[{string.Join(",", item.RemovedTokenIds)}] Confidence={item.Confidence} Action={action} AutoApply=false");
                        var decisionKey = $"{repairReport.SnapshotId}|{item.GroupKey}|{candidateForLog}|{item.FinalDecision}";
                        var semanticConflict = AllowlistRepairService.DetectRefreshSemanticConflict(item.GroupKey, item.BestCandidateGroupKey);
                        var sameGroupMarketSetMismatch = string.IsNullOrWhiteSpace(semanticConflict)
                            && item.GroupKey.Equals(item.BestCandidateGroupKey, StringComparison.OrdinalIgnoreCase)
                            && (item.AddedMarketIds.Count > 0 || item.RemovedMarketIds.Count > 0 || item.AddedTokenIds.Count > 0 || item.RemovedTokenIds.Count > 0);
                        if (sameGroupMarketSetMismatch)
                        {
                            var candidateMarketIds = item.CurrentConfiguredMarketIds
                                .Except(item.RemovedMarketIds, StringComparer.OrdinalIgnoreCase)
                                .Concat(item.AddedMarketIds)
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                            var marketMismatchKey = $"{decisionKey}|MarketSetMismatch";
                            if (emittedAllowlistRefreshMarketSetMismatches.Add(marketMismatchKey) && ShouldQuietLog("allowlist-refresh", "ALLOWLIST_REFRESH_MARKET_SET_MISMATCH", marketMismatchKey, LogImportance.Normal, groupKey: item.GroupKey, everyNCycles: repairLogEveryNCycles, maxPerHour: repairSuggestionMaxPerHour))
                                Console.WriteLine($"[ALLOWLIST_REFRESH_MARKET_SET_MISMATCH] Group={item.GroupKey} BestCandidate={candidateForLog} ConfiguredMarketIds=[{string.Join(",", item.CurrentConfiguredMarketIds)}] CandidateMarketIds=[{string.Join(",", candidateMarketIds)}] AddedMarketIds=[{string.Join(",", item.AddedMarketIds)}] RemovedMarketIds=[{string.Join(",", item.RemovedMarketIds)}] Overlap={item.OverlapRatio:0.##} Action=ManualReview AutoApply=false");
                        }
                        if (!string.IsNullOrWhiteSpace(semanticConflict))
                        {
                            var semanticKey = $"{decisionKey}|{semanticConflict}";
                            if (emittedAllowlistRefreshSemanticConflicts.Add(semanticKey) && ShouldQuietLog("allowlist-refresh", "ALLOWLIST_REFRESH_SEMANTIC_CONFLICT", semanticKey, LogImportance.Normal, groupKey: item.GroupKey, everyNCycles: repairLogEveryNCycles, maxPerHour: repairSuggestionMaxPerHour))
                                Console.WriteLine($"[ALLOWLIST_REFRESH_SEMANTIC_CONFLICT] Group={item.GroupKey} Candidate={candidateForLog} Conflict={semanticConflict} Action=ManualReview AutoApply=false");
                        }
                        if (emittedAllowlistRefreshFinalDecisions.Add(decisionKey) && ShouldQuietLog("allowlist-refresh", "ALLOWLIST_REFRESH_FINAL_DECISION", decisionKey, LogImportance.Normal, groupKey: item.GroupKey, everyNCycles: repairLogEveryNCycles, maxPerHour: repairSuggestionMaxPerHour))
                            Console.WriteLine($"[ALLOWLIST_REFRESH_FINAL_DECISION] Group={item.GroupKey} Decision={item.FinalDecision} BestCandidate={candidateForLog} Score={item.BestCandidateScore:0.##} Confidence={item.Confidence} Reason={item.Reason} AutoApply=false");
                        if (item.GroupKey.Equals("winner:2026 women s us open|kind:generic", StringComparison.OrdinalIgnoreCase))
                        {
                            if (emittedAllowlistRefreshActionExplanations.Add(decisionKey))
                                Console.WriteLine($"[ALLOWLIST_REFRESH_ACTION_EXPLAINED] Group={item.GroupKey} CurrentStatus={item.ResolverStatus} BestCandidate={candidateForLog} CandidateScore={item.BestCandidateScore:0.##} Confidence={item.Confidence} AddedMarketIds=[{string.Join(",", item.AddedMarketIds)}] RemovedMarketIds=[{string.Join(",", item.RemovedMarketIds)}] AddedTokenIds=[{string.Join(",", item.AddedTokenIds)}] RemovedTokenIds=[{string.Join(",", item.RemovedTokenIds)}] FinalDecision={item.FinalDecision} Reason={item.Reason} AutoApply=false");
                            else
                                actionExplainedSuppressedThisCycle++;
                        }
                    }
                    foreach (var unstable in unstableSummaries)
                    {
                        var unstableKey = $"{repairReport.SnapshotId}|{unstable.GroupKey}|ActionFlipFlopDuringSoak";
                        if (emittedAllowlistRefreshFinalDecisions.Add($"unstable|{unstableKey}"))
                            Console.WriteLine($"[ALLOWLIST_REFRESH_UNSTABLE_GROUP] Group={unstable.GroupKey} ObservedDecisions=[{string.Join(",", unstable.ObservedDecisions)}] ObservedActions=[{string.Join(",", unstable.ObservedActions)}] SnapshotsObserved={unstable.SnapshotsObserved} FinalDecision=LockedManualReview Reason=ActionFlipFlopDuringSoak AutoApply=false");
                    }
                    foreach (var manualLock in activeUnstableLocks)
                    {
                        if (emittedAllowlistUnstableManualReviewLocks.Add(manualLock.GroupKey))
                        {
                            Console.WriteLine($"[ALLOWLIST_UNSTABLE_MANUAL_REVIEW_LOCKED] Group={manualLock.GroupKey} Reason={manualLock.Reason} ObservedDecisions=[{string.Join(",", manualLock.ObservedDecisions)}] ObservedActions=[{string.Join(",", manualLock.ObservedActions)}] FirstDetectedSnapshot={manualLock.FirstDetectedSnapshotId} AutoApply=false");
                        }
                        else if (ShouldQuietLog("allowlist-refresh", "ALLOWLIST_UNSTABLE_MANUAL_REVIEW_LOCK_ACTIVE", $"{manualLock.GroupKey}|{manualLock.LastSeenSnapshotId}|{manualLock.Reason}", LogImportance.Normal, groupKey: manualLock.GroupKey, everyNCycles: repairLogEveryNCycles, maxPerHour: repairSuggestionMaxPerHour))
                        {
                            Console.WriteLine($"[ALLOWLIST_UNSTABLE_MANUAL_REVIEW_LOCK_ACTIVE] Group={manualLock.GroupKey} Reason={manualLock.Reason} HealthCategory=ReviewOnly RecommendedAction=NeedsManualReview AutoApply=false");
                        }
                    }
                    if (actionExplainedSuppressedThisCycle > 0)
                    {
                        state.SetAllowlistRefreshCounters(
                            finalClassification.NeedsRefresh,
                            effectiveReviewOnly,
                            refreshDiag.Items.Count(x => x.ResolverStatus.Contains("Mismatch", StringComparison.OrdinalIgnoreCase)),
                            refreshDiag.Items.Count(x => x.RecommendedAction.Equals("RefreshFromCandidateExport", StringComparison.OrdinalIgnoreCase)),
                            refreshDiag.Items.Count(x => x.Confidence.Equals("High", StringComparison.OrdinalIgnoreCase)),
                            refreshDiag.Items.Count(x => x.FinalDecision.Equals("NoCandidate", StringComparison.OrdinalIgnoreCase)),
                            refreshDiag.Items.Count(x => x.FinalDecision.Equals("CandidateRejectedSemanticConflict", StringComparison.OrdinalIgnoreCase)),
                            refreshDiag.Items.Count(x => x.FinalDecision.Equals("CandidateRejectedLowConfidence", StringComparison.OrdinalIgnoreCase)),
                            refreshDiag.Items.Count(x => x.FinalDecision.Equals("CandidateRejectedUnstableAcrossSnapshots", StringComparison.OrdinalIgnoreCase)),
                            refreshDiag.Items.Count(x => x.FinalDecision.Equals("CandidateAcceptedPreviewOnly", StringComparison.OrdinalIgnoreCase)),
                            finalLockedManualReviewCount,
                            actionExplainedSuppressedThisCycle,
                            Math.Max(unstableSummaries.Length, activeUnstableLocks.Length),
                            Math.Max(unstableSummaries.Length, activeUnstableLocks.Length),
                            finalClassification.Healthy,
                            finalClassification.MonitoringOnly,
                            finalClassification.NeedsPricingPrune,
                            effectiveBrokenConfig,
                            finalClassification.Disabled,
                            finalClassification.Ignored,
                            classificationTotal,
                            classificationValid);
                    }
                    var patchableCount = patchPreview.Summary.PatchableHighConfidence + patchPreview.Summary.PatchableMediumConfidence;
                    var quarantinedCount = patchPreview.Summary.Quarantined;
                    var noOpCount = patchPreview.Summary.NoOp;
                    var lockedCount = patchPreview.Summary.Locked;
                    var reviewOnlyCount = patchPreview.Patches.Count - patchableCount;
                    var patchPreviewFingerprint = $"{patchableCount}|{reviewOnlyCount}|{quarantinedCount}|{noOpCount}|{lockedCount}|{string.Join(";", patchPreview.Patches.Select(x => $"{x.GroupKey}:{x.PatchType}:{x.Confidence}:{string.Join(",", x.RiskNotes)}"))}";
                    if (ShouldQuietLog("allowlist-repair", "ALLOWLIST_REPAIR_PATCH_PREVIEW", patchPreviewFingerprint, LogImportance.Normal, everyNCycles: repairLogEveryNCycles))
                        Console.WriteLine($"[ALLOWLIST_REPAIR_PATCH_PREVIEW] Snapshot={patchPreview.SnapshotId} Patchable={patchableCount} ReviewOnly={reviewOnlyCount} Quarantined={quarantinedCount} NoOp={noOpCount} Locked={lockedCount} Output=exports/verified-allowlist-repair-patch-preview-latest.json");
                    var patchedPreviewValidation = patchPreview.PatchedPreviewValidation;
                    var patchedPreviewValidationFingerprint = $"{patchPreview.SnapshotId}|{patchedPreviewValidation.TotalGroups}|{patchedPreviewValidation.UniqueGroupKeys}|{patchedPreviewValidation.DuplicateGroupKeys}|{patchedPreviewValidation.Valid}|{string.Join(';', patchedPreviewValidation.Reasons)}";
                    if (logThrottle.ShouldLog("ALLOWLIST_PATCHED_PREVIEW_VALIDATION", patchedPreviewValidationFingerprint, options.Logging.LogAllowlistRepairOnChangeOnly, repairLogEveryNCycles))
                    {
                        if (patchedPreviewValidation.Valid)
                            Console.WriteLine($"[ALLOWLIST_PATCHED_PREVIEW_VALIDATION] Total={patchedPreviewValidation.TotalGroups} UniqueGroupKeys={patchedPreviewValidation.UniqueGroupKeys} DuplicateGroupKeys={patchedPreviewValidation.DuplicateGroupKeys} Valid=true");
                        else
                            Console.WriteLine($"[ALLOWLIST_PATCHED_PREVIEW_INVALID] Reason={string.Join('|', patchedPreviewValidation.Reasons)} Total={patchedPreviewValidation.TotalGroups} UniqueGroupKeys={patchedPreviewValidation.UniqueGroupKeys} DuplicateGroupKeys={patchedPreviewValidation.DuplicateGroupKeys}");
                    }
                    foreach (var patch in patchPreview.Patches.Where(x => (x.PatchType is "ReplaceGroup" or "PruneGroup" or "DisableGroup") && (x.Confidence is "High" or "Medium")))
                    {
                        var removed = patch.Diff?["removedMarketIds"] is System.Text.Json.Nodes.JsonArray rm ? string.Join(",", rm.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x))) : string.Empty;
                        var added = patch.Diff?["addedMarketIds"] is System.Text.Json.Nodes.JsonArray am ? string.Join(",", am.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x))) : string.Empty;
                        var patchFingerprint = $"{patch.GroupKey}|{patch.PatchType}|{patch.Confidence}|{removed}|{added}";
                        if (ShouldQuietLog("allowlist-repair", "ALLOWLIST_REPAIR_PATCH", patchFingerprint, LogImportance.Normal, groupKey: patch.GroupKey, everyNCycles: repairLogEveryNCycles, maxPerHour: repairSuggestionMaxPerHour))
                            Console.WriteLine($"[ALLOWLIST_REPAIR_PATCH] Group={patch.GroupKey} Type={patch.PatchType} Confidence={patch.Confidence} Added=[{added}] Removed=[{removed}]");
                        if (patch.PatchType == "PruneGroup")
                        {
                            var validation = AllowlistRepairService.ValidatePatchItem(patch);
                            var validationRemoved = string.Join(",", validation.RemovedMarketIds);
                            var validationFingerprint = $"{patchPreview.SnapshotId}|{validation.GroupKey}|{validation.Valid}|{validation.FinalLegs}|{validationRemoved}";
                            if (logThrottle.ShouldLog($"ALLOWLIST_PATCH_VALIDATION:{patch.GroupKey}", validationFingerprint, options.Logging.LogAllowlistRepairOnChangeOnly, repairLogEveryNCycles))
                                Console.WriteLine($"[ALLOWLIST_PATCH_VALIDATION] Group={validation.GroupKey} Removed=[{validationRemoved}] Valid={validation.Valid.ToString().ToLowerInvariant()} FinalLegs={validation.FinalLegs}");
                        }
                    }
                    foreach (var repair in repairReport.Groups.Where(x => x.RecommendedAction is "PruneMissingNoAskLegs" or "RefreshFromCandidateExport" or "DisableMissingMarkets" or "NeedsManualReview" || x.RepairMatch is not null))
                    {
                        var isRepairAction = repair.RecommendedAction is "PruneMissingNoAskLegs" or "RefreshFromCandidateExport" or "DisableMissingMarkets" or "NeedsManualReview";
                        if (isRepairAction)
                        {
                            var suggestedLegs = repair.SuggestedPrunedTemplate?["marketIds"] is System.Text.Json.Nodes.JsonArray ids ? ids.Count : 0;
                            var suggestionStableHash = ScanLogSummaryService.RepairSuggestionStableHash(
                                repair.GroupKey,
                                repair.RecommendedAction,
                                repair.RepairConfidence,
                                repair.RepairMatch?.AddedMarketIds ?? Array.Empty<string>(),
                                repair.RepairMatch?.RemovedMarketIds ?? Array.Empty<string>(),
                                repair.MissingNoAsk,
                                lockProvider.IsHardLocked(repair.GroupKey),
                                repair.HealthCategory.Equals("Quarantined", StringComparison.OrdinalIgnoreCase) || repair.RepairConfidence.Equals("Unsafe", StringComparison.OrdinalIgnoreCase));
                            var repairFingerprint = options.Logging.SuppressRepeatedRepairSnapshotLogs ? suggestionStableHash : $"{repair.RepairSnapshotId}|{suggestionStableHash}|{suggestedLegs}|{repair.ActionVersion}";
                            if (ShouldQuietLog("allowlist-repair", "ALLOWLIST_REPAIR_SUGGESTION", $"{repair.RepairSnapshotId}|{repairFingerprint}", LogImportance.Normal, repairFingerprint, repair.GroupKey, everyNCycles: repairSuggestionEveryNCycles, maxPerHour: repairSuggestionMaxPerHour))
                                Console.WriteLine($"[ALLOWLIST_REPAIR_SUGGESTION] Group={repair.GroupKey} Snapshot={repair.RepairSnapshotId} Action={repair.RecommendedAction} MissingNoAsk={repair.MissingNoAsk} SuggestedLegs={suggestedLegs} Confidence={repair.RepairConfidence}");
                        }
                        if (repair.ActionVersion > 1 && !string.Equals(repair.PreviousAction, repair.CurrentAction, StringComparison.OrdinalIgnoreCase))
                        {
                            var directionFingerprint = ScanLogSummaryService.RepairActionDirectionFingerprint(repair.GroupKey, repair.PreviousAction, repair.CurrentAction);
                            if (ScanLogSummaryService.IsWomenUsOpenRepairFlipFlop(repair.GroupKey, repair.PreviousAction, repair.CurrentAction, repair.ReasonForChange) && options.Diagnostics.OperationalQuietMode)
                            {
                                if (ShouldQuietLog("allowlist-repair", "ALLOWLIST_REPAIR_UNSTABLE_SUPPRESSED", directionFingerprint, LogImportance.Normal, groupKey: repair.GroupKey, everyNCycles: options.Logging.LogAllowlistRepairEveryNCycles, maxPerHour: repairSuggestionMaxPerHour))
                                    Console.WriteLine($"[ALLOWLIST_REPAIR_UNSTABLE_SUPPRESSED] Group={repair.GroupKey} Reason=ActionFlipFlopDuringSoak");
                            }
                            else if (ShouldQuietLog("allowlist-repair", "ALLOWLIST_REPAIR_ACTION_CHANGED", directionFingerprint, LogImportance.Normal, groupKey: repair.GroupKey, everyNCycles: repairLogEveryNCycles, maxPerHour: repairSuggestionMaxPerHour))
                            {
                                Console.WriteLine($"[ALLOWLIST_REPAIR_ACTION_CHANGED] Group={repair.GroupKey} From={repair.PreviousAction} To={repair.CurrentAction} Reason={repair.ReasonForChange} ConsecutiveSnapshotMisses={repair.ConsecutiveMatchMisses}");
                                if (repair.GroupKey.Equals("winner:2026 nba finals|kind:generic", StringComparison.OrdinalIgnoreCase))
                                {
                                    var m = repair.RepairMatch;
                                    var bestCandidateScore = m?.Score ?? 0m;
                                    var bestCandidateOverlap = m?.MarketOverlap ?? 0m;
                                    Console.WriteLine($"[ALLOWLIST_REFRESH_ACTION_EXPLAINED] Group={repair.GroupKey} PreviousAction={repair.PreviousAction} NewAction={repair.CurrentAction} Reason={repair.ReasonForChange} ConsecutiveMisses={repair.ConsecutiveMatchMisses} RequiredForRefresh={options.AllowlistRepair.RefreshPreview.RequiredConsecutiveMatches} BestCandidateScore={bestCandidateScore:0.##} BestCandidateOverlap={bestCandidateOverlap:0.##} MissingMarketIds=[{string.Join(",", repair.MissingMarketIds)}] CandidateMarketIds=[{string.Join(",", m?.AddedMarketIds ?? Array.Empty<string>())}]");
                                }
                            }
                        }
                        if (repair.RepairMatch is not null)
                        {
                            var m = repair.RepairMatch;
                            var isNoDiffMatch = m.Score >= 1m && m.AddedMarketIds.Count == 0 && m.RemovedMarketIds.Count == 0;
                            if (isNoDiffMatch)
                            {
                                var noopFingerprint = $"{repair.GroupKey}|{m.CandidateGroupKey}|{m.Score}|NoDiff";
                                if (logThrottle.ShouldLog($"ALLOWLIST_REPAIR_NOOP:{repair.GroupKey}", noopFingerprint, true, 0))
                                    Console.WriteLine($"[ALLOWLIST_REPAIR_NOOP] Group={repair.GroupKey} Score={m.Score:0.##} Reason=NoDiff");
                            }
                            else if (lockProvider.IsHardLocked(repair.GroupKey))
                            {
                                var ignoredFingerprint = $"{repair.GroupKey}|{m.CandidateGroupKey}|{m.Score}|LockedGroup";
                                if (ShouldQuietLog("allowlist-repair", "ALLOWLIST_REPAIR_LOCKED_MATCH_IGNORED", ignoredFingerprint, LogImportance.Normal, groupKey: repair.GroupKey, everyNCycles: repairLogEveryNCycles, maxPerHour: repairSuggestionMaxPerHour))
                                    Console.WriteLine($"[ALLOWLIST_REPAIR_LOCKED_MATCH_IGNORED] Group={repair.GroupKey} Candidate={m.CandidateGroupKey} Score={m.Score:0.##} Reason=LockedGroup");
                            }
                            else
                            {
                                var matchFingerprint = $"{repair.GroupKey}|{m.CandidateGroupKey}|{m.Score}|{m.Confidence}|{string.Join(",", m.AddedMarketIds)}|{string.Join(",", m.RemovedMarketIds)}";
                                if (ShouldQuietLog("allowlist-repair", "ALLOWLIST_REPAIR_MATCH", matchFingerprint, LogImportance.Normal, groupKey: repair.GroupKey, everyNCycles: repairLogEveryNCycles, maxPerHour: repairSuggestionMaxPerHour))
                                    Console.WriteLine($"[ALLOWLIST_REPAIR_MATCH] Group={repair.GroupKey} Candidate={m.CandidateGroupKey} Score={m.Score:0.##} Confidence={m.Confidence} Added={m.AddedMarketIds.Count} Removed={m.RemovedMarketIds.Count}");
                            }
                        }
                        if (isRepairAction && (repair.ConsecutiveMatchMisses > 0 || repair.RecommendedAction == "NeedsManualReview" || repair.RecommendedAction == "DisableMissingMarkets"))
                        {
                            var noMatchFingerprint = options.Diagnostics.OperationalQuietMode
                                ? $"{repair.GroupKey}|{repair.RecommendedAction}|{repair.Reason}|{repair.ConsecutiveMatchMisses}"
                                : $"{repair.RepairSnapshotId}|{repair.GroupKey}|{repair.RecommendedAction}|{repair.Reason}|{repair.ConsecutiveMatchMisses}";
                            if (ShouldQuietLog("allowlist-repair", "ALLOWLIST_REPAIR_NO_MATCH", noMatchFingerprint, LogImportance.Normal, groupKey: repair.GroupKey, everyNCycles: repairLogEveryNCycles, maxPerHour: repairSuggestionMaxPerHour))
                                Console.WriteLine($"[ALLOWLIST_REPAIR_NO_MATCH] Group={repair.GroupKey} ConsecutiveMisses={repair.ConsecutiveMatchMisses} RequiredForDowngrade={options.AllowlistRepair.MatchFailureDowngradeCycles}");
                        }
                    }
                    var snapshot = VerifiedBasketScreener.BuildSnapshot(options.MultiOutcomeArbitrage.CostProfiles.ActiveProfile, "PolymarketApprox", verifiedScreenResults, unresolvedConfiguredGroups);
                    VerifiedBasketScreener.Export(screenerPath, snapshot);
                    foreach (var row in snapshot.ExperimentalCandidates)
                    {
                        experimentalCandidateCycle++;
                        var expState = stability.State(row.GroupKey);
                        var expFingerprint = $"{row.GroupKey}|{row.ActiveProfileNetEdge}|{row.ExperimentalProfileNetEdge}|{expState}|{row.ExecutionStatus}";
                        var expChanged = !lastExperimentalFingerprintByGroup.TryGetValue(row.GroupKey, out var prevExp) || prevExp != expFingerprint;
                        lastExperimentalFingerprintByGroup[row.GroupKey] = expFingerprint;
                        var expPeriodic = options.Logging.LogExperimentalCandidateEveryNCycles > 0 && experimentalCandidateCycle % options.Logging.LogExperimentalCandidateEveryNCycles == 0;
                        if (!options.Logging.LogExperimentalCandidateOnChangeOnly || expChanged || expPeriodic)
                            Console.WriteLine($"[EXPERIMENTAL_PROFILE_CANDIDATE] Group={row.GroupKey} ActiveProfile={snapshot.ActiveProfile} ActiveNet={row.ActiveProfileNetEdge} ExperimentalProfile={snapshot.ExperimentalProfile} ExperimentalNet={row.ExperimentalProfileNetEdge} Status={expState}");
                    }
                    var experimentalExportPath = Path.Combine(contentRootPath, "exports/experimental-profile-paper-candidates-latest.json");
                    File.WriteAllText(experimentalExportPath, System.Text.Json.JsonSerializer.Serialize(new { timestamp = DateTime.UtcNow, activeProfile = snapshot.ActiveProfile, experimentalProfile = snapshot.ExperimentalProfile, candidates = snapshot.ExperimentalCandidates, stabilityState = stability.Summaries(), paperActions = Array.Empty<object>(), blockedReasons = Array.Empty<object>() }, new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
                    foreach (var row in snapshot.VerifiedBaskets)
                    {
                        var prevState = basketStateByGroup.TryGetValue(row.GroupKey, out var pstate) ? pstate : VerifiedBasketState.NotExecutable;
                        var cur = stability.State(row.GroupKey);
                        basketStateByGroup[row.GroupKey] = cur;
                        if (prevState != cur) Console.WriteLine($"[VERIFIED_BASKET_STATE_CHANGE] Group={row.GroupKey} From={prevState} To={cur} Reason=Transition");
                    }
                    stability.Export(Path.Combine(contentRootPath, "exports/verified-basket-edge-history-latest.json"));
                    stability.ExportExecutionReadiness(Path.Combine(contentRootPath, "exports/execution-readiness-latest.json"), executionOptions.RequiredConsecutiveExecutionReadyScans);
                    var openGroupKeys = positionBook.GetOpenPositions().Select(x => $"{x.GroupKey}|{x.Strategy}").ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var verifiedRowsWithReadiness = snapshot.VerifiedBaskets.Select(row => BuildVerifiedScreenerRow(row, stability, executionOptions, openGroupKeys, verifiedExecution)).Take(100).ToArray();
                    var rankingRowsWithReadiness = snapshot.VerifiedBaskets.Select(row => BuildVerifiedScreenerRow(row, stability, executionOptions, openGroupKeys, verifiedExecution)).Take(100).ToArray();
                    state.SetVerifiedBasketScreener(new VerifiedBasketScreenerDto(snapshot.ActiveProfile, snapshot.ExperimentalProfile, snapshot.Timestamp, verifiedRowsWithReadiness, rankingRowsWithReadiness, snapshot.NearExecutableBaskets.Cast<object>().Take(25).ToArray(), snapshot.ExperimentalCandidates.Cast<object>().Take(100).ToArray(), snapshot.StableExperimentalCandidates.Cast<object>().Take(100).ToArray(), snapshot.ActiveProfileExecutable.Cast<object>().Take(100).ToArray(), snapshot.DiagnosticsOnlyPositive.Cast<object>().Take(100).ToArray(), snapshot.Profiles, snapshot.BestByActiveProfile, snapshot.BestByRawEdge, snapshot.BestByConservative, snapshot.BestByPolymarketApprox, snapshot.BestByRaw, snapshot.BestNearExecutable, snapshot.UnresolvedConfiguredGroups, stability.ReadinessSummaries(executionOptions.RequiredConsecutiveExecutionReadyScans).Cast<object>().ToArray()));
                    profileComparisonCycle++;
                    static long EdgeBucket(decimal value, decimal bucket) => bucket <= 0m ? (long)(value * 1000000m) : (long)Math.Round(value / bucket, MidpointRounding.AwayFromZero);
                    var profileComparisonFingerprint = ScanLogSummaryService.ProfileComparisonFingerprint(snapshot.VerifiedBaskets, options.Logging.ProfileComparisonSignificantNetDelta);
                    var shouldLogProfileComparisonSummary = options.Logging.LogProfileComparisonSummary && ShouldQuietLog("profile-comparison", "PROFILE_COMPARISON_SUMMARY", profileComparisonFingerprint, LogImportance.Normal, profileComparisonFingerprint, everyNCycles: options.Logging.LogProfileComparisonEveryNCycles, maxPerHour: options.Logging.MaxProfileComparisonDetailLogsPerHour);
                    if (options.Logging.LogProfileComparisonSummary)
                    {
                        foreach (var row in snapshot.VerifiedBaskets.Take(5))
                        {
                            var c = row.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals("Conservative", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m;
                            var p = row.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m;
                            var o = row.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals("OrderbookOnly", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m;
                            var r = row.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals("RawOnly", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m;
                            var detailFingerprint = $"{row.GroupKey}|{row.Classification}|active:{EdgeBucket(row.ActiveProfileNetEdge, options.Logging.ProfileComparisonSignificantNetDelta)}";
                            var detailEveryNCycles = options.Diagnostics.OperationalQuietMode ? 0 : options.Logging.LogProfileComparisonEveryNCycles;
                            if (ShouldQuietLog("profile-comparison", "PROFILE_COMPARISON", detailFingerprint, LogImportance.Normal, detailFingerprint, row.GroupKey, everyNCycles: detailEveryNCycles, maxPerHour: options.Logging.MaxProfileComparisonDetailLogsPerHour))
                                Console.WriteLine($"[PROFILE_COMPARISON] Group={row.GroupKey} Gross={row.GrossEdge} Conservative={c} PolymarketApprox={p} OrderbookOnly={o} RawOnly={r}");
                        }
                    }
                    if (shouldLogProfileComparisonSummary)
                    {
                        var bestConsValue = snapshot.BestByConservative?.ActiveProfileNetEdge;
                        var polyValues = snapshot.VerifiedBaskets.Select(gx => gx.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase))?.NetEdge).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                        var rawValues = snapshot.VerifiedBaskets.Select(gx => gx.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("RawOnly", StringComparison.OrdinalIgnoreCase))?.NetEdge).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                        var bestConsText = bestConsValue.HasValue ? bestConsValue.Value.ToString("0.####") : "N/A";
                        var bestPolyText = polyValues.Length > 0 ? polyValues.Max().ToString("0.####") : "N/A";
                        var profileBestRawText = rawValues.Length > 0 ? rawValues.Max().ToString("0.####") : "N/A";
                        var profileReason = snapshot.VerifiedBaskets.Count == 0 || (polyValues.Length == 0 && rawValues.Length == 0 && !bestConsValue.HasValue) ? " Reason=NoPricedVerifiedGroups" : string.Empty;
                        Console.WriteLine($"[PROFILE_COMPARISON_SUMMARY] Verified={snapshot.VerifiedBaskets.Count} NearExecutable={snapshot.NearExecutableBaskets.Count} BestConservative={bestConsText} BestPolymarketApprox={bestPolyText} BestRaw={profileBestRawText}{profileReason}");
                    }
                    var nearFingerprint = string.Join("|", snapshot.NearExecutableBaskets.OrderBy(x => x.GroupKey).Select(x => $"{x.GroupKey}:{EdgeBucket(x.ActiveProfileNetEdge, 0.002m)}:{EdgeBucket(x.CostReductionNeeded, 0.002m)}"));
                    var shouldLogNear = ShouldQuietLog("verified-basket", "NEAR_EXECUTABLE_VERIFIED_BASKET", nearFingerprint, LogImportance.Normal, nearFingerprint, everyNCycles: options.Logging.LogVerifiedBasketEveryNCycles, maxPerHour: options.Logging.MaxMultiVerifiedScanLogsPerHour);
                    nearExecutableFingerprint = nearFingerprint;
                    if (shouldLogNear)
                    {
                        foreach (var row in snapshot.NearExecutableBaskets)
                        {
                            var p = row.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m;
                            var raw = row.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals("RawOnly", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m;
                            Console.WriteLine($"[NEAR_EXECUTABLE_VERIFIED_BASKET] Group={row.GroupKey} ConservativeNet={row.ActiveProfileNetEdge} PolymarketApproxNet={p} RawOnly={raw} RequiredCostReduction={row.CostReductionNeeded}");
                            var q1 = row.QuantityScenarios.FirstOrDefault(x => x.Qty == 1m)?.NetEdgePerBasket ?? 0m;
                            var q5 = row.QuantityScenarios.FirstOrDefault(x => x.Qty == 5m)?.NetEdgePerBasket ?? q1;
                            var q10 = row.QuantityScenarios.FirstOrDefault(x => x.Qty == 10m)?.NetEdgePerBasket ?? q1;
                            var mode = row.QuantityScenarios.FirstOrDefault()?.DepthMode ?? "BestAskOnly";
                            var lim = row.QuantityScenarios.FirstOrDefault()?.LimitingLeg ?? "None";
                            Console.WriteLine($"[QUANTITY_SCENARIOS] Group={row.GroupKey} Qty1={q1} Qty5={q5} Qty10={q10} DepthMode={mode} LimitingLeg={lim}");
                        }
                    }
                    if (options.Logging.LogVerifiedBasketRanking && rankedScreen.Count > 0)
                    {
                        verifiedBasketRankingCycle++;
                        var rankingExecutableCount = rankedScreen.Count(x => x.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.ExecutableUnderActiveProfile);
                        var rankingFingerprint = $"{rankedScreen.Count}|{EdgeBucket(rankedScreen[0].ActiveProfileNetEdge, 0.002m)}|{rankedScreen[0].GroupKey}|{rankedScreen[0].Classification}|{rankingExecutableCount}";
                        var rankingOnChangeOnly = options.Logging.LogVerifiedBasketRankingOnChangeOnly && options.Logging.LogVerifiedBasketOnlyOnChangeRanking;
                        var shouldLogRanking = ShouldQuietLog("verified-basket", "VERIFIED_BASKET_RANKING", rankingFingerprint, LogImportance.Normal, everyNCycles: options.Diagnostics.OperationalQuietMode ? options.Logging.LogVerifiedRankingEveryNCycles : options.Logging.LogVerifiedBasketRankingEveryNCycles);
                        lastRankingFingerprint = rankingFingerprint;
                        if (shouldLogRanking) Console.WriteLine($"[VERIFIED_BASKET_RANKING] Count={rankedScreen.Count} BestGrossEdge={rankedScreen[0].GrossEdge} BestNetEdge={rankedScreen[0].ActiveProfileNetEdge} BestGroup={rankedScreen[0].GroupKey} Classification={rankedScreen[0].Classification}");
                    }
                    var bestVerifiedEdge = groupDiagnostics.Where(x=>x.BestEdge.HasValue).Select(x=>x.BestEdge!.Value).Cast<decimal?>().DefaultIfEmpty(null).Max();
                    var missingNoAskTotal = groupDiagnostics.Sum(x => x.MissingNoAskCount);
                    var noAskResolvedTotal = verifiedPricingExport.Sum(x => int.Parse(System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(x)).RootElement.GetProperty("noAskResolvedCount").ToString()));
                    var bestPricing = pricingDiagnostics.Where(x=>x.SkipReason!="MissingNoAsk").OrderByDescending(x=>x.NetEdge).FirstOrDefault();
                    var snapshotExportPath = Path.Combine(contentRootPath, "exports/verified-executable-opportunities-latest.json");
                    verifiedExecution.ExportSnapshot(snapshotExportPath, options.MultiOutcomeArbitrage.CostProfiles.ActiveProfile, promotedVerifiedOpportunities, preTradeResults, positionBook.OpenPositions);
                    var candidateBucket = Math.Max(1, options.Logging.CandidateScanBucketSize);
                    var candidateReasons = string.Join(",", multiOutcomeReport.RejectedByReason.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value}"));
                    candidateScanCycle++;
                    var rejectedCandidates = Math.Max(0, multiOutcomeReport.GroupsDetected - multiOutcomeReport.GroupsVerified);
                    var rejectedOnlyCandidateScan = multiOutcomeReport.GroupsDetected > 0 && rejectedCandidates == multiOutcomeReport.GroupsDetected && multiOutcomeReport.ExecutableGroups == 0 && activeExecutable == 0;
                    var candidateFingerprint = ScanLogSummaryService.CandidateScanFingerprint(multiOutcomeReport.GroupsDetected, multiOutcomeReport.TopSkipReason, multiOutcomeReport.RejectedByReason, candidateBucket, multiOutcomeReport.ExecutableGroups);
                    var rejectedOnlyBucketSize = options.Diagnostics.OperationalQuietMode ? 25 : Math.Max(1, options.Logging.CandidateScanBucketSize);
                    var rejectedOnlyReasonBucketSize = options.Diagnostics.OperationalQuietMode ? 25 : Math.Max(1, options.Logging.CandidateScanMaterialReasonDelta);
                    var rejectedOnlyMaterialFingerprint = ScanLogSummaryService.RejectedOnlyCandidateScanFingerprint(multiOutcomeReport.GroupsDetected, multiOutcomeReport.TopSkipReason, multiOutcomeReport.RejectedByReason, rejectedOnlyBucketSize, rejectedOnlyReasonBucketSize);
                    var throttleFingerprint = rejectedOnlyCandidateScan && options.Diagnostics.OperationalQuietMode ? rejectedOnlyMaterialFingerprint : candidateFingerprint;
                    var candidateImportance = (multiOutcomeReport.ExecutableGroups > 0 || activeExecutable > 0) ? LogImportance.Critical : LogImportance.Normal;
                    var shouldLogCandidate = ShouldQuietLog("multi-candidate", "MULTI_CANDIDATE_SCAN", throttleFingerprint, candidateImportance, throttleFingerprint, everyNCycles: options.Logging.LogCandidateScanEveryNCycles, maxPerHour: options.Logging.MaxMultiCandidateScanLogsPerHour);
                    var candidatePeriodic = options.Logging.LogCandidateScanEveryNCycles > 0 && candidateScanCycle % options.Logging.LogCandidateScanEveryNCycles == 0;
                    if (rejectedOnlyCandidateScan && ScanLogSummaryService.ShouldSuppressRejectedOnlyCandidateScan(options.Diagnostics.OperationalQuietMode, options.Logging.LogCandidateScanWhenOnlyRejected, true, rejectedOnlyMaterialFingerprint, lastRejectedOnlyCandidateScanFingerprint, candidatePeriodic))
                        shouldLogCandidate = false;
                    if (shouldLogCandidate)
                        Console.WriteLine($"[MULTI_CANDIDATE_SCAN] Candidates={multiOutcomeReport.GroupsDetected} Rejected={rejectedCandidates} TopReject={multiOutcomeReport.TopSkipReason} RejectedByReason={{{candidateReasons}}}");
                    lastCandidateScanFingerprint = candidateFingerprint;
                    if (rejectedOnlyCandidateScan) lastRejectedOnlyCandidateScanFingerprint = rejectedOnlyMaterialFingerprint;
                    var autoTopSkipReason = string.IsNullOrWhiteSpace(multiOutcomeReport.TopSkipReason) ? "None" : multiOutcomeReport.TopSkipReason;
                    var autoTopSkipCount = autoTopSkipReason == "None" ? 0 : (multiOutcomeReport.RejectedByReason.TryGetValue(autoTopSkipReason, out var autoTopSkipValue) ? autoTopSkipValue : 0);
                    strategyOrchestrator.RecordExternalResult(new OpportunityStrategyScanResult(
                        "AutoCandidateMultiOutcome",
                        StrategyMode.DiagnosticsOnly,
                        Scanned: multiOutcomeReport.GroupsDetected,
                        Candidates: multiOutcomeReport.GroupsDetected,
                        ExecutionCandidates: 0,
                        PaperOpened: 0,
                        DiagnosticsOnlyBlocked: multiOutcomeReport.ExecutableGroups,
                        PositiveEdges: 0,
                        ExecutionReady: 0,
                        BestEdge: multiOutcomeReport.BestCandidateEdge,
                        TopSkipReason: autoTopSkipReason,
                        TopSkipCount: autoTopSkipCount,
                        RejectedByReason: multiOutcomeReport.RejectedByReason),
                        multiOutcomeReport.GroupsDetected);
                    var bestConservativeNet = snapshot.BestByConservative?.ActiveProfileNetEdge;
                    decimal? bestExperimentalNet = ScanLogSummaryService.BestExperimentalNet(snapshot.ExperimentalCandidates);
                    var bestAlternateProfileNetValue = ScanLogSummaryService.BestAlternateProfileNet(snapshot.VerifiedBaskets, "PolymarketApprox");
                    var bestRawValues = snapshot.VerifiedBaskets.Select(gx => gx.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("RawOnly", StringComparison.OrdinalIgnoreCase))?.NetEdge).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                    decimal? bestRawValue = bestRawValues.Length > 0 ? bestRawValues.Max() : null;
                    var bestAlternateProfileText = bestAlternateProfileNetValue.HasValue ? bestAlternateProfileNetValue.Value.ToString("0.####") : "N/A";
                    var bestRawText = bestRawValue.HasValue ? bestRawValue.Value.ToString("0.####") : "N/A";
                    var unresolved = Math.Max(0, multiOutcomeValidator.LoadedAllowlistCount - verifiedResolved);
                    verifiedScanCycle++;
                    var bestExperimentalText = bestExperimentalNet.HasValue ? bestExperimentalNet.Value.ToString("0.####") : "N/A";
                    var verifiedScanFingerprint = $"{multiOutcomeValidator.LoadedAllowlistCount}|{verifiedResolved}|{unresolved}|{verifiedEvaluated}|{activeExecutable}|{experimentalCandidates}|{diagnosticsOnlyPositive}|{paperOpenedCount}|{suppressedDuplicateCount}|{verifiedMismatch}|{bestConservativeNet}|{bestExperimentalText}|{bestAlternateProfileText}|{bestRawText}";
                    var repairByGroupKey = GroupKeyDictionaryBuilder.BuildUniqueByGroupKey(repairReport.Groups, x => x.GroupKey, "Scanner.RepairReport.Groups", DuplicateGroupKeyPolicy.KeepMostRestrictive);
                    var unresolvedDiagnosticsForSampling = ScanLogSummaryService.BuildUnresolvedDiagnostics(allowlistedGroups, resolved, repairByGroupKey, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    var unresolvedSampleDecisions = unresolvedDiagnosticsForSampling
                        .Take(Math.Max(0, options.Logging.MaxVerifiedUnresolvedSamplesToLog))
                        .Select(ug =>
                        {
                            var sampleFingerprint = $"{ug.GroupKey}|{ug.Reason}|{ug.ValidationStatus}|{ug.HealthCategory}|{ug.RecommendedAction}|{ug.RepairConfidence}";
                            var shouldLogSample = logThrottle.ShouldLog($"VERIFIED_UNRESOLVED_SAMPLE:{ug.GroupKey}", sampleFingerprint, options.Logging.LogVerifiedUnresolvedOnChangeOnly, options.Logging.LogVerifiedUnresolvedEveryNCycles);
                            return new { Group = ug, ShouldLog = shouldLogSample };
                        })
                        .ToArray();
                    var loggedUnresolvedGroupKeys = unresolvedSampleDecisions.Where(x => x.ShouldLog).Select(x => x.Group.GroupKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var unresolvedDiagnostics = ScanLogSummaryService.BuildUnresolvedDiagnostics(allowlistedGroups, resolved, repairByGroupKey, loggedUnresolvedGroupKeys);
                    var unresolvedCounts = ScanLogSummaryService.UnresolvedCategoryCounts(unresolvedDiagnostics);
                    unresolved = unresolvedCounts.Total;
                    state.AddUnresolvedDiagnostics(unresolvedDiagnostics.Cast<object>());
                    var unresolvedExport = ScanLogSummaryService.BuildUnresolvedExport(unresolvedDiagnostics.Take(options.RuntimeState.MaxUnresolvedDiagnostics).ToArray(), DateTime.UtcNow);
                    var unresolvedExportPath = Path.Combine(contentRootPath, options.MultiOutcomeReview.ExportVerifiedUnresolvedGroupsPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(unresolvedExportPath)!);
                    File.WriteAllText(unresolvedExportPath, System.Text.Json.JsonSerializer.Serialize(unresolvedExport, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase }));
                    var suppressedUnresolvedKeys = unresolvedDiagnostics.Where(x => x.SuppressedInConsole).Select(x => x.GroupKey).ToArray();
                    var unresolvedGroupSetFingerprint = ScanLogSummaryService.VerifiedUnresolvedGroupSetFingerprint(unresolvedDiagnostics.Select(x => x.GroupKey));
                    var quietVerifiedFingerprint = $"configured:{multiOutcomeValidator.LoadedAllowlistCount}|resolved:{verifiedResolved}|unresolved:{unresolved}|" + ScanLogSummaryService.MultiVerifiedScanQuietFingerprint(unresolvedCounts, unresolvedGroupSetFingerprint, activeExecutable, paperOpenedCount, bestConservativeNet, options.Logging.VerifiedScanSignificantEdgeDelta);
                    verifiedScanFingerprint = options.Diagnostics.OperationalQuietMode
                        ? quietVerifiedFingerprint
                        : $"{verifiedScanFingerprint}|{unresolvedCounts.BrokenConfig}|{unresolvedCounts.NeedsRefresh}|{unresolvedCounts.ReviewOnly}|{unresolvedCounts.MonitoringOnly}|{unresolvedCounts.Other}|{unresolvedCounts.SamplesShown}|{unresolvedCounts.Suppressed}";
                    var verifiedImportance = paperOpenedCount > 0 ? LogImportance.Critical : activeExecutable > 0 ? LogImportance.Important : LogImportance.Normal;
                    var shouldLogVerifiedScan = ShouldQuietLog("multi-verified", "MULTI_VERIFIED_SCAN", verifiedScanFingerprint, verifiedImportance, verifiedScanFingerprint, everyNCycles: options.Logging.LogVerifiedScanEveryNCycles, maxPerHour: options.Logging.MaxMultiVerifiedScanLogsPerHour);
                    var finalBrokenConfigCount = effectiveBrokenConfig;
                    var finalReviewOnlyCount = effectiveReviewOnly;
                    if (shouldLogVerifiedScan) Console.WriteLine($"[MULTI_VERIFIED_SCAN] Configured={multiOutcomeValidator.LoadedAllowlistCount} Resolved={verifiedResolved} Unresolved={unresolved} BrokenConfigCount={finalBrokenConfigCount} NeedsRefreshCount={unresolvedCounts.NeedsRefresh} ReviewOnlyCount={finalReviewOnlyCount} MonitoringOnlyUnresolvedCount={unresolvedCounts.MonitoringOnly} OtherUnresolvedCount={unresolvedCounts.Other} UnresolvedSamplesShown={unresolvedCounts.SamplesShown} SuppressedUnresolvedSamples={unresolvedCounts.Suppressed} UnresolvedTotal={unresolvedCounts.Total} Evaluated={verifiedEvaluated} ActiveExecutable={activeExecutable} ExperimentalCandidates={experimentalCandidates} DiagnosticsOnlyPositive={diagnosticsOnlyPositive} PaperOpened={paperOpenedCount} SuppressedDuplicate={suppressedDuplicateCount} Mismatch={verifiedMismatch} BestActiveNet={(bestConservativeNet.HasValue ? bestConservativeNet.Value : 0m)} BestExperimentalNet={bestExperimentalText} BestAlternateProfileNet={bestAlternateProfileText} BestRaw={bestRawText}");
                    var verifiedRejectedByReason = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    foreach (var reasonGroup in groupDiagnostics.Concat(pricingDiagnostics.Select(x => new VerifiedGroupDiagnosticDto(x.GroupKey, x.Legs, x.Legs, 0, "Pricing", x.SkipReason, x.NetEdge, x.SkipReason.Contains("MissingNoAsk", StringComparison.OrdinalIgnoreCase) ? 1 : 0, 0, Array.Empty<string>(), Array.Empty<string>())))
                        .Where(x => !string.IsNullOrWhiteSpace(x.SkipReason) && !x.SkipReason.Equals("None", StringComparison.OrdinalIgnoreCase))
                        .GroupBy(x => x.SkipReason, StringComparer.OrdinalIgnoreCase))
                        verifiedRejectedByReason[reasonGroup.Key] = reasonGroup.Count();
                    if (unresolved > 0) verifiedRejectedByReason["Unresolved"] = unresolved;
                    if (finalBrokenConfigCount > 0) verifiedRejectedByReason["BrokenConfig"] = finalBrokenConfigCount;
                    if (unresolvedCounts.NeedsRefresh > 0) verifiedRejectedByReason["NeedsRefresh"] = unresolvedCounts.NeedsRefresh;
                    if (finalReviewOnlyCount > 0) verifiedRejectedByReason["ReviewOnly"] = finalReviewOnlyCount;
                    if (verifiedWouldOpenIfPaperEligible > 0) verifiedRejectedByReason["DiagnosticsOnly"] = verifiedWouldOpenIfPaperEligible;
                    if (verifiedRawPositiveOnly > 0) verifiedRejectedByReason["RawPositiveOnly"] = verifiedRawPositiveOnly;
                    if (verifiedAlternatePositive > 0) verifiedRejectedByReason["AlternateProfilePositive"] = verifiedAlternatePositive;
                    if (experimentalCandidates > 0) verifiedRejectedByReason["ExperimentalProfileCandidate"] = experimentalCandidates;
                    var verifiedTopSkip = verifiedRejectedByReason.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                    decimal? verifiedBestStrategyEdge = bestConservativeNet
                        ?? bestAlternateProfileNetValue
                        ?? bestRawValue;
                    strategyOrchestrator.RecordExternalResult(new OpportunityStrategyScanResult(
                        "VerifiedMultiOutcome",
                        StrategyMode.DiagnosticsOnly,
                        Scanned: multiOutcomeValidator.LoadedAllowlistCount,
                        Candidates: verifiedEvaluated,
                        ExecutionCandidates: 0,
                        PaperOpened: 0,
                        DiagnosticsOnlyBlocked: verifiedWouldOpenIfPaperEligible,
                        PositiveEdges: verifiedActivePositive + verifiedRawPositiveOnly + verifiedAlternatePositive + experimentalCandidates,
                        ExecutionReady: verifiedWouldOpenIfPaperEligible,
                        OrderbookUnavailable: verifiedRejectedByReason.Where(x => x.Key.Contains("Orderbook", StringComparison.OrdinalIgnoreCase) || x.Key.Contains("MissingNoAsk", StringComparison.OrdinalIgnoreCase)).Sum(x => x.Value),
                        BestEdge: verifiedBestStrategyEdge,
                        TopSkipReason: verifiedTopSkip.Key ?? "None",
                        TopSkipCount: verifiedTopSkip.Value,
                        RejectedByReason: verifiedRejectedByReason,
                        VerifiedActiveConservativePositive: verifiedActivePositive,
                        VerifiedActiveConservativeExecutable: activeExecutable,
                        VerifiedRawPositiveOnly: verifiedRawPositiveOnly,
                        VerifiedAlternateProfilePositive: verifiedAlternatePositive,
                        VerifiedExperimentalProfileCandidate: experimentalCandidates,
                        VerifiedDiagnosticsOnlyBlocked: verifiedWouldOpenIfPaperEligible,
                        VerifiedWouldOpenIfPaperEligible: verifiedWouldOpenIfPaperEligible,
                        VerifiedRejectedByCostProfile: verifiedRejectedByCostProfile,
                        VerifiedRejectedByStability: verifiedRejectedByStability,
                        VerifiedRejectedByMissingNoAsk: verifiedRejectedByMissingNoAsk,
                        VerifiedRejectedByUnresolvedGroup: verifiedRejectedByUnresolvedGroup,
                        VerifiedRejectedByRisk: verifiedRejectedByRisk,
                        VerifiedPricingBlockedByMissingNoAsk: verifiedPricingBlockedByMissingNoAsk,
                        VerifiedPricingBlockedByOrderbookUnavailable: verifiedPricingBlockedByOrderbookUnavailable,
                        VerifiedPricingBlockedByQuarantinedToken: verifiedPricingBlockedByQuarantinedToken,
                        VerifiedPricingBlockedByEmptyBook: verifiedPricingBlockedByEmptyBook,
                        VerifiedPricingBlockedByCircuitBreakerActive: verifiedPricingBlockedByCircuitBreakerActive,
                        VerifiedPricingBlockedByMarketOrderbookQuarantined: verifiedPricingBlockedByMarketOrderbookQuarantined,
                        VerifiedPricingBlockedByTokenQuarantined: verifiedPricingBlockedByTokenQuarantined,
                        VerifiedWouldOpenBlockedByFill: verifiedBlockedByFill,
                        VerifiedWouldOpenBlockedByDepth: verifiedBlockedByDepth,
                        VerifiedWouldOpenBlockedByUnknown: verifiedBlockedByUnknown),
                        multiOutcomeValidator.LoadedAllowlistCount);
                    if (!unresolvedCounts.InvariantOk)
                        Console.WriteLine(unresolvedCounts.ToCounterErrorLogLine());
                    var unresolvedFingerprint = ScanLogSummaryService.VerifiedUnresolvedCategoryFingerprint(unresolvedCounts, unresolvedGroupSetFingerprint);
                    if (ShouldQuietLog("multi-verified", "VERIFIED_UNRESOLVED_BREAKDOWN", unresolvedFingerprint, LogImportance.Normal, unresolvedFingerprint, everyNCycles: options.Logging.LogVerifiedUnresolvedEveryNCycles, maxPerHour: options.Logging.MaxMultiVerifiedScanLogsPerHour))
                        Console.WriteLine(unresolvedCounts.ToBreakdownLogLine());
                    var unresolvedSummary = ScanLogSummaryService.UnresolvedSampleSummary(unresolvedCounts.Total, unresolvedCounts.SamplesShown, suppressedUnresolvedKeys);
                    if (unresolvedSummary.Suppressed > 0 && ShouldQuietLog("multi-verified", "VERIFIED_UNRESOLVED_SUMMARY", unresolvedFingerprint, LogImportance.Normal, unresolvedFingerprint, everyNCycles: options.Logging.LogVerifiedUnresolvedEveryNCycles, maxPerHour: options.Logging.MaxMultiVerifiedScanLogsPerHour))
                        Console.WriteLine(unresolvedSummary.ToLogLine());
                    foreach (var ug in unresolvedDiagnostics.Where(x => x.SampleLogged))
                    {
                        Console.WriteLine($"[VERIFIED_UNRESOLVED_SAMPLE] {System.Text.Json.JsonSerializer.Serialize(new { ug.GroupKey, ug.Reason, ug.ValidationStatus, ug.HealthCategory, ug.RecommendedAction, ug.RepairConfidence })}");
                    }

                    var triageRows = resolved.Select(g =>
                    {
                        var gd = groupDiagnostics.FirstOrDefault(x => x.GroupKey == g.GroupKey);
                        var pd = pricingDiagnostics.FirstOrDefault(x => x.GroupKey == g.GroupKey);
                        var hasSuggestedPrune = verifiedPricingExport.Any(x => System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(x)).RootElement.TryGetProperty("suggestedPrunedAllowlistTemplate", out var tpl) && tpl.ValueKind == System.Text.Json.JsonValueKind.Object && System.Text.Json.JsonSerializer.Serialize(x).Contains($"\"groupKey\":\"{g.GroupKey}\""));
                        var missing = gd?.MissingNoAskCount ?? 0;
                        var classification = (pd?.SkipReason == "FormulaOrNormalizationError" || pd?.SkipReason == "InvalidGuaranteedPayoutFormula")
                            ? "FormulaError"
                            : (missing > 0 || hasSuggestedPrune || ((pd?.Legs ?? g.ResolvedMarkets.Count) - missing) < (pd?.Legs ?? g.ResolvedMarkets.Count) || (pd?.SkipReason == "MissingNoAsk"))
                            ? "NeedsPruning"
                            : pd?.SkipReason == "Executable"
                            ? "Executable"
                            : (pd?.GrossEdge ?? decimal.MinValue) > 0m && (pd?.NetEdge ?? 0m) <= 0m ? "RawPositiveNetNegative"
                            : (pd?.GrossEdge ?? 0m) < options.MultiOutcomeArbitrage.VerifiedGroupTriage.HopelessGrossEdgeThreshold ? "HopelessNegative"
                            : "FarFromExecutable";
                        var action = classification switch
                        {
                            "Executable" => "KeepEnabled",
                            "RawPositiveNetNegative" => "KeepForMonitoring",
                            "HopelessNegative" => "DisableUntilBetterPricing",
                            "NeedsPruning" => "PruneMissingNoAskLegs",
                            _ => "NeedsManualReview"
                        };
                        return new
                        {
                            groupKey = g.GroupKey,
                            enabled = true,
                            resolved = g.ValidationStatus == "VerifiedGroupResolved",
                            evaluated = gd is not null,
                            executable = pd?.SkipReason == "Executable",
                            legs = pd?.Legs ?? g.ResolvedMarkets.Count,
                            noAskResolved = (pd?.Legs ?? g.ResolvedMarkets.Count) - missing,
                            missingNoAsk = missing,
                            guaranteedPayout = pd?.GuaranteedPayout ?? 0m,
                            noAskSum = pd?.NoAskSum ?? 0m,
                            grossEdge = pd?.GrossEdge ?? 0m,
                            netEdgeConservative = pd?.NetEdge ?? 0m,
                            netEdgeRawOnly = pd?.GrossEdge ?? 0m,
                            topReject = pd?.SkipReason ?? gd?.SkipReason ?? g.RejectionReason,
                            classification,
                            recommendedConfigAction = action,
                            reason = gd?.SkipReason ?? g.RejectionReason
                        };
                    }).ToArray();
                    var triagePath = Path.Combine(contentRootPath, options.MultiOutcomeReview.ExportVerifiedTriagePath);
                    Directory.CreateDirectory(Path.GetDirectoryName(triagePath)!);
                    File.WriteAllText(triagePath, System.Text.Json.JsonSerializer.Serialize(triageRows, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    var nextGroups = reviewReport
                        .Select(x => System.Text.Json.JsonSerializer.SerializeToNode(x)!.AsObject())
                        .Where(n => (n["recommendedAction"]?.GetValue<string>() ?? "") == "SafeCandidateForManualVerification")
                        .Take(10)
                        .Select(n => new
                        {
                            groupKey = n["groupKey"]?.GetValue<string>() ?? "",
                            title = n["title"]?.GetValue<string>() ?? "",
                            detectedMarketsCount = n["detectedMarketsCount"]?.GetValue<int>() ?? 0,
                            pricedLegs = n["pricedLegs"]?.GetValue<int>() ?? 0,
                            missingNoAsk = n["missingPrices"]?.GetValue<int>() ?? 0,
                            estimatedGrossEdge = n["estimatedGrossEdge"]?.GetValue<decimal?>(),
                            estimatedNetEdgeConservative = n["estimatedNetEdgeConservative"]?.GetValue<decimal?>(),
                            rawOnlyEdge = n["estimatedNetEdgeRawOnly"]?.GetValue<decimal?>(),
                            candidateQualityScore = n["candidateQualityScore"]?.GetValue<int>() ?? 0,
                            safetyClassification = "SafeForManualVerification",
                            recommendedAction = n["recommendedAction"]?.GetValue<string>() ?? "",
                            suggestedAllowlistTemplate = n["suggestedAllowlistTemplate"]
                        }).ToArray<object>();
                    var nextPath = Path.Combine(contentRootPath, options.MultiOutcomeReview.ExportNextGroupsToVerifyPath);
                    Directory.CreateDirectory(Path.GetDirectoryName(nextPath)!);
                    File.WriteAllText(nextPath, System.Text.Json.JsonSerializer.Serialize(nextGroups, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    var suggestedPath = Path.Combine(contentRootPath, options.MultiOutcomeReview.ExportSuggestedVerifiedGroupsPath);
                    File.WriteAllText(suggestedPath, System.Text.Json.JsonSerializer.Serialize(new
                    {
                        metadata = new { note = "Suggested only. Does not overwrite config/verified-multi-outcome-groups.json.", generatedAtUtc = DateTime.UtcNow },
                        groups = triageRows.Select(t => new
                        {
                            t.groupKey,
                            recommendedEnabled = t.recommendedConfigAction is "KeepEnabled" or "KeepForMonitoring",
                            t.classification,
                            t.recommendedConfigAction,
                            suggestionReason = t.classification == "NeedsPruning" ? "MissingNoAsk legs excluded" : t.reason,
                            suggestedAllowlistTemplate = verifiedPricingExport
                                .Select(x => System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(x)) as System.Text.Json.Nodes.JsonObject)
                                .Where(e => e is not null && e.TryGetPropertyValue("groupKey", out var gk) && gk?.GetValue<string>() == t.groupKey)
                                .Select(e => e!.TryGetPropertyValue("suggestedPrunedAllowlistTemplate", out var tpl) ? tpl : null)
                                .FirstOrDefault()
                        }).ToArray(),
                        keepEnabledGroups = triageRows.Where(x => x.recommendedConfigAction is "KeepEnabled" or "KeepForMonitoring").Select(x => x.groupKey).ToArray(),
                        disableRecommendedGroups = triageRows.Where(x => x.recommendedConfigAction == "DisableUntilBetterPricing").Select(x => x.groupKey).ToArray(),
                        needsPruningGroups = triageRows.Where(x => x.recommendedConfigAction == "PruneMissingNoAskLegs").Select(x => x.groupKey).ToArray(),
                        nextGroupsToVerify = nextGroups
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                    portfolioCycle++;
                    var openPositionFingerprint = string.Join("|", positionBook.OpenPositions.Select(p => $"{p.GroupKey}:{p.Status}:{p.Quantity}").OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
                    var portfolioFingerprint = $"{multiOutcomeValidator.LoadedAllowlistCount}|{verifiedExecutable}|{bestPricing?.GroupKey}|{bestPricing?.NetEdge}|{openPositionFingerprint}";
                    var shouldLogPortfolio = ShouldQuietLog("multi-verified", "VERIFIED_GROUP_PORTFOLIO", portfolioFingerprint, LogImportance.Normal, portfolioFingerprint, everyNCycles: options.Logging.LogPortfolioEveryNCycles, maxPerHour: options.Logging.MaxMultiVerifiedScanLogsPerHour);
                    if (shouldLogPortfolio)
                        Console.WriteLine($"[VERIFIED_GROUP_PORTFOLIO] Total={multiOutcomeValidator.LoadedAllowlistCount} Keep={triageRows.Count(x => x.recommendedConfigAction is "KeepEnabled" or "KeepForMonitoring")} DisableRecommended={triageRows.Count(x => x.recommendedConfigAction == "DisableUntilBetterPricing")} NeedsPruning={triageRows.Count(x => x.recommendedConfigAction == "PruneMissingNoAskLegs")} Executable={verifiedExecutable} Best={(bestPricing?.GroupKey ?? "N/A")}");
                    lastPortfolioFingerprint = portfolioFingerprint;
                    exportService.ExportVerifiedPricing(verifiedPricingExport);
                    state.SetMultiOutcomeDiagnostics(new MultiOutcomeDiagnosticsDto(multiOutcomeReport.GroupsDetected,multiOutcomeReport.GroupsVerified,Math.Max(0,multiOutcomeReport.GroupsDetected - multiOutcomeReport.GroupsVerified),multiOutcomeReport.ExecutableGroups,multiOutcomeReport.RejectedByReason.ToDictionary(k=>k.Key,v=>v.Value),multiOutcomeReport.TopRejectedSamples.Take(25).Select(x=>$"{x.GroupKey}:{x.Reason}").ToArray(),Array.Empty<string>(),multiOutcomeValidator.LoadedAllowlistCount,Array.Empty<string>(),Guid.NewGuid().ToString("N"),DateTime.UtcNow,state.NextSeq(),multiOutcomeValidator.LoadedAllowlistCount,verifiedResolved,verifiedMismatch,verifiedEvaluated,verifiedExecutable,groupDiagnostics,skipReason,pricingDiagnostics,basketCostDiagnostics));
                }
            }

            var pairs = pairLoader.Load();
            foreach (var pair in pairs.Where(x=>x.Enabled && x.RiskLevel == MarketPairRiskLevel.Verified))
            {
                var pm = filtered.FirstOrDefault(x => x.id == pair.PolymarketMarketId);
                if (pm is null) continue;
                var polySnap = await orderbookService.GetBinarySnapshotAsync(pm, stoppingToken);
                if (polySnap is null) continue;
                var kalshiSnap = await kalshiBookService.GetNormalizedOrderbookAsync(pair.KalshiTicker, stoppingToken);
                if (kalshiSnap is null) continue;
                var polyNorm = PolymarketOrderbookNormalizer.Normalize(polySnap);
                foreach (var opp in crossEngine.Evaluate(pair, polyNorm, kalshiSnap, positionBook.OpenPositions.Count))
                {
                    if (!opp.IsExecutable) continue;
                    monitor.AddExternalOpportunity(opp.PairKey, opp.Strategy, $"{opp.Leg1Exchange}:{opp.Leg1Side}+{opp.Leg2Exchange}:{opp.Leg2Side}", opp.EdgePerShare, opp.ExpectedProfit, opp.GrossCost, opp.GuaranteedPayout, opp.ExecutableQty);
                }
            }
            var cycleTop = monitor.GetTopCycleRecords(200,false);
            var executableCount = cycleTop.Count(x=>x.IsExecutable);
            var batchMarketIds = filtered.Select(x => x.id ?? "").Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            var distinctMarketIdsInBatch = batchMarketIds.Distinct(StringComparer.Ordinal).Count();
            var batchFingerprint = string.Join("|", batchMarketIds);
            if (batchFingerprint == lastBatchFingerprint) repeatedBatchCount++; else repeatedBatchCount = 1;
            lastBatchFingerprint = batchFingerprint;
            if (repeatedBatchCount >= 3)
            {
                duplicateBatchWarnings++;
                Console.WriteLine($"[SCAN_WARNING] Same market batch repeated {repeatedBatchCount} times. Rolling cursor may be stuck. Range={batchStartIndex}-{batchEndIndex}");
            }
            if (executableCount > 0)
            {
                if (!options.LogOnlyExecutableOpportunities) monitor.PrintCycleRanking(top: 10, executableOnly: false);
                else monitor.PrintCycleRanking(top: 10, executableOnly: true);
                emptyCycles = 0;
            }
            else
            {
                emptyCycles++;
                if (!options.Diagnostics.OperationalQuietMode && options.LogEmptyExecutableRanking && (options.LogEmptyOpportunityCycles || options.LogNoOpportunityCycles))
                    Console.WriteLine($"[SCAN] Markets={filtered.Count} Books={scanStats.BookOk} Candidates={scanStats.Candidates} Positive={scanStats.PositiveEdgeFound} Executable=0 BestEdge={(scanStats.Candidates>0 && scanStats.BestEdgeSeen.HasValue ? scanStats.BestEdgeSeen.Value.ToString("0.####") : "N/A")} NearMiss={scanStats.NearMisses?.Count ?? 0} DurationMs={(long)(DateTime.UtcNow - started).TotalMilliseconds}");
            }
            var shouldLogBatchScan = options.LogCompactScanSummary && ScanLogSummaryService.ShouldLogBatchScan(
                options.Diagnostics.OperationalQuietMode,
                options.LogEveryScanCycle,
                options.Logging.LogBatchScanInQuietMode,
                (int)Math.Min(int.MaxValue, scanId),
                options.Logging.LogScanProgressEveryNBatches,
                singleMarketFullCycleComplete,
                fullCoverageCompletedThisBatch,
                executableCount > 0,
                false);
            if (shouldLogBatchScan)
            {
                var coveragePercent = scanPool.Count == 0 ? 0 : Math.Min(100m, (decimal)Math.Min(scanPool.Count, cyclesCompletedSinceDiscovery * Math.Max(1, batchSize)) / scanPool.Count * 100m);
                var estimatedCyclesToFullCoverage = batchSize == 0 ? 0 : (int)Math.Ceiling(scanPool.Count / (double)batchSize);
                var topSkipReason = scanStats.SkipReasons?.OrderByDescending(x => x.Value).FirstOrDefault().Key ?? "None";
                var obStats = orderbookService.GetStats();
                var missSample = options.Logging.LogBookCacheMissDetails ? string.Join(",", orderbookService.GetBookCacheMissSamples(options.Logging.BookCacheMissSampleSize)) : "disabled";
                Console.WriteLine($"[SCAN] Id={scanId} Pool={scanPool.Count} Batch={batchSize} Range={batchStartIndex}-{batchEndIndex} NextOffset={currentRollingOffsetAfter} Coverage={coveragePercent:0.0}% Markets={filtered.Count} Tokens={filtered.Count*2} Books={scanStats.BookOk} BookCacheMisses={obStats.BookCacheMisses} BookCacheMissSample={missSample} Snapshots={filtered.Count} Candidates={scanStats.Candidates} Positive={scanStats.PositiveEdgeFound} Executable={scanStats.Executed} BestEdge={(scanStats.Candidates>0 && scanStats.BestEdgeSeen.HasValue ? scanStats.BestEdgeSeen.Value.ToString("0.####") : "N/A")} NearMiss={scanStats.NearMisses?.Count ?? 0} TopSkip={topSkipReason} FirstMarket={filtered.FirstOrDefault()?.id ?? "-"} LastMarket={filtered.LastOrDefault()?.id ?? "-"} DistinctMarketIds={distinctMarketIdsInBatch} DurationMs={(long)(DateTime.UtcNow - started).TotalMilliseconds}");
            }
            monitor.FlushCsv();
            SetScannerStage("ExportWrite", "SyncRuntimeState");
            UpdateDiscoveryGuardRuntimeState();
            SyncRuntimeState(state, monitor, positionBook, executionJournalPath, executionPolicy, orderbookService, paper, filtered.Count, started, null, scanStats, filtering, lastDiscoverySummary, rollingOffset, options.ScanBatchSize, discoveredMarkets.Count, discoveryStartedAt, discoveryCompletedAt, emptyCycles, options.MarketScanLimit, effectiveMarketLimit, options.MaxMarketsToDiscover, options, poolLimitReason, multiOutcomeReport, contentRootPath);
            if (options.Caches.ClearOrderbookCacheAfterScan) orderbookService.ClearExpiredCache();
            state.SetRuntimeCounts(repairHistoryCount: allowlistRepairService.RepairHistorySnapshotCount, dryRunOrderPlansCount: verifiedExecution.DryRunPlanCount, fillSimulationsCount: verifiedExecution.FillSimulationCount, executionAuditCount: verifiedExecution.AuditCount, orderbookCacheCount: orderbookService.CacheEntryCount, marketCacheCount: discoveredMarkets.Count);
            memoryGuard.Check(state, options, () => { orderbookService.ClearAllCache(); orderbookService.TrimAllBoundedStores(); quietLogGate.TrimExpired(); }, contentRootPath);
            SetScannerStage("SignalRPublish", "PushUiUpdates");
            await PushUiUpdates(state, hub, uiLogger, options, verifiedExecution, contentRootPath);
            var scanDurationMs = (long)(DateTime.UtcNow - started).TotalMilliseconds;
            var paperOpenedThisScan = Math.Max(0, state.PaperExecutionsCount - paperOpenBeforeScan);
            scannerSummaryBatches++;
            scannerSummaryMarketsScanned += filtered.Count;
            scannerSummaryDurationMs += scanDurationMs;
            scannerSummaryPositive += scanStats.PositiveEdgeFound;
            scannerSummaryExecutable += executableCount;
            scannerSummaryPaperOpened += paperOpenedThisScan;
            var scannerLifecycleImportance = executableCount > 0 || paperOpenedThisScan > 0 ? LogImportance.Critical : LogImportance.Normal;
            if (ShouldLogScannerChannel("scan_end", $"scan_end|executable:{executableCount > 0}|paper:{paperOpenedThisScan > 0}", scannerLifecycleImportance))
                uiLogger.LogInfo("scanner", $"{{\"event\":\"scan_end\",\"durationMs\":{scanDurationMs},\"marketsScanned\":{filtered.Count},\"detected\":{cycleTop.Count},\"executable\":{executableCount}}}");
            var summaryNow = DateTime.UtcNow;
            if (ScanLogSummaryService.ShouldEmitScannerSummary(summaryNow, lastScannerSummaryAt, options.Logging.LogScannerSummaryEveryMinutes, fullCoverageCompletedThisBatch, false, paperOpenedThisScan > 0, options.Logging.LogScannerSummaryOnEveryFullCycle, options.Logging.LogScannerSummaryOnError, options.Logging.LogScannerSummaryOnPaperOpen))
            {
                var avgBatchMs = scannerSummaryBatches == 0 ? 0 : scannerSummaryDurationMs / scannerSummaryBatches;
                var uptime = summaryNow - scannerStartedAt;
                var uptimeText = uptime.ToString(@"dd\.hh\:mm\:ss");
                Console.WriteLine($"[SCANNER_SUMMARY] Uptime={uptimeText} ScanId={scanId} Pool={scanPool.Count} Offset={rollingOffset} CyclesCompleted={cyclesCompletedSinceDiscovery} Batches={scannerSummaryBatches} MarketsScanned={scannerSummaryMarketsScanned} AvgBatchMs={avgBatchMs} Errors={scannerSummaryErrors} Positive={scannerSummaryPositive} Executable={scannerSummaryExecutable} PaperOpened={scannerSummaryPaperOpened}");
                lastScannerSummaryAt = summaryNow;
                scannerSummaryBatches = 0;
                scannerSummaryMarketsScanned = 0;
                scannerSummaryDurationMs = 0;
                scannerSummaryErrors = 0;
                scannerSummaryPositive = 0;
                scannerSummaryExecutable = 0;
                scannerSummaryPaperOpened = 0;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            scannerSummaryErrors++;
            var errorSummaryNow = DateTime.UtcNow;
            if (options.Logging.LogScannerSummaryOnError)
            {
                var errorUptimeText = (errorSummaryNow - scannerStartedAt).ToString(@"dd\.hh\:mm\:ss");
                Console.WriteLine($"[SCANNER_SUMMARY] Reason=Error Uptime={errorUptimeText} ScanId={scanId} Pool={discoveredMarkets.Count} Offset={rollingOffset} CyclesCompleted={cyclesCompletedSinceDiscovery} Batches={scannerSummaryBatches} MarketsScanned={scannerSummaryMarketsScanned} AvgBatchMs={(scannerSummaryBatches == 0 ? 0 : scannerSummaryDurationMs / scannerSummaryBatches)} Errors={scannerSummaryErrors} Positive={scannerSummaryPositive} Executable={scannerSummaryExecutable} PaperOpened={scannerSummaryPaperOpened}");
                lastScannerSummaryAt = errorSummaryNow;
            }
            lastError = ex.Message;
            var errorContext = new ScannerExceptionContext(
                scannerStage,
                scannerComponent,
                scanId,
                singleMarketFullCycleId,
                rollingOffset,
                $"{batchStartIndex}-{batchEndIndex}",
                scannerState.State.ToString(),
                memoryGuard.IsScannerPausedByMemory,
                stoppingToken.IsCancellationRequested);
            var scannerError = scannerErrors.Record(ex, errorContext, Console.WriteLine);
            uiLogger.LogError("scanner", $"{{\"event\":\"scan_error\",\"type\":\"{scannerError.Type}\",\"stage\":\"{scannerError.Stage}\",\"component\":\"{scannerError.Component}\",\"message\":\"{ex.Message.Replace("\"", "'")}\"}}");
            if (scannerError.Faulted)
            {
                scannerState.TryFault(Console.WriteLine);
                state.SetControls(state.Controls with { IsPaused = true, Reason = "SCANNER_FAULTED", UpdatedAtUtc = DateTime.UtcNow, Sequence = state.NextSeq() });
                state.SetStatus(state.Status with { ScannerActive = false, LastScanTime = DateTime.UtcNow });
                Console.WriteLine($"[SCANNER_FAULTED] Reason=RepeatedSameException Type={scannerError.Type} Count={scannerError.SameExceptionCount} Action=Paused");
            }
            SetScannerStage("ExportWrite", "SyncRuntimeState");
            SyncRuntimeState(state, monitor, positionBook, executionJournalPath, executionPolicy, orderbookService, paper, 0, started, lastError, new SingleMarketScanStats(0,0,0,0,0,0,0,0), filtering, lastDiscoverySummary, rollingOffset, options.ScanBatchSize, discoveredMarkets.Count, discoveryStartedAt, discoveryCompletedAt, emptyCycles, options.MarketScanLimit, 0, options.MaxMarketsToDiscover, options, "Error", new MultiOutcomeGroupArbEngine.MultiOutcomeScanReport(0,0,0,0,0,0,0,0m,0m,0m,string.Empty,"Error",new Dictionary<string,int>(),Array.Empty<MultiOutcomeGroupArbEngine.RejectedSample>(),Array.Empty<MultiOutcomeGroupArbEngine.CandidateGroupReview>()), contentRootPath);
            var backoff = scannerErrors.NextBackoff(scannerError.Faulted);
            await Task.Delay(backoff, stoppingToken);
            if (scannerError.Faulted) continue;
        }

        scannerErrors.ResetBackoff();
        await Task.Delay(options.ScanIntervalMs, stoppingToken);
    }
}


static AllowlistRepairReport BuildEmptyAllowlistRepairReport(int configuredGroups, string reason)
{
    var snapshotId = $"repair-error-{DateTime.UtcNow:yyyyMMddHHmmss}";
    var summary = new AllowlistRepairSummary(configuredGroups, 0, 0, 0, 0, 0, 0, 0, 0, 0, configuredGroups == 0);
    var counts = new AllowlistRepairCategoryCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    return new AllowlistRepairReport(
        snapshotId,
        DateTime.UtcNow,
        summary,
        counts,
        summary.InvariantOk,
        new AllowlistRepairSnapshot(snapshotId, DateTime.UtcNow, snapshotId, string.Empty, 0, configuredGroups, "RepairError", Array.Empty<AllowlistRepairGroup>()),
        configuredGroups,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        Array.Empty<AllowlistRepairGroup>(),
        Array.Empty<AllowlistRepairSuggestion>(),
        $"Repair diagnostics failed safely: {reason}");
}

static AllowlistRepairPatchPreview BuildEmptyAllowlistRepairPatchPreview(string snapshotId, string sourceConfigPath)
    => new(
        DateTime.UtcNow,
        snapshotId,
        sourceConfigPath,
        "ManualPreviewOnly",
        false,
        new AllowlistRepairPatchSummary(0, 0, 0, 0, 0, 0, 0, 0, 0),
        Array.Empty<AllowlistRepairPatchItem>(),
        new AllowlistRepairPostApplyValidationPlan(Array.Empty<string>(), 0, 0, 0, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
        new AllowlistRepairManualApplyInstructions(string.Empty, sourceConfigPath, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
        new AllowlistPatchedPreviewValidation(0, 0, 0, true, Array.Empty<string>()));

static async Task PushUiUpdates(BotRuntimeState state, IHubContext<BotHub> hub, IBotUiLogger logger, TradingBotOptions options, VerifiedBasketExecutionCoordinator verifiedExecution, string contentRootPath)
{
    try
    {
        var opportunitiesPayload = TrimPayload(state, "opportunitiesUpdated", state.Opportunities().TakeLast(options.SignalR.MaxPayloadItems).ToArray(), options);
        state.AddSignalREvent("opportunitiesUpdated");
        await hub.Clients.All.SendAsync("opportunitiesUpdated", opportunitiesPayload);
        state.AddSignalREvent("tradeLogUpdated");
        await hub.Clients.All.SendAsync("tradeLogUpdated", TrimPayload(state, "tradeLogUpdated", state.Trades().TakeLast(Math.Min(300, options.SignalR.MaxPayloadItems)).ToArray(), options));
        verifiedExecution.ExportAudit(Path.Combine(contentRootPath, "exports/execution-audit-latest.json"));
        verifiedExecution.ExportDryRunPlans(Path.Combine(contentRootPath, "exports/dry-run-order-plans-latest.json"), options.MultiOutcomeArbitrage.CostProfiles.ActiveProfile, true);
        verifiedExecution.ExportFillSimulations(Path.Combine(contentRootPath, "exports/dry-run-fill-simulations-latest.json"));
        File.WriteAllText(Path.Combine(contentRootPath, "exports/paper-positions-latest.json"), System.Text.Json.JsonSerializer.Serialize(state.Positions().TakeLast(200), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(contentRootPath, "exports/paper-executions-latest.json"), System.Text.Json.JsonSerializer.Serialize(state.SingleMarketExecutions().TakeLast(options.RuntimeState.MaxSingleMarketExecutions), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(contentRootPath, "exports/paper-account-latest.json"), System.Text.Json.JsonSerializer.Serialize(new {
            initialCash = 1000m,
            cash = state.Status.Cash,
            locked = state.Status.LockedCapital,
            lockedCapital = state.Status.LockedCapital,
            equity = state.Status.Equity,
            realizedPnl = state.Status.RealizedPnl,
            unrealizedPnl = state.Positions().Where(p => p.MtmStatus != "Incomplete").Sum(p => p.UnrealizedPnl),
            openPositions = state.Status.OpenPositions,
            openPositionsCount = state.Status.OpenPositions,
            openBasketPositionsCount = state.Status.OpenPositions,
            totalExposure = state.Status.LockedCapital,
            openExposure = state.Status.LockedCapital,
            positionsByStrategy = state.Positions().GroupBy(p => p.Strategy ?? "unknown", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
            hourlyOpenCount = state.PaperOpenCountLastHour,
            lastOpenAt = state.Positions().Select(p => p.OpenedAt).DefaultIfEmpty().Max(),
            blockedCountsByReason = state.PaperPretradeRejectsByReason,
            lastUpdatedAt = state.Status.LastScanTime
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        ExportRuntimeSoakStatus(state, options, contentRootPath);
        state.AddSignalREvent("positionsUpdated");
        await hub.Clients.All.SendAsync("positionsUpdated", TrimPayload(state, "positionsUpdated", state.Positions().TakeLast(options.RuntimeState.MaxPaperPositions).ToArray(), options));
        state.AddSignalREvent("scannerStatsUpdated");
        await hub.Clients.All.SendAsync("scannerStatsUpdated", state.ScannerStats);
        state.AddSignalREvent("riskUpdated");
        await hub.Clients.All.SendAsync("riskUpdated", state.Risk);
        state.AddSignalREvent("controlsUpdated");
        await hub.Clients.All.SendAsync("controlsUpdated", state.Controls);
        state.AddSignalREvent("botStatusUpdated");
        await hub.Clients.All.SendAsync("botStatusUpdated", state.Status);
        state.AddSignalREvent("equityUpdated");
        await hub.Clients.All.SendAsync("equityUpdated", TrimPayload(state, "equityUpdated", state.Equity().TakeLast(options.SignalR.MaxPayloadItems).ToArray(), options));
        state.AddSignalREvent("singleMarketArbsUpdated");
        await hub.Clients.All.SendAsync("singleMarketArbsUpdated", BuildSingleMarketSignalRPayload(state, options));
        state.AddSignalREvent("singleMarketPaperExecutionsUpdated");
        await hub.Clients.All.SendAsync("singleMarketPaperExecutionsUpdated", TrimPayload(state, "singleMarketPaperExecutionsUpdated", state.SingleMarketExecutions().TakeLast(options.RuntimeState.MaxSingleMarketExecutions).ToArray(), options));
        state.AddSignalREvent("terminalLogsUpdated");
        await hub.Clients.All.SendAsync("terminalLogsUpdated", TrimPayload(state, "terminalLogsUpdated", state.Logs().TakeLast(options.SignalR.MaxRecentLogsToBroadcast).ToArray(), options));
    }
    catch (Exception ex)
    {
        logger.LogError("signalr", $"{{\"event\":\"push_pipeline_error\",\"message\":\"{ex.Message.Replace("\"", "'")}\"}}");
    }
}


static T[] TrimPayload<T>(BotRuntimeState state, string eventName, T[] items, TradingBotOptions options)
{
    var result = SignalRPayloadGuard.Trim(items, options.SignalR);
    if (result.Trimmed)
        Console.WriteLine($"[SIGNALR_PAYLOAD_TRIMMED] Event={eventName} ItemsBefore={result.ItemsBefore} ItemsAfter={result.ItemsAfter}");
    return result.Items;
}


static void ExportRuntimeSoakStatus(BotRuntimeState state, TradingBotOptions options, string contentRootPath)
    => RuntimeSoakStatusExporter.Export(state, options, contentRootPath);


static SingleMarketArbSnapshotDto BuildSingleMarketSignalRPayload(BotRuntimeState state, TradingBotOptions options)
{
    var snapshot = state.SingleMarketSnapshot;
    var limit = Math.Max(1, Math.Min(options.SignalR.MaxPayloadItems, options.SignalR.MaxDiagnosticsItemsToBroadcast));
    var payload = snapshot with
    {
        PositiveCandidates = snapshot.PositiveCandidates.Take(limit).ToArray(),
        TopNearMisses = snapshot.TopNearMisses.Take(Math.Min(limit, options.RuntimeState.MaxSingleMarketNearMisses)).ToArray(),
        DataQualityRejectSamples = snapshot.DataQualityRejectSamples.Take(Math.Min(limit, options.RuntimeState.MaxSingleMarketDataQualitySamples)).ToArray(),
        PaperExecutions = snapshot.PaperExecutions.Take(Math.Min(limit, options.RuntimeState.MaxSingleMarketExecutions)).ToArray()
    };
    var maxBytes = Math.Max(1024, options.SignalR.MaxPayloadBytes);
    while (System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload).Length > maxBytes && limit > 1)
    {
        limit = Math.Max(1, limit / 2);
        payload = payload with
        {
            PositiveCandidates = payload.PositiveCandidates.Take(limit).ToArray(),
            TopNearMisses = payload.TopNearMisses.Take(limit).ToArray(),
            DataQualityRejectSamples = payload.DataQualityRejectSamples.Take(limit).ToArray(),
            PaperExecutions = payload.PaperExecutions.Take(limit).ToArray()
        };
    }
    return payload;
}

static List<Market> BuildRollingBatch(List<Market> markets, ref int offset, int batchSize, TradingBotOptions options)
{
    if (markets.Count == 0 || batchSize <= 0) return new();
    if (options.Mode == "FastTopMarkets")
    {
        return markets.OrderByDescending(x => x.liquidityNum).ThenByDescending(x => x.volume24hrNum).Take(batchSize).ToList();
    }

    var result = new List<Market>(batchSize);
    for (var i = 0; i < batchSize; i++)
    {
        var index = (offset + i) % markets.Count;
        result.Add(markets[index]);
    }
    offset = (offset + batchSize) % markets.Count;
    return result;
}

static string ResolveEffectiveDiscoveryMode(TradingBotOptions options)
{
    if (options.MarketDiscovery.SourceAuditOnly) return "SourceAuditOnly";
    if (options.MarketDiscovery.AllowReducedUniverseDiagnosticsOnly) return "ReducedUniverseDiagnosticsOnly";
    return "Normal";
}

static string ResolveConfigSource(IConfiguration configuration, params string[] keys)
{
    if (configuration is not IConfigurationRoot root) return "Unknown";
    var matches = new List<string>();
    foreach (var provider in root.Providers)
    {
        foreach (var key in keys)
        {
            if (provider.TryGet(key, out var value))
            {
                var providerName = provider.ToString()?.Replace(' ', '_') ?? provider.GetType().Name;
                matches.Add($"{key}@{providerName}={value}");
            }
        }
    }
    return matches.Count == 0 ? "Default" : string.Join("|", matches);
}

static void SyncRuntimeState(BotRuntimeState state, OpportunityMonitor monitor, PaperPositionBook pb, string executionJournalPath, ExecutionPolicy p, OrderBookService obs, PaperTradingEngine paper, int marketsScanned, DateTime scanStart, string? lastError, SingleMarketScanStats scanStats, OpportunityFilteringOptions filtering, MarketDiscoverySummary discovery, int rollingOffset, int batchSize, int totalDiscovered, DateTime discoveryStartedAt, DateTime discoveryCompletedAt, int emptyCycles, int configuredMarketScanLimit, int effectiveMarketLimit, int configuredMaxMarketsToDiscover, TradingBotOptions options, string poolLimitReason, MultiOutcomeGroupArbEngine.MultiOutcomeScanReport multiOutcomeReport, string contentRootPath)
{
    var top = monitor.GetTopCycleRecords(200, executableOnly: false);
    var skippedPositive = top.Count(x => !x.IsExecutable && x.EdgePerShare > 0 && x.ExpectedProfit > 0);
    var hiddenFromUi = top.Count(x => !OpportunityVisibilityFilter.IsVisibleOpportunity(new OpportunityDto("", DateTime.UtcNow, 0, "", "", "", "", x.EdgePerShare, x.ExpectedProfit, 0, 0, 0, x.IsExecutable, x.IsExecutable ? "EXECUTABLE" : "SKIPPED", null, 0), filtering));
    state.ReplaceOpportunities(top.Select((r, i) =>
    {
        var status = r.IsExecutable ? "EXECUTABLE" : "SKIPPED";
        var reason = r.IsExecutable ? null : "NOT_EXECUTABLE";
        return new OpportunityDto($"{r.Engine}-{r.Key}-{i}", r.TimestampUtc, i + 1, r.Strategy, r.GroupKey ?? "", r.Leg1, "BOTH", r.EdgePerShare, r.ExpectedProfit, r.CostOrProceeds, r.GuaranteedPayout, r.QuantityAvailable, r.IsExecutable, status, reason, state.NextSeq());
    }));

    state.ReplacePositions(pb.OpenPositions.Concat(pb.ClosedPositions).Take(200).Select(pz => PaperPositionDtoFactory.ToDto(pz, state.NextSeq())));

    state.ReplaceTrades(ReadTradeEntries(executionJournalPath, state, filtering));

    var s = obs.GetStats();
    state.SetOrderBookServiceStats(s);

    var edges = top.Select(x => x.EdgePerShare).OrderBy(x => x).ToList();
    var median = edges.Count == 0 ? 0m : edges[edges.Count / 2];
    var nearMissTop = (scanStats.NearMisses ?? new List<NearMissOpportunity>()).OrderByDescending(x => x.NetEdge).Take(options.Diagnostics.NearMissTopN).Select((x,i)=>x with { Rank = i+1 }).ToList();
    var skipReasonList = (scanStats.SkipReasons ?? new Dictionary<string,int>()).OrderByDescending(x=>x.Value).Select(x=>new SkipReasonSummary(x.Key, x.Value, null, null)).ToList();
    var thresholdSimulation = new ThresholdSimulationSnapshot(
        ActualPositive: scanStats.PositiveEdgeFound,
        ZeroBufferPositive: edges.Count(x => x > 0m),
        ZeroFeePositive: edges.Count(x => x > 0m),
        RawPositive: edges.Count(x => x > 0m),
        RelaxedMinEdgePositive: edges.Count(x => x >= 0m),
        RelaxedMinProfitPositive: edges.Count(x => x >= 0m));
    var strategyRecommendation = new StrategyRecommendationSnapshot(
        scanStats.PositiveEdgeFound == 0
            ? "Single-market BUY_YES_AND_BUY_NO is not producing positive edge. Prioritize multi-outcome NO basket or cross-exchange matching."
            : "Continue current strategy set and monitor strategy-level diagnostics.",
        new[]
        {
            "BUY_ALL_NO_MUTUALLY_EXCLUSIVE",
            "CROSS_EXCHANGE_KALSHI_POLYMARKET"
        });
    var singleMarketDescription = new StrategyDescriptor(
        "BUY_YES_AND_BUY_NO",
        "Rare",
        "In binary markets, YES ask + NO ask is usually >= 1 due to spread and buffers.",
        new[] { "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", "CROSS_EXCHANGE_KALSHI_POLYMARKET", "CrossMarket semantic pairs" });
    var multiOutcomeGroupDiagnostics = new MultiOutcomeGroupDiagnosticsSnapshot(multiOutcomeReport.GroupsDetected, 0, 0, multiOutcomeReport.GroupsEvaluated, 0, multiOutcomeReport.BestVerifiedEdge, multiOutcomeReport.ExecutableGroups, multiOutcomeReport.TopSkipReason);
    var rejectedGroupsCount = Math.Max(0, multiOutcomeReport.GroupsDetected - multiOutcomeReport.GroupsVerified);
    if (state.MultiOutcomeDiagnostics is null)
        state.SetMultiOutcomeDiagnostics(new MultiOutcomeDiagnosticsDto(multiOutcomeReport.GroupsDetected,multiOutcomeReport.GroupsVerified,rejectedGroupsCount,multiOutcomeReport.ExecutableGroups,multiOutcomeReport.RejectedByReason.ToDictionary(k=>k.Key,v=>v.Value),multiOutcomeReport.TopRejectedSamples.Take(25).Select(x=>$"{x.GroupKey}:{x.Reason}").ToArray(),Array.Empty<string>(),0,Array.Empty<string>(),Guid.NewGuid().ToString("N"),DateTime.UtcNow,state.NextSeq()));
    var diag = new OpportunityDiagnosticsSnapshot(
        Guid.NewGuid().ToString("N"),
        DateTime.UtcNow,
        marketsScanned,
        (int)Math.Min(int.MaxValue, s.BatchBooksLoaded),
        scanStats.BothAsks,
        scanStats.Candidates,
        scanStats.PositiveEdgeFound,
        scanStats.PositiveEdgeFound,
        scanStats.Executed,
        nearMissTop.Count,
        scanStats.BestEdgeSeen ?? 0m,
        scanStats.BestEdgeSeen ?? 0m,
        edges.Count == 0 ? 0m : edges.Average(),
        edges.Count == 0 ? 0m : edges.Average(),
        nearMissTop.Count == 0 ? 0m : nearMissTop.Min(x => x.GrossCost),
        nearMissTop.Count == 0 ? 0m : nearMissTop.Min(x => x.NetCost),
        options.SingleMarketFees,
        options.SingleMarketSlippage,
        0m,
        skipReasonList,
        new[]
        {
            new StrategyBreakdownItem(
                "BUY_YES_AND_BUY_NO",
                marketsScanned,
                scanStats.Candidates,
                scanStats.PositiveEdgeFound,
                scanStats.PositiveEdgeFound,
                scanStats.Executed,
                scanStats.BestEdgeSeen ?? 0m,
                scanStats.BestEdgeSeen ?? 0m,
                edges.Count == 0 ? 0m : edges.Average(),
                nearMissTop.Count,
                skipReasonList.FirstOrDefault()?.Reason ?? "None")
        },
        (long)(DateTime.UtcNow-scanStart).TotalMilliseconds,
        nearMissTop,
        thresholdSimulation,
        strategyRecommendation,
        singleMarketDescription,
        multiOutcomeGroupDiagnostics);
    state.SetOpportunityDiagnostics(diag);
    state.SetScannerStats(new ScannerStatsDto(
        MarketsScanned: marketsScanned,
        OrderbooksScanned: (int)Math.Min(int.MaxValue, s.BatchBooksLoaded),
        OpportunitiesDetected: top.Count,
        ExecutableOpportunities: top.Count(x => x.IsExecutable),
        SkippedByRisk: Math.Max(0, top.Count - top.Count(x => x.IsExecutable)),
        NegativeEdgeSkipped: scanStats.NegativeEdgeSkipped,
        ZeroEdgeSkipped: scanStats.ZeroEdgeSkipped,
        PositiveEdgeFound: scanStats.PositiveEdgeFound,
        ExecutableFound: scanStats.Executed,
        SkippedPositiveEdgeCount: skippedPositive,
        HiddenFromUiCount: hiddenFromUi,
        ScanDurationMs: (long)(DateTime.UtcNow - scanStart).TotalMilliseconds,
        LastScanStartedAt: scanStart,
        LastScanCompletedAt: DateTime.UtcNow,
        LastError: lastError,
        TotalMarketsDiscovered: totalDiscovered,
        ActiveMarketsDiscovered: discovery.ActiveMarketsAvailable,
        DiscoveryPagesFetched: discovery.PagesFetched,
        CurrentRollingScanOffset: rollingOffset,
        RollingScanBatchSize: batchSize,
        MarketsScannedThisCycle: marketsScanned,
        EstimatedCyclesToFullCoverage: batchSize == 0 ? 0 : (int)Math.Ceiling(totalDiscovered / (double)batchSize),
        LastFullDiscoveryStartedAt: discoveryStartedAt,
        LastFullDiscoveryCompletedAt: discoveryCompletedAt,
        LastFullDiscoveryDurationMs: discoveryCompletedAt > discoveryStartedAt ? (long)(discoveryCompletedAt - discoveryStartedAt).TotalMilliseconds : 0,
        LastScanDurationMs: (long)(DateTime.UtcNow - scanStart).TotalMilliseconds,
        OrderbooksRequested: (int)Math.Min(int.MaxValue, s.BatchBooksLoaded),
        OrderbooksSucceeded: (int)Math.Min(int.MaxValue, s.BatchBooksLoaded),
        OrderbooksFailed: 0,
        PositiveEdgeFoundCycle: scanStats.PositiveEdgeFound,
        ExecutableFoundCycle: scanStats.Executed,
        EmptyCyclesSinceLastOpportunity: emptyCycles,
        ConfiguredMarketScanLimit: configuredMarketScanLimit,
        ConfiguredMaxMarketsToDiscover: configuredMaxMarketsToDiscover,
        EffectiveMarketLimit: effectiveMarketLimit,
        ScanBatchSizeConfigured: batchSize,
        LastEmptyExecutableCycleAt: emptyCycles > 0 ? DateTime.UtcNow : null,
        SkippedBelowMinEdge: scanStats.SkipReasons?.GetValueOrDefault("BelowMinEdgeThreshold", 0) ?? 0,
        SkippedBelowMinProfit: 0,
        SkippedInsufficientLiquidity: scanStats.SkipReasons?.GetValueOrDefault("InsufficientLiquidity", 0) ?? 0,
        SkippedMissingBothSides: (scanStats.SkipReasons?.GetValueOrDefault("MissingYesAsk", 0) ?? 0) + (scanStats.SkipReasons?.GetValueOrDefault("MissingNoAsk", 0) ?? 0),
        SkippedStaleOrderbook: 0,
        SkippedRiskLimit: scanStats.SkipReasons?.GetValueOrDefault("RiskLimitExceeded", 0) ?? 0,
        SkippedOther: scanStats.SkipReasons?.Where(kv => kv.Key is not "BelowMinEdgeThreshold" and not "InsufficientLiquidity" and not "MissingYesAsk" and not "MissingNoAsk" and not "RiskLimitExceeded").Sum(kv => kv.Value) ?? 0,
        DiscoveryHealthy: discovery.DiscoveryHealthy,
        RawMarketsLoaded: discovery.RawLoadedTotal,
        UniqueMarketsLoaded: discovery.UniqueMarketsTotal,
        InactiveSkipped: discovery.InactiveSkipped,
        SkippedClosed: discovery.SkippedClosed,
        SkippedArchived: discovery.SkippedArchived,
        SkippedMissingTokenIds: discovery.SkippedMissingTokenIds,
        SkippedInvalidShape: discovery.SkippedInvalidShape,
        PaginationMode: discovery.PaginationMode,
        LastPaginationCursor: discovery.LastPaginationCursor,
        LastDiscoveryWarning: discovery.LastDiscoveryWarning,
        LastDiscoveryCompletedAt: discovery.LastDiscoveryCompletedAtUtc,
        Diagnostics: diag,
        Sequence: state.NextSeq()));
    state.SetRisk(new RiskStateDto(p.MaxNotionalPerTrade, p.MinNotionalPerTrade, p.MinEdgePerShare, p.MinExpectedProfit, p.MaxLockedCapital, paper.LockedCapital, p.MaxOpenPositions, pb.OpenPositions.Count, p.MaxExposurePerGroup, new Dictionary<string, decimal>(), p.AllowBasketArbs, p.AllowSingleMarketArbs, p.AllowCompleteSetSellArbs, p.AllowThresholdArbs, DateTime.UtcNow, state.NextSeq()));
    state.SetStatus(new BotStatusDto("PAPER", !state.Controls.IsPaused, "CONNECTED", paper.Balance, paper.LockedCapital, paper.Equity, paper.RealizedPnl, paper.ExpectedProfit, pb.OpenPositions.Count, top.Count, DateTime.UtcNow, DateTime.UtcNow));
    state.SetPaperOpenCountLastHour(paper.HourlyOpenCount);
    state.SetPaperInFlightOpens(paper.PaperInFlightOpenCount);
    state.SetPaperDuplicateDedupeEntries(paper.PaperDuplicateDedupeEntryCount);
    state.SetPaperOpenPositionKeys(pb.OpenPositions.Select(x => x.GroupKey));
    state.SetPaperOpenMarketIds(pb.OpenPositions.SelectMany(x => x.Legs.Select(l => l.MarketId)).Distinct(StringComparer.OrdinalIgnoreCase));
    state.ReplacePaperSettlements(pb.Settlements);
    state.SetPaperSettlementCounters(paper.SettlementRejects, paper.DuplicateSettlementSuppressions);
    var exportsRoot = Path.Combine(contentRootPath, "exports");
    PaperAccountExporter.ExportLatest(exportsRoot, paper, pb, state.SingleMarketExecutions(), paper.BlockedCountsByReason);
    PaperOpportunityFunnelExporter.ExportLatest(exportsRoot, PaperOpportunityFunnelExporter.Build(options, state, scanStats, multiOutcomeReport, marketsScanned, !discovery.DiscoveryHealthy && discovery.ActiveMarketsAvailable < options.MarketDiscovery.MinHealthyActiveMarkets));
    state.AddEquity(new EquityPointDto(DateTime.UtcNow, paper.Equity, state.NextSeq()));
}



static object BuildVerifiedScreenerRow(VerifiedBasketScreener.ScreenResult row, VerifiedOpportunityStabilityTracker stability, ExecutionOptions executionOptions, ISet<string>? openGroupKeys = null, VerifiedBasketExecutionCoordinator? verifiedExecution = null)
{
    var json = System.Text.Json.JsonSerializer.SerializeToNode(row)!.AsObject();
    var latest = stability.LatestReadiness(row.GroupKey);
    var state = stability.State(row.GroupKey);
    var strategy = "BUY_ALL_NO_MUTUALLY_EXCLUSIVE";
    var hasOpenPaperPosition = openGroupKeys?.Contains($"{row.GroupKey}|{strategy}") ?? false;
    json["edgeStabilityStatus"] = state is VerifiedBasketState.EdgeStable or VerifiedBasketState.ExecutionReadinessPending or VerifiedBasketState.ExecutionStable or VerifiedBasketState.PaperOpened or VerifiedBasketState.SuppressedDuplicate ? "EdgeStable" : state.ToString();
    json["executionReadinessStatus"] = state == VerifiedBasketState.ExecutionStable ? "ExecutionStable" : state == VerifiedBasketState.ExecutionReadinessPending ? "ExecutionReadinessPending" : "WaitingForExecutionReadiness";
    json["consecutiveEdgeScans"] = stability.Consecutive(row.GroupKey);
    json["requiredEdgeScans"] = executionOptions.RequiredConsecutiveExecutableScans;
    json["stateAgeSeconds"] = stability.StateAge(row.GroupKey).TotalSeconds;
    json["lastResetReason"] = stability.LastResetReason(row.GroupKey);
    json["consecutiveExecutionReadyScans"] = stability.ConsecutiveExecutionReady(row.GroupKey);
    json["requiredConsecutiveExecutionReadyScans"] = executionOptions.RequiredConsecutiveExecutionReadyScans;
    json["latestReadinessSample"] = latest is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(latest);
    json["readinessHistorySummary"] = System.Text.Json.JsonSerializer.SerializeToNode(stability.ReadinessSummaries(executionOptions.RequiredConsecutiveExecutionReadyScans).FirstOrDefault(x => x.GroupKey.Equals(row.GroupKey, StringComparison.OrdinalIgnoreCase)));
    json["notReadyReason"] = latest?.NotReadyReason;
    json["state"] = state.ToString();
    json["hasOpenPaperPosition"] = hasOpenPaperPosition;
    json["suppressionCount"] = verifiedExecution?.GetSuppressionCount(row.GroupKey) ?? 0;
    json["activeExecutable"] = !hasOpenPaperPosition && row.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.ExecutableUnderActiveProfile;
    json["experimentalCandidate"] = row.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.ExperimentalPaperCandidate;
    json["diagnosticsOnly"] = row.ExecutionStatus == VerifiedBasketScreener.ExecutionStatus.DiagnosticsOnlyPositive;
    json["paperOpened"] = hasOpenPaperPosition || state is VerifiedBasketState.PaperOpened or VerifiedBasketState.SuppressedDuplicate;
    json["lastStateTransition"] = DateTime.UtcNow - stability.StateAge(row.GroupKey);
    json["lastSuppressionReason"] = hasOpenPaperPosition ? "DuplicateOpenPosition" : null;
    json["experimentalProfileNetEdge"] = row.ExperimentalProfileNetEdge;
    json["uiStatus"] = state == VerifiedBasketState.ExecutionStable ? "Actionable" : hasOpenPaperPosition ? "AlreadyOpen" : "WaitingForExecutionReadiness";
    return json;
}

static IEnumerable<TradeLogEntryDto> ReadTradeEntries(string executionJournalPath, BotRuntimeState state, OpportunityFilteringOptions filtering)
{
    if (!File.Exists(executionJournalPath)) yield break;
    foreach (var line in File.ReadLines(executionJournalPath).Skip(1).TakeLast(100))
    {
        var c = line.Split(',');
        if (c.Length < 10) continue;
        var entry = new TradeLogEntryDto(Guid.NewGuid().ToString("N"), DateTime.UtcNow, "SCAN", "BASKET", c[4], 0, 0, 0, 0, "SKIPPED", "JOURNAL", state.NextSeq());
        if (!IsVisibleTrade(entry, filtering)) continue;
        yield return entry;
    }
}

static bool IsVisibleTrade(TradeLogEntryDto t, OpportunityFilteringOptions _)
{
    if (string.Equals(t.Status, "SKIPPED", StringComparison.OrdinalIgnoreCase) && t.Edge <= 0) return false;
    return t.Edge > 0 && t.ExpectedProfit > 0;
}

public class MultiTextWriter(TextWriter original, Action<string> mirror) : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;
    public override void WriteLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var enriched = ProcessRunContext.EnrichLogLine(value);
        original.WriteLine(enriched);
        mirror(enriched);
    }
}

public sealed record PersistedHealthyDiscoverySnapshot(string ProcessRunId, DateTime CreatedAtUtc, int ActiveCount, int RawCount, IReadOnlyList<PersistedHealthyMarket> Markets);
public sealed record PersistedHealthyMarket(string Id, string Question, string? ConditionId, IReadOnlyList<string> Outcomes, IReadOnlyList<string> ClobTokenIds, bool? Active, bool? Closed, bool? Archived, bool? AcceptingOrdersSnake, bool? AcceptingOrders, decimal? Liquidity, decimal? Volume24hr);
