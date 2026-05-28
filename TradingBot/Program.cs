using System.Text;
using Microsoft.AspNetCore.SignalR;
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
builder.Services.AddSingleton<IExchangeOrderExecutor, DisabledExchangeOrderExecutor>();
builder.Services.AddSingleton<DryRunLiveExecutor>();
builder.Services.AddSingleton(sp => new BotRuntimeState(sp.GetRequiredService<IOptions<TradingBotOptions>>().Value.RuntimeState));
builder.Services.AddSingleton<TextWriter>(originalOut);
builder.Services.AddSingleton<IBotUiLogger, BotUiLogger>();

var app = builder.Build();
app.UseCors("ui");
var options = builder.Configuration.GetSection(TradingBotOptions.SectionName).Get<TradingBotOptions>() ?? new TradingBotOptions();
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
app.MapGet("/api/bot/verified-allowlist-health", (IHostEnvironment env) =>
{
    var path = Path.Combine(env.ContentRootPath, "exports/verified-allowlist-health-latest.json");
    if (!File.Exists(path)) return Results.Ok(Array.Empty<object>());
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
app.MapGet("/api/bot/risk", (BotRuntimeState s, IRiskManager risk) => Results.Ok(new { runtime = s.Risk, executionRisk = risk.GetRiskSnapshot() }));
app.MapGet("/api/bot/execution-audit", (VerifiedBasketExecutionCoordinator audit, int? limit) => audit.ListAudit(Math.Clamp(limit ?? 200, 1, 1000)));
app.MapGet("/api/bot/dry-run-order-plans", (VerifiedBasketExecutionCoordinator audit, int? limit) =>
{
    var sims = audit.ListFillSimulations(500).GroupBy(x => x.OrderPlanId).ToDictionary(g => g.Key, g => g.Last());
    return Results.Ok(audit.ListDryRunPlans(Math.Clamp(limit ?? 50, 1, 500)).Select(p =>
    {
        sims.TryGetValue(p.Id, out var sim);
        return new { plan = p, p.Id, p.OpportunityId, p.GroupKey, p.Title, p.Strategy, p.ActiveProfile, p.DryRunOnly, p.CreatedAt, p.ExpiresAt, status = p.Status.ToString(), p.LegsCount, p.PlannedQty, p.GuaranteedPayout, p.CostPerBasket, p.TotalEstimatedCost, p.ExpectedProfit, p.NetEdge, p.MaxNotional, p.Orders, p.ValidationWarnings, p.ValidationErrors, latestFillSimulationStatus = sim?.Status.ToString(), fullyFillableQty = sim?.FullyFillableQty, partialFillRisk = sim?.PartialFillRisk, worstLeg = sim?.WorstLeg, estimatedFilledCost = sim?.EstimatedFilledCost };
    }));
});
app.MapGet("/api/bot/dry-run-fill-simulations", (VerifiedBasketExecutionCoordinator audit, int? limit) => Results.Ok(audit.ListFillSimulations(Math.Clamp(limit ?? 50, 1, 500))));
app.MapGet("/api/bot/execution-plans", (BotRuntimeState s, int? limit) => s.Trades().TakeLast(Math.Clamp(limit ?? 100, 1, 500)).ToArray());
app.MapGet("/api/bot/controls", (BotRuntimeState s) => s.Controls);
app.MapPost("/api/bot/controls/pause", async (BotRuntimeState s, IHubContext<BotHub> hub) =>
{
    s.SetControls(new BotControlStateDto(true, "MANUAL_PAUSE", DateTime.UtcNow, s.NextSeq()));
    s.SetStatus(s.Status with { ScannerActive = false, LastScanTime = DateTime.UtcNow });
    await hub.Clients.All.SendAsync("controlsUpdated", s.Controls);
    await hub.Clients.All.SendAsync("botStatusUpdated", s.Status);
    return Results.Ok(s.Controls);
});
app.MapPost("/api/bot/controls/kill-switch/enable", (IRiskManager risk) => { risk.SetKillSwitch(true); return Results.Ok(new { killSwitchEnabled = true }); });
app.MapPost("/api/bot/controls/kill-switch/disable", (IRiskManager risk) => { risk.SetKillSwitch(false); return Results.Ok(new { killSwitchEnabled = false }); });
app.MapPost("/api/bot/controls/resume", async (BotRuntimeState s, IHubContext<BotHub> hub) =>
{
    s.SetControls(new BotControlStateDto(false, "RUNNING", DateTime.UtcNow, s.NextSeq()));
    s.SetStatus(s.Status with { ScannerActive = true, LastScanTime = DateTime.UtcNow });
    await hub.Clients.All.SendAsync("controlsUpdated", s.Controls);
    await hub.Clients.All.SendAsync("botStatusUpdated", s.Status);
    return Results.Ok(s.Controls);
});
app.MapGet("/api/bot/logs/recent", (BotRuntimeState s, int? limit) => s.Logs().TakeLast(Math.Clamp(limit ?? 300, 1, 1000)).ToArray());
app.MapGet("/api/bot/equity", (BotRuntimeState s, int? limit) => s.Equity().TakeLast(Math.Clamp(limit ?? 500, 1, 1000)).ToArray());
app.MapGet("/api/bot/runtime-health", (BotRuntimeState s) => Results.Ok(new {
    processMemoryMb = Math.Round(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64 / 1024d / 1024d, 2),
    gcTotalMemoryMb = Math.Round(GC.GetTotalMemory(false) / 1024d / 1024d, 2),
    gen0Collections = GC.CollectionCount(0),
    gen1Collections = GC.CollectionCount(1),
    gen2Collections = GC.CollectionCount(2),
    recentLogsCount = s.Logs().Length,
    scannerStatsHistoryCount = s.ScannerStatsHistoryCount,
    verifiedGroupsCount = s.VerifiedBasketScreener?.VerifiedGroups?.Count ?? 0,
    candidateGroupsCount = s.MultiOutcomeCandidates.Length,
    rejectedSamplesCount = s.MultiOutcomeReviewReport.Length,
    signalRClientsCount = (int?)null,
    orderbookCacheCount = 0,
    marketCacheCount = 0,
    uptime = DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime(),
    lastScanId = s.ScannerStats.Sequence
}));
app.MapHub<BotHub>("/hubs/bot");

var apiTask = app.RunAsync(listenUrl);
var state = app.Services.GetRequiredService<BotRuntimeState>();
var logger = app.Services.GetRequiredService<IBotUiLogger>();
options = app.Services.GetRequiredService<IOptions<TradingBotOptions>>().Value;

Console.SetOut(new MultiTextWriter(originalOut, msg => logger.LogInfo("console", msg)));
logger.LogSuccess("startup", $"Bot API listening on {listenUrl}");
logger.LogSuccess("startup", $"ExecutionMode={options.ExecutionMode}; EnablePaperTrading={options.EnablePaperTrading}; EnableLiveExecution={options.EnableLiveExecution}");
logger.LogInfo("startup", $"[CONFIG] Scanner Mode={options.Mode} MarketScanLimit={options.MarketScanLimit} MaxMarketsToDiscover={options.MaxMarketsToDiscover} ScanBatchSize={options.ScanBatchSize} MaxOrderbooksPerCycle={options.MaxOrderbooksPerCycle} MaxConcurrentOrderbookRequests={options.MaxConcurrentOrderbookRequests} LogEmptyOpportunityCycles={options.LogEmptyOpportunityCycles}");
logger.LogInfo("startup", $"[DIAGNOSTICS] DebuggerSafeMode={options.Diagnostics.DebuggerSafeMode} DetailedLogs={(!options.Diagnostics.DebuggerSafeMode).ToString().ToLowerInvariant()} MaxRecentLogs={options.RuntimeState.MaxRecentLogs}");
logger.LogInfo("startup", $"[CONFIG] MultiOutcome FeePerLeg={options.MultiOutcomeArbitrage.FeePerLeg} SlippagePerLeg={options.MultiOutcomeArbitrage.SlippageBufferPerLeg} SafetyPerGroup={options.MultiOutcomeArbitrage.SafetyBufferPerGroup} MinNetEdgePerBasket={options.MultiOutcomeArbitrage.MinMultiOutcomeEdge} MinExpectedProfit={options.MultiOutcomeArbitrage.MinExpectedProfit} EnableSensitivityDiagnostics={options.MultiOutcomeArbitrage.EnableSensitivityDiagnostics}");
var executionCfg = app.Services.GetRequiredService<IOptions<ExecutionOptions>>().Value;
logger.LogInfo("startup", $"[CONFIG] Execution PaperOnly={executionCfg.PaperOnly.ToString().ToLowerInvariant()} MaxNotionalPerBasket={executionCfg.MaxNotionalPerBasket} MaxOpenBasketPositions={executionCfg.MaxOpenBasketPositions} MaxExposurePerGroup={executionCfg.MaxExposurePerGroup} DuplicateCooldownMinutes={executionCfg.DuplicateCooldownMinutes}");
logger.LogInfo("startup", $"[CONFIG] ExecutionRisk MaxNotionalPerBasket={executionCfg.MaxNotionalPerBasket} MaxNotionalPerTrade={executionCfg.MaxNotionalPerTrade} MinPlannedNotional={executionCfg.MinPlannedNotional} MinPlannedExpectedProfit={executionCfg.MinPlannedExpectedProfit} MinPlannedBasketQty={executionCfg.MinPlannedBasketQty}");
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
    var hub = app.Services.GetRequiredService<IHubContext<BotHub>>();
    while (!app.Lifetime.ApplicationStopping.IsCancellationRequested)
    {
        state.SetStatus(state.Status with { LastHeartbeat = DateTime.UtcNow, ConnectionStatus = "CONNECTED" });
        await hub.Clients.All.SendAsync("heartbeat", new { timestamp = DateTime.UtcNow, sequence = state.NextSeq() });
        await Task.Delay(options.HeartbeatIntervalMs);
    }
});

await RunScannerAsync(state, logger, app.Services.GetRequiredService<IHubContext<BotHub>>(), app.Services.GetRequiredService<VerifiedBasketExecutionCoordinator>(), app.Services.GetRequiredService<VerifiedBasketDryRunOrderBuilder>(), app.Services.GetRequiredService<DryRunFillSimulator>(), app.Services.GetRequiredService<IOptions<ExecutionOptions>>().Value, options, app.Services.GetRequiredService<IOptions<OpportunityFilteringOptions>>().Value, app.Environment.ContentRootPath, app.Lifetime.ApplicationStopping);
await apiTask;

static async Task RunScannerAsync(BotRuntimeState state, IBotUiLogger uiLogger, IHubContext<BotHub> hub, VerifiedBasketExecutionCoordinator verifiedExecution, VerifiedBasketDryRunOrderBuilder dryRunBuilder, DryRunFillSimulator fillSimulator, ExecutionOptions executionOptions, TradingBotOptions options, OpportunityFilteringOptions filtering, string contentRootPath, CancellationToken stoppingToken)
{
    var scannerInstanceId = Guid.NewGuid().ToString("N");
    var scannerStartedAt = DateTime.UtcNow;
    Console.WriteLine($"[SCANNER] Background scanner started InstanceId={scannerInstanceId}");
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    http.Timeout = TimeSpan.FromSeconds(options.ExternalApiTimeoutSeconds);

    var marketService = new MarketDataService(http);
    var orderbookService = new OrderBookService(http) { DisableSingleBookHttpFallback = true, LogPrefetchDetails = options.LogPrefetchDetails, LogBookCacheMissDetails = options.Logging.LogBookCacheMissDetails, BookCacheMissSampleSize = options.Logging.BookCacheMissSampleSize };
    var crossOptions = new CrossExchangeOptions();
    options.GetType();
    var feeOptions = new ExchangeFeesOptions();
    var kalshiOptions = new KalshiOptions();
    var executionPolicy = new ExecutionPolicy
    {
        MaxNotionalPerTrade = options.MaxNotionalPerTrade,
        MinNotionalPerTrade = options.MinNotionalPerTrade,
        MinEdgePerShare = options.MinEdgePerShare,
        MinExpectedProfit = options.MinExpectedProfit,
        MaxLockedCapital = options.MaxLockedCapital,
        MaxOpenPositions = options.MaxOpenPositions,
        MaxExposurePerGroup = options.MaxExposurePerGroup,
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
    var paper = new PaperTradingEngine(executionPolicy, executionJournal, executionDecisionService, positionBook);
    var monitor = new OpportunityMonitor(Path.Combine(AppContext.BaseDirectory, "data", "arb-opportunities.csv"), options.MinEdgePerShare, -0.02m, TimeSpan.FromMinutes(2), options.MinExpectedProfit, new DryRunLiveOrderBuilder(minEdgePerShare: -0.01m, maxPlanCost: 100000m, minSize: 1m, tickSize: 0.001m, orderType: LiveOrderType.FOK, policy: executionPolicy));
    var semaphore = new SemaphoreSlim(options.MaxConcurrentRequests);
    var singleMarketArb = new SingleMarketOrderBookArbEngine(orderbookService, options.MinEdgePerShare, options.SingleMarketSlippage, options.SingleMarketFees, monitor, sizing);

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
    var lastVerifiedScanFingerprint = string.Empty;
    var lastPortfolioFingerprint = string.Empty;
    var lastMtmFingerprint = string.Empty;
    var mismatchCycle = 0;
    var mismatchFingerprintByGroup = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
    var runtimeHealthLastLoggedAt = DateTime.MinValue;
    var basketStateByGroup = new Dictionary<string, VerifiedBasketState>(StringComparer.OrdinalIgnoreCase);
    var stability = new VerifiedOpportunityStabilityTracker();
    var verifiedBasketLastFingerprint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var verifiedBasketLastExecutable = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
    var verifiedPricingLastFingerprint = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var exportService = new MultiOutcomeCandidateExportService(options.MultiOutcomeReview, contentRootPath);
    var multiOutcomeValidator = new MutuallyExclusiveGroupValidator(options.MultiOutcomeArbitrage, contentRootPath);
    var verifiedResolver = new VerifiedMultiOutcomeGroupResolver();
    var preTradeResults = new List<VerifiedBasketPreTradeValidationResult>();
    var promotedVerifiedOpportunities = new List<VerifiedMultiOutcomeOpportunity>();
    Console.WriteLine($"[ALLOWLIST] Loaded verified multi-outcome groups: {multiOutcomeValidator.LoadedAllowlistCount}");
    while (!stoppingToken.IsCancellationRequested)
    {
        var started = DateTime.UtcNow;
        string? lastError = null;
        try
        {
            uiLogger.LogInfo("scanner", "{\"event\":\"scan_start\",\"timestamp\":\"" + started.ToString("O") + "\"}");
            if (state.Controls.IsPaused)
            {
                state.SetStatus(state.Status with { ScannerActive = false, LastScanTime = DateTime.UtcNow });
                if (options.RuntimeHealth.Enabled && (runtimeHealthLastLoggedAt == DateTime.MinValue || DateTime.UtcNow - runtimeHealthLastLoggedAt >= TimeSpan.FromMinutes(Math.Max(1, options.RuntimeHealth.LogEveryMinutes))))
            {
                runtimeHealthLastLoggedAt = DateTime.UtcNow;
                Console.WriteLine($"[RUNTIME_HEALTH] MemoryMb={Math.Round(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64/1024d/1024d,2)} Logs={state.Logs().Length} ScannerHistory={state.ScannerStatsHistoryCount} OrderbookCache=0 Uptime={(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime())}");
            }
            await PushUiUpdates(state, hub, uiLogger, options, verifiedExecution, contentRootPath);
                uiLogger.LogInfo("scanner", "{\"event\":\"scan_skipped\",\"reason\":\"PAUSED\"}");
                await Task.Delay(options.ScanIntervalMs, stoppingToken);
                continue;
            }
            monitor.BeginCycle();
            if (lastDiscoveryAt == default || DateTime.UtcNow - lastDiscoveryAt >= TimeSpan.FromMinutes(options.FullDiscoveryIntervalMinutes) || discoveredMarkets.Count == 0)
            {
                discoveryStartedAt = DateTime.UtcNow;
                var discovery = await marketService.GetMarketsAsync(options, stoppingToken);
                discoveredMarkets = discovery.Markets.Where(m => m?.outcomes?.Count == 2 && m.clobTokenIds?.Count >= 2).ToList();
                lastDiscoverySummary = discovery.Summary;
                lastDiscoveryAt = DateTime.UtcNow;
                discoveryCompletedAt = lastDiscoveryAt;
                cyclesCompletedSinceDiscovery = 0;
                if (options.LogPrefetchSummary)
                    Console.WriteLine($"[DISCOVERY] marketsDiscovered={lastDiscoverySummary.MarketsDiscovered}, pagesFetched={lastDiscoverySummary.PagesFetched}, duplicatesRemoved={lastDiscoverySummary.DuplicatesRemoved}, inactiveSkipped={lastDiscoverySummary.InactiveSkipped}, activeMarketsAvailable={lastDiscoverySummary.ActiveMarketsAvailable}, rawLoadedTotal={lastDiscoverySummary.RawLoadedTotal}, uniqueMarketsTotal={lastDiscoverySummary.UniqueMarketsTotal}, skippedClosed={lastDiscoverySummary.SkippedClosed}, skippedArchived={lastDiscoverySummary.SkippedArchived}, skippedMissingTokenIds={lastDiscoverySummary.SkippedMissingTokenIds}, skippedInvalidShape={lastDiscoverySummary.SkippedInvalidShape}");
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
            var batchStartIndex = filtered.Count == 0 ? 0 : currentRollingOffsetBefore;
            var batchEndIndex = filtered.Count == 0 ? 0 : ((currentRollingOffsetBefore + filtered.Count - 1) % Math.Max(1, scanPool.Count));
            if (scanPool.Count > 0 && currentRollingOffsetAfter < currentRollingOffsetBefore) fullCoverageCompletedCount++;
            cyclesCompletedSinceDiscovery++;
            totalMarketsScannedSinceStart += filtered.Count;
            scanId++;
            var orderbookSemaphore = new SemaphoreSlim(options.MaxConcurrentOrderbookRequests);
            await orderbookService.PrefetchBinarySnapshotsAsync(filtered);
            var scanStats = await singleMarketArb.ScanAsync(filtered!, paper, orderbookSemaphore);

            MultiOutcomeGroupArbEngine.MultiOutcomeScanReport multiOutcomeReport = new(0,0,0,0,0,0,0,0m,0m,0m,"","NotEvaluated",new Dictionary<string,int>(),Array.Empty<MultiOutcomeGroupArbEngine.RejectedSample>(),Array.Empty<MultiOutcomeGroupArbEngine.CandidateGroupReview>());
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
                    validator: multiOutcomeValidator);
                multiOutcomeReport = await multiEngine.ScanAsync(filtered!, paper, orderbookSemaphore, stoppingToken);
                var boundedCandidates = exportService.BuildBoundedCandidates(multiOutcomeReport.CandidateGroupsForReview, options.MultiOutcomeReview.TopCandidateGroupsForReview, options.MultiOutcomeReview.MaxMarketsPerCandidateGroup, includeMarkets: true);
                var reviewReport = exportService.BuildReviewReport(multiOutcomeReport.CandidateGroupsForReview, options.MultiOutcomeReview.AllowUnpricedLegsInTemplate);
                state.SetMultiOutcomeCandidates(boundedCandidates);
                state.SetMultiOutcomeReviewReport(reviewReport);
                exportService.ExportIfDue(multiOutcomeReport.CandidateGroupsForReview);

                if (options.MultiOutcomeArbitrage.EvaluateVerifiedGroupsAgainstFullPool)
                {
                    var allByMarketId = discoveredMarkets.Where(m => !string.IsNullOrWhiteSpace(m.id)).ToDictionary(m => m.id, StringComparer.OrdinalIgnoreCase);
                    var allowlistedGroups = multiOutcomeValidator.GetAllowlistedGroups();
                    var resolved = verifiedResolver.ResolveVerifiedGroups(allowlistedGroups, allByMarketId, options.MultiOutcomeArbitrage, lastDiscoverySummary.DiscoveryHealthy);
                    var verifiedMismatch = resolved.Count(x => x.ValidationStatus != "VerifiedGroupResolved");
                    var verifiedResolved = resolved.Count - verifiedMismatch;
                    var verifiedEvaluated = 0;
                    var verifiedExecutable = 0;
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
                            var s = await orderbookService.GetBinarySnapshotAsync(m, stoppingToken);
                            resolvedNoAsks.Add(VerifiedGroupPricingService.ResolveNoAsk(m, s, DateTime.UtcNow, options.MultiOutcomeArbitrage.VerifiedGroupOrderbookMaxAgeMs));
                        }
                        var missingNoAskLegs = resolvedNoAsks.Where(x => !x.NoAsk.HasValue).ToList();
                        var noAskResolvedCount = resolvedNoAsks.Count - missingNoAskLegs.Count;
                        if (missingNoAskLegs.Count > 0)
                        {
                            skipReason = "MissingNoAsk";
                            verifiedPricingCycle++;
                            var pricingFingerprint = $"{g.GroupKey}|{resolvedNoAsks.Count}|{noAskResolvedCount}|{missingNoAskLegs.Count}|{string.Join(",", missingNoAskLegs.Select(x => x.MarketId).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}";
                            var shouldLogPricing = !verifiedPricingLastFingerprint.TryGetValue(g.GroupKey, out var prevPricing)
                                || prevPricing != pricingFingerprint
                                || (options.Logging.LogVerifiedGroupPricingEveryNCycles > 0 && verifiedPricingCycle % options.Logging.LogVerifiedGroupPricingEveryNCycles == 0);
                            verifiedPricingLastFingerprint[g.GroupKey] = pricingFingerprint;
                            if (shouldLogPricing)
                            {
                                Console.WriteLine($"[VERIFIED_GROUP_PRICING] Group={g.GroupKey} Legs={resolvedNoAsks.Count} NoAskResolved={noAskResolvedCount} MissingNoAsk={missingNoAskLegs.Count} MissingLiquidity=0 TopReason=MissingNoAsk");
                                Console.WriteLine($"[VERIFIED_GROUP_MISSING_NO_ASK] Group={g.GroupKey} Missing={missingNoAskLegs.Count} Samples=[{string.Join(", ", missingNoAskLegs.Take(5).Select(x => $"{x.MarketId}:{x.FailureReason}"))}]");
                            }
                            var pricedLegs = resolvedNoAsks.Where(x => x.NoAsk.HasValue).ToList();
                            var missingIds = missingNoAskLegs.Select(x => x.MarketId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                            var suggestedMarketIds = markets.Where(x => !missingIds.Contains(x.id) && x.active != false).Select(x => x.id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                            var suggestedConditionIds = markets.Where(x => !missingIds.Contains(x.id) && x.active != false && !string.IsNullOrWhiteSpace(x.conditionId)).Select(x => x.conditionId!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                            var suggestion = options.MultiOutcomeReview.IncludeSuggestedPrunedAllowlist ? new { enabled = true, groupKey = g.GroupKey, title = g.Title, verificationStatus = "Verified", groupType = "MutuallyExclusiveWinner", allowedStrategy = "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", marketIds = suggestedMarketIds, conditionIds = suggestedConditionIds, requiredOutcomeCount = suggestedMarketIds.Length, requireExactOutcomeCount = false, suggestionReason = "MissingNoAsk legs excluded" } : null;
                            verifiedPricingExport.Add(new
                            {
                                groupKey = g.GroupKey,
                                totalLegs = resolvedNoAsks.Count,
                                noAskResolvedCount = pricedLegs.Count,
                                missingNoAskCount = missingNoAskLegs.Count,
                                pricedLegs = pricedLegs.Select(x => new { marketId = x.MarketId, conditionId = x.ConditionId, noAsk = x.NoAsk, noAskQuantity = x.NoAskQuantity, noAskSource = x.Source, yesBid = x.YesBid, yesBidQuantity = x.YesBidQuantity, noTokenId = x.NoTokenId, priceResolutionFailureReason = x.FailureReason }).ToArray(),
                                missingPriceLegs = missingNoAskLegs.Select(x => new { marketId = x.MarketId, conditionId = x.ConditionId, noAsk = x.NoAsk, noAskQuantity = x.NoAskQuantity, noAskSource = x.Source, yesBid = x.YesBid, yesBidQuantity = x.YesBidQuantity, noTokenId = x.NoTokenId, priceResolutionFailureReason = x.FailureReason }).ToArray(),
                                suggestedPrunedAllowlistTemplate = suggestion
                            });
                            if (shouldLogPricing) Console.WriteLine($"[VERIFIED_GROUP_PRICING_SUGGESTION] Group={g.GroupKey} MissingNoAsk={missingNoAskLegs.Count} SuggestedPrunedLegs={suggestedMarketIds.Length}");
                            groupDiagnostics.Add(new VerifiedGroupDiagnosticDto(g.GroupKey, g.MarketIds.Count, g.ResolvedMarkets.Count, g.MissingMarketIds.Count, "VerifiedResolved", "MissingNoAsk", null, missingNoAskLegs.Count, 0, missingNoAskLegs.Select(x => x.MarketId).Take(5).ToArray(), Array.Empty<string>()));
                            continue;
                        }
                        var activeCostProfileName = options.MultiOutcomeArbitrage.CostProfiles.ActiveProfile;
                        if (!options.MultiOutcomeArbitrage.CostProfiles.Profiles.TryGetValue(activeCostProfileName, out var activeCostProfile)) activeCostProfile = options.MultiOutcomeArbitrage.CostProfiles.Profiles["Conservative"];
                        var formula = VerifiedBasketFormulaService.Evaluate(resolvedNoAsks, activeCostProfile.FeePerLeg, activeCostProfile.SlippageBufferPerLeg, activeCostProfile.SafetyBufferPerGroup, options.MultiOutcomeArbitrage.RequireAllNoPrices);
                        var breakdown = VerifiedBasketDiagnostics.Compute(g.GroupKey, resolvedNoAsks.Count, formula, activeCostProfile.FeePerLeg, activeCostProfile.SlippageBufferPerLeg, options.MultiOutcomeArbitrage.NearExecutableCostReductionThreshold, options.MultiOutcomeArbitrage.FarFromExecutableCostReductionThreshold);
                        var screen = VerifiedBasketScreener.Evaluate(g.GroupKey, resolvedNoAsks, options.MultiOutcomeArbitrage);
                        verifiedScreenResults.Add(screen);
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
                        var isExec = edge > options.MultiOutcomeArbitrage.MinMultiOutcomeEdge;
                        if (isExec)
                        {
                            verifiedExecutable++;
                            var maxLiquidityQty = Math.Max(options.MultiOutcomeArbitrage.MinExecutableQty, resolvedNoAsks.Min(x => x.NoAskQuantity ?? 0m));
                            var maxLiquidityExpectedProfit = maxLiquidityQty * formula.NetEdge;
                            var questionByMarket = markets.Where(m => !string.IsNullOrWhiteSpace(m.id)).ToDictionary(m => m.id, m => m.question ?? m.id, StringComparer.OrdinalIgnoreCase);
                            var legs = resolvedNoAsks.Select(x => new VerifiedMultiOutcomeOpportunityLeg(x.MarketId, x.ConditionId ?? x.MarketId, questionByMarket.TryGetValue(x.MarketId, out var q) ? q : x.MarketId, "NO", x.NoTokenId ?? x.MarketId, x.NoAsk ?? 0m, x.NoAskQuantity ?? 0m, x.Source, maxLiquidityQty, maxLiquidityQty * (x.NoAsk ?? 0m))).ToArray();
                            var opp = new VerifiedMultiOutcomeOpportunity($"verified-{g.GroupKey}-{DateTime.UtcNow:yyyyMMddHHmmss}", "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", g.GroupKey, g.Title, "Verified", legs.Length, formula.GuaranteedPayout, formula.NoAskSum, formula.GrossEdge, formula.NetEdge, activeCostProfileName, maxLiquidityQty, maxLiquidityExpectedProfit, options.MultiOutcomeArbitrage.MaxNotionalPerGroup, maxLiquidityQty * formula.NoAskSum, "PaperExecutable", legs);
                            promotedVerifiedOpportunities.Add(opp);

                            var hasOpenPosition = positionBook.GetOpenPositions().Any(x => x.GroupKey.Equals(opp.GroupKey, StringComparison.OrdinalIgnoreCase) && x.Strategy == opp.Strategy);
                            if (hasOpenPosition)
                            {
                                var suppressedCount = verifiedExecution.MarkDuplicateSuppressed(opp.GroupKey);
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "DuplicateSuppressed", "Suppressed", "DuplicateOpenPosition", opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, $"Count={suppressedCount}"));
                                state.AddOpportunity(new OpportunityDto($"{opp.Id}-dup-{suppressedCount}", DateTime.UtcNow, 1, opp.Strategy, opp.GroupKey, opp.Title, "NO", opp.NetEdge, 0m, 0m, opp.GuaranteedPayout, 0m, false, "ALREADY_OPEN", "AlreadyOpen", state.NextSeq()));
                                if (options.Logging.LogExecutionSuppressionSummary && (suppressedCount == 1 || options.Logging.LogDuplicatePositionEveryNCycles <= 1 || suppressedCount % options.Logging.LogDuplicatePositionEveryNCycles == 0))
                                    Console.WriteLine($"[VERIFIED_EXECUTION_SUPPRESSED] Group={opp.GroupKey} Reason=DuplicateOpenPosition Count={suppressedCount} LastNetEdge={opp.NetEdge}");
                                continue;
                            }

                            verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "Detected", "Ok", "VerifiedExecutable", opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, ""));
                            verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "PromotedToOpportunity", "Ok", "Actionable", opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, ""));
                            var st = stability.State(opp.GroupKey);
                            if (st == VerifiedBasketState.EdgeExecutablePending)
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "EdgeStabilityPending", "Pending", "WaitingConsecutiveExecutableScans", opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, ""));
                            if (st == VerifiedBasketState.EdgeStable)
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "EdgeStable", "Ok", "EdgeStable", opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, ""));
                            Console.WriteLine($"[VERIFIED_ARB_DETECTED] Group={opp.GroupKey} NetEdge={opp.NetEdge} MaxLiquidityQty={maxLiquidityQty} MaxLiquidityExpectedProfit={maxLiquidityExpectedProfit} Status=RequiresSizing");
                            if (options.EnablePaperTrading && options.MultiOutcomeArbitrage.Enabled && options.ExecutionMode == "PAPER" && st is not (VerifiedBasketState.EdgeStable or VerifiedBasketState.ExecutionReadinessPending or VerifiedBasketState.ExecutionStable))
                            {
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "PreTradeBlocked", "Blocked", "WaitingForStableExecutableSignal", opp.NetEdge, opp.ExpectedProfit, opp.EstimatedCost, opp.ExecutableQty, ""));
                                Console.WriteLine($"[VERIFIED_PRETRADE_BLOCKED] Group={opp.GroupKey} Reason=WaitingForStableExecutableSignal State={st}");
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
                                Console.WriteLine($"[EXECUTION_READINESS_REJECTED] Group={opp.GroupKey} Reason={readiness.NotReadyReason} PlannedQty={readiness.PlannedQty:0.####} MinQty={executionOptions.MinPlannedBasketQty} MaxQtyByLiquidity={readiness.MaxQtyByLiquidity:0.####}");
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "PreTradeBlocked", "Blocked", "WaitingForExecutionReadiness", opp.NetEdge, readiness.PlannedExpectedProfit, readiness.PlannedCost, readiness.PlannedQty, ""));
                                Console.WriteLine($"[VERIFIED_PRETRADE_BLOCKED] Group={opp.GroupKey} Reason=WaitingForExecutionReadiness State={st}");
                                state.AddOpportunity(new OpportunityDto(opp.Id, DateTime.UtcNow, 1, opp.Strategy, opp.GroupKey, opp.Title, "NO", opp.NetEdge, readiness.PlannedExpectedProfit, readiness.PlannedCost, opp.GuaranteedPayout, readiness.PlannedQty, false, "WAITING_FOR_EXECUTION_READINESS", readiness.NotReadyReason, state.NextSeq()));
                                continue;
                            }
                            if (readinessState != VerifiedBasketState.ExecutionStable)
                            {
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "ExecutionReadinessPending", "Pending", "WaitingConsecutiveExecutionReadyScans", opp.NetEdge, readiness.PlannedExpectedProfit, readiness.PlannedCost, readiness.PlannedQty, $"ConsecutiveReady={readiness.ConsecutiveReadyScans};Required={readiness.RequiredConsecutiveReadyScans}"));
                                Console.WriteLine($"[EXECUTION_READINESS_PENDING] Group={opp.GroupKey} ConsecutiveReady={readiness.ConsecutiveReadyScans} Required={readiness.RequiredConsecutiveReadyScans} PlannedQty={readiness.PlannedQty:0.####} PlannedCost={readiness.PlannedCost:0.####} ExpectedProfit={readiness.PlannedExpectedProfit:0.####}");
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "PreTradeBlocked", "Blocked", "WaitingForExecutionReadiness", opp.NetEdge, readiness.PlannedExpectedProfit, readiness.PlannedCost, readiness.PlannedQty, ""));
                                Console.WriteLine($"[VERIFIED_PRETRADE_BLOCKED] Group={opp.GroupKey} Reason=WaitingForExecutionReadiness State={st}");
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
                                    Console.WriteLine($"[DRY_RUN_ORDER_PLAN_REJECTED] Group={opp.GroupKey} Reason={reason}");
                                    continue;
                                }
                                verifiedExecution.RecordDryRunPlan(plan);
                                verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "DryRunOrderPlanCreated", "Ok", "DryRunOnly", pre.NetEdge, pre.ExpectedProfit, pre.EstimatedCost, pre.Quantity, $"Orders={plan.Orders.Count}"));
                                Console.WriteLine($"[DRY_RUN_ORDER_PLAN_CREATED] Group={opp.GroupKey} Orders={plan.Orders.Count} Qty={pre.Quantity:0.####} TotalCost={plan.TotalEstimatedCost:0.####} ExpectedProfit={plan.ExpectedProfit:0.####} DryRunOnly=true");
                                var marketById = g.ResolvedMarkets.ToDictionary(x => x.id, StringComparer.OrdinalIgnoreCase);
                                var snapshotsByMarket = new Dictionary<string, BinaryOrderBookSnapshot?>(StringComparer.OrdinalIgnoreCase);
                                foreach (var order in plan.Orders)
                                {
                                    if (marketById.TryGetValue(order.MarketId, out var market)) snapshotsByMarket[order.MarketId] = await orderbookService.GetBinarySnapshotAsync(market, stoppingToken);
                                    else snapshotsByMarket[order.MarketId] = null;
                                }
                                var booksByToken = plan.Orders.ToDictionary(o => o.TokenId, o => orderbookService.GetCachedOrderBookSnapshot(o.TokenId, o.MarketId), StringComparer.OrdinalIgnoreCase);
                                var fill = fillSimulator.Simulate(plan, booksByToken, snapshotsByMarket, executionOptions);
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
                                    var simulatedNetEdge = fill.SafeExecutableQty > 0m ? (opp.GuaranteedPayout - (fill.EstimatedFilledCost / fill.SafeExecutableQty)) : 0m;
                                    verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "DryRunFillSimulationPassed", "Ok", "FullyFillable", simulatedNetEdge, fill.EstimatedExpectedProfit, fill.EstimatedFilledCost, fill.SafeExecutableQty, $"Orders={fill.RequestedOrdersCount}"));
                                    Console.WriteLine($"[DRY_RUN_FILL_SIMULATION_PASSED] Group={opp.GroupKey} Orders={plan.Orders.Count} PlannedQty={pre.Quantity:0.####} FullyFillableQty={fill.FullyFillableQty:0.####} EstimatedCost={fill.EstimatedFilledCost:0.####}");
                                }
                                var opened = verifiedExecution.OpenPaperPosition(opp, pre, positionBook, fill);
                                if (opened is null) Console.WriteLine($"[PAPER BASKET SKIPPED] Group={opp.GroupKey} Reason={(fill.Status == FillSimulationStatus.FullyFillable ? "DuplicateOpenPosition" : "FillSimulationFailed")}");
                                else { paper.RegisterExternalBasketOpen(opened, opened.TotalCost, opened.ExpectedProfit); Console.WriteLine($"[PAPER BASKET OPENED] Group={opp.GroupKey} Legs={opp.LegsCount} Qty={opened.Quantity} Cost={opened.TotalCost} NetEdge={opened.NetEdgeAtOpen} ExpectedProfit={opened.ExpectedProfit}"); Console.WriteLine($"[PAPER ACCOUNT] Cash={paper.Balance:0.####} Locked={paper.LockedCapital:0.####} OpenExposure={paper.LockedCapital:0.####} UnrealizedPnl={paper.UnrealizedPnl:0.####} RealizedPnl={paper.RealizedPnl:0.####} Equity={paper.Equity:0.####} OpenPositions={positionBook.OpenPositions.Count}"); var mtmFingerprint=$"{opp.GroupKey}|Incomplete|{opp.LegsCount}"; mtmCycle++; var logMtm = !options.Logging.LogPaperMtmOnChangeOnly || mtmFingerprint != lastMtmFingerprint || (options.Logging.LogPaperMtmEveryNCycles > 0 && mtmCycle % options.Logging.LogPaperMtmEveryNCycles == 0); lastMtmFingerprint = mtmFingerprint; if (logMtm) Console.WriteLine($"[PAPER_BASKET_MTM] Group={opp.GroupKey} MtMStatus=Incomplete MissingExitPrices={opp.LegsCount}"); verifiedExecution.Audit(new ExecutionAuditEvent(DateTime.UtcNow, opp.Id, opp.GroupKey, opp.Strategy, "MtMUpdated", "Ok", "Incomplete", opened.NetEdgeAtOpen, 0m, opened.TotalCost, opened.Quantity, $"MissingExitPrices={opp.LegsCount}")); }
                            }
                        }
                        skipReason = isExec ? "Executable" : "NegativeEdge";
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
                    var unresolvedConfiguredGroups = resolved.Where(x => x.ValidationStatus != "VerifiedGroupResolved").Take(5).Select(x => (object)new { x.GroupKey, Reason = x.RejectionReason, x.ValidationStatus }).ToArray();
                    var resolvedByGroup = resolved.ToDictionary(x => x.GroupKey, StringComparer.OrdinalIgnoreCase);
                    var allowlistHealth = allowlistedGroups.Select(g =>
                    {
                        if (!resolvedByGroup.TryGetValue(g.GroupKey, out var rg))
                        {
                            return new { groupKey = g.GroupKey, enabled = g.Enabled, configuredMarketCount = g.MarketIds.Count, resolvedMarketCount = 0, missingMarketCount = g.MarketIds.Count, missingMarketIds = g.MarketIds, foundMarketIds = Array.Empty<string>(), status = "Broken", reason = "ResolverMissingConfiguredGroup", recommendedAction = "RefreshFromCandidateExport" };
                        }
                        return new { groupKey = rg.GroupKey, enabled = g.Enabled, configuredMarketCount = rg.MarketIds.Count, resolvedMarketCount = rg.ResolvedMarkets.Count, missingMarketCount = rg.MissingMarketIds.Count + rg.MissingConditionIds.Count, missingMarketIds = rg.MissingMarketIds, foundMarketIds = rg.ResolvedMarkets.Select(x => x.id).ToArray(), status = rg.ValidationStatus == "VerifiedGroupResolved" ? "Healthy" : "Broken", reason = rg.RejectionReason ?? (rg.ValidationStatus == "VerifiedGroupResolved" ? "Healthy" : "Unresolved"), recommendedAction = rg.ValidationStatus == "VerifiedGroupResolved" ? "PruneIfSafe" : (rg.ResolvedMarkets.Count < 2 ? "DisableMissingMarkets" : "RefreshFromCandidateExport") };
                    }).ToArray();
                    var healthyCount = allowlistHealth.Count(x=>x.status=="Healthy");
                    var brokenCount = allowlistHealth.Count(x=>x.status=="Broken");
                    var unresolvedCount = allowlistHealth.Count(x=>x.status!="Healthy");
                    var disabledCount = 0; var ignoredCount = 0;
                    var needsRefreshCount = allowlistHealth.Count(x=>x.status!="Healthy");
                    var invariantTotal = healthyCount + brokenCount + disabledCount + ignoredCount;
                    allowlistHealthCycle++;
                    var allowlistFingerprint = $"{multiOutcomeValidator.LoadedAllowlistCount}|{healthyCount}|{brokenCount}|{disabledCount}|{ignoredCount}|{needsRefreshCount}";
                    var shouldLogAllowlist = !options.Logging.LogAllowlistHealthOnChangeOnly || allowlistFingerprint != lastAllowlistHealthFingerprint || (options.Logging.LogAllowlistHealthEveryNCycles > 0 && allowlistHealthCycle % options.Logging.LogAllowlistHealthEveryNCycles == 0);
                    lastAllowlistHealthFingerprint = allowlistFingerprint;
                    if (shouldLogAllowlist) Console.WriteLine($"[ALLOWLIST_HEALTH] Configured={multiOutcomeValidator.LoadedAllowlistCount} Healthy={healthyCount} Broken={brokenCount} Disabled={disabledCount} NeedsRefresh={needsRefreshCount} Ignored={ignoredCount}");
                    if (invariantTotal != multiOutcomeValidator.LoadedAllowlistCount) Console.WriteLine($"[ALLOWLIST_HEALTH_ERROR] Configured={multiOutcomeValidator.LoadedAllowlistCount} Healthy={healthyCount} Broken={brokenCount} Disabled={disabledCount} Ignored={ignoredCount}");
                    var healthPath = Path.Combine(contentRootPath, "exports/verified-allowlist-health-latest.json"); Directory.CreateDirectory(Path.GetDirectoryName(healthPath)!); File.WriteAllText(healthPath, System.Text.Json.JsonSerializer.Serialize(new { configured = multiOutcomeValidator.LoadedAllowlistCount, healthy = healthyCount, broken = brokenCount, disabled = disabledCount, ignored = ignoredCount, unresolved = unresolvedCount, needsRefresh = needsRefreshCount, groups = allowlistHealth }, new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
                    var cleanup = new { metadata = new { generatedAtUtc = DateTime.UtcNow, note = "suggested only" }, groups = allowlistHealth.Select(h => new { h.groupKey, h.enabled, h.configuredMarketCount, h.resolvedMarketCount, h.missingMarketCount, h.missingMarketIds, suggestedEnabled = h.missingMarketCount > 0 ? false : h.enabled, reason = h.missingMarketCount > 0 ? "Missing markets in discovered pool" : "Healthy", suggestedPrunedTemplate = h.resolvedMarketCount >= 2 ? new { groupKey = h.groupKey, marketIds = h.foundMarketIds, requiredOutcomeCount = h.resolvedMarketCount } : null })};
                    var cleanupPath = Path.Combine(contentRootPath, "exports/verified-allowlist-cleanup-suggested.json"); File.WriteAllText(cleanupPath, System.Text.Json.JsonSerializer.Serialize(cleanup, new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
                    var snapshot = VerifiedBasketScreener.BuildSnapshot(options.MultiOutcomeArbitrage.CostProfiles.ActiveProfile, "PolymarketApprox", verifiedScreenResults, unresolvedConfiguredGroups);
                    VerifiedBasketScreener.Export(screenerPath, snapshot);
                    foreach (var row in snapshot.ExperimentalCandidates)
                    {
                        Console.WriteLine($"[EXPERIMENTAL_PROFILE_CANDIDATE] Group={row.GroupKey} ActiveProfile={snapshot.ActiveProfile} ActiveNet={row.ActiveProfileNetEdge} ExperimentalProfile={snapshot.ExperimentalProfile} ExperimentalNet={row.ExperimentalProfileNetEdge} Status=PendingStability");
                    }
                    var experimentalExportPath = Path.Combine(contentRootPath, "exports/experimental-profile-paper-candidates-latest.json");
                    File.WriteAllText(experimentalExportPath, System.Text.Json.JsonSerializer.Serialize(new { timestamp = DateTime.UtcNow, activeProfile = snapshot.ActiveProfile, experimentalProfile = snapshot.ExperimentalProfile, candidates = snapshot.ExperimentalCandidates, stabilityState = stability.Summaries(), paperActions = Array.Empty<object>(), blockedReasons = Array.Empty<object>() }, new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
                    foreach (var row in snapshot.VerifiedBaskets)
                    {
                        var prevState = basketStateByGroup.TryGetValue(row.GroupKey, out var pstate) ? pstate : VerifiedBasketState.NotExecutable;
                        var cur = stability.Track(row.GroupKey, row, options.RuntimeState.MaxVerifiedBasketEdgeHistoryPerGroup, 3, 0.001m, 0.002m);
                        basketStateByGroup[row.GroupKey] = cur;
                        if (prevState != cur) Console.WriteLine($"[VERIFIED_BASKET_STATE_CHANGE] Group={row.GroupKey} From={prevState} To={cur} Reason=Transition");
                    }
                    stability.Export(Path.Combine(contentRootPath, "exports/verified-basket-edge-history-latest.json"));
                    stability.ExportExecutionReadiness(Path.Combine(contentRootPath, "exports/execution-readiness-latest.json"), executionOptions.RequiredConsecutiveExecutionReadyScans);
                    var verifiedRowsWithReadiness = snapshot.VerifiedBaskets.Select(row => BuildVerifiedScreenerRow(row, stability, executionOptions)).Take(100).ToArray();
                    var rankingRowsWithReadiness = snapshot.VerifiedBaskets.Select(row => BuildVerifiedScreenerRow(row, stability, executionOptions)).Take(100).ToArray();
                    state.SetVerifiedBasketScreener(new VerifiedBasketScreenerDto(snapshot.ActiveProfile, snapshot.ExperimentalProfile, snapshot.Timestamp, verifiedRowsWithReadiness, rankingRowsWithReadiness, snapshot.NearExecutableBaskets.Cast<object>().Take(25).ToArray(), snapshot.ExperimentalCandidates.Cast<object>().Take(100).ToArray(), snapshot.StableExperimentalCandidates.Cast<object>().Take(100).ToArray(), snapshot.ActiveProfileExecutable.Cast<object>().Take(100).ToArray(), snapshot.DiagnosticsOnlyPositive.Cast<object>().Take(100).ToArray(), snapshot.Profiles, snapshot.BestByActiveProfile, snapshot.BestByRawEdge, snapshot.BestByConservative, snapshot.BestByPolymarketApprox, snapshot.BestByRaw, snapshot.BestNearExecutable, snapshot.UnresolvedConfiguredGroups, stability.ReadinessSummaries(executionOptions.RequiredConsecutiveExecutionReadyScans).Cast<object>().ToArray()));
                    profileComparisonCycle++;
                    var shouldLogProfileComparison = options.Logging.LogProfileComparisonEveryNCycles > 0 && profileComparisonCycle % options.Logging.LogProfileComparisonEveryNCycles == 0;
                    if (options.Logging.LogProfileComparisonSummary && shouldLogProfileComparison)
                    {
                        foreach (var row in snapshot.VerifiedBaskets.Take(5))
                        {
                            var c = row.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals("Conservative", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m;
                            var p = row.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m;
                            var o = row.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals("OrderbookOnly", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m;
                            var r = row.ProfileResults.FirstOrDefault(x => x.ProfileName.Equals("RawOnly", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? 0m;
                            Console.WriteLine($"[PROFILE_COMPARISON] Group={row.GroupKey} Gross={row.GrossEdge} Conservative={c} PolymarketApprox={p} OrderbookOnly={o} RawOnly={r}");
                        }
                        var bestCons = snapshot.BestByConservative?.ActiveProfileNetEdge ?? 0m;
                        var bestPolySum = snapshot.VerifiedBaskets.Select(gx => gx.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? decimal.MinValue).DefaultIfEmpty(decimal.MinValue).Max();
                        var bestRawSum = snapshot.VerifiedBaskets.Select(gx => gx.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("RawOnly", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? decimal.MinValue).DefaultIfEmpty(decimal.MinValue).Max();
                        Console.WriteLine($"[PROFILE_COMPARISON_SUMMARY] Verified={snapshot.VerifiedBaskets.Count} NearExecutable={snapshot.NearExecutableBaskets.Count} BestConservative={bestCons} BestPolymarketApprox={bestPolySum} BestRaw={bestRawSum}");
                    }
                    var nearFingerprint = string.Join("|", snapshot.NearExecutableBaskets.OrderBy(x => x.GroupKey).Select(x => $"{x.GroupKey}:{x.ActiveProfileNetEdge}:{x.CostReductionNeeded}"));
                    var shouldLogNear = !options.Logging.LogNearExecutableOnlyOnChange || nearFingerprint != nearExecutableFingerprint;
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
                        var rankingFingerprint = $"{rankedScreen.Count}|{rankedScreen[0].GrossEdge}|{rankedScreen[0].ActiveProfileNetEdge}|{rankedScreen[0].GroupKey}|{rankedScreen[0].Classification}";
                        var shouldLogRanking = !options.Logging.LogVerifiedBasketOnlyOnChangeRanking
                            || rankingFingerprint != lastRankingFingerprint
                            || (options.Logging.LogVerifiedBasketRankingEveryNCycles > 0 && verifiedBasketRankingCycle % options.Logging.LogVerifiedBasketRankingEveryNCycles == 0);
                        lastRankingFingerprint = rankingFingerprint;
                        if (shouldLogRanking) Console.WriteLine($"[VERIFIED_BASKET_RANKING] Count={rankedScreen.Count} BestGrossEdge={rankedScreen[0].GrossEdge} BestNetEdge={rankedScreen[0].ActiveProfileNetEdge} BestGroup={rankedScreen[0].GroupKey} Classification={rankedScreen[0].Classification}");
                    }
                    var bestVerifiedEdge = groupDiagnostics.Where(x=>x.BestEdge.HasValue).Select(x=>x.BestEdge!.Value).Cast<decimal?>().DefaultIfEmpty(null).Max();
                    var missingNoAskTotal = groupDiagnostics.Sum(x => x.MissingNoAskCount);
                    var noAskResolvedTotal = verifiedPricingExport.Sum(x => int.Parse(System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(x)).RootElement.GetProperty("noAskResolvedCount").ToString()));
                    var bestPricing = pricingDiagnostics.Where(x=>x.SkipReason!="MissingNoAsk").OrderByDescending(x=>x.NetEdge).FirstOrDefault();
                    var snapshotExportPath = Path.Combine(contentRootPath, "exports/verified-executable-opportunities-latest.json");
                    verifiedExecution.ExportSnapshot(snapshotExportPath, options.MultiOutcomeArbitrage.CostProfiles.ActiveProfile, promotedVerifiedOpportunities, preTradeResults, positionBook.OpenPositions);
                    var candidateReasons = string.Join(",", multiOutcomeReport.RejectedByReason.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{x.Value}"));
                    candidateScanCycle++;
                    var candidateCount = multiOutcomeReport.GroupsDetected;
                    var rejectedCount = Math.Max(0, multiOutcomeReport.GroupsDetected - multiOutcomeReport.GroupsVerified);
                    var candidateFingerprint = $"{candidateCount}|{rejectedCount}|{multiOutcomeReport.TopSkipReason}|{candidateReasons}";
                    var hasExecutableAutoCandidates = multiOutcomeReport.ExecutableGroups > 0;
                    var distributionChanged = !string.Equals(candidateFingerprint, lastCandidateScanFingerprint, StringComparison.Ordinal);
                    var periodicCandidate = options.Logging.LogCandidateScanEveryNCycles > 0 && candidateScanCycle % options.Logging.LogCandidateScanEveryNCycles == 0;
                    var firstCandidateScan = string.IsNullOrEmpty(lastCandidateScanFingerprint);
                    var shouldLogCandidate = firstCandidateScan
                        || periodicCandidate
                        || (!options.Logging.LogCandidateScanOnChangeOnly)
                        || (options.Logging.LogCandidateScanWhenRejectDistributionChanges && distributionChanged)
                        || (options.Logging.LogCandidateScanWhenExecutableOnly && hasExecutableAutoCandidates);
                    if (shouldLogCandidate)
                        Console.WriteLine($"[MULTI_CANDIDATE_SCAN] Candidates={multiOutcomeReport.GroupsDetected} Rejected={Math.Max(0,multiOutcomeReport.GroupsDetected - multiOutcomeReport.GroupsVerified)} TopReject={multiOutcomeReport.TopSkipReason} RejectedByReason={{{candidateReasons}}}");
                    lastCandidateScanFingerprint = candidateFingerprint;
                    var bestConservativeNet = snapshot.BestByConservative?.ActiveProfileNetEdge;
                    var bestPoly = snapshot.VerifiedBaskets.Select(gx => gx.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("PolymarketApprox", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? decimal.MinValue).DefaultIfEmpty(decimal.MinValue).Max();
                    var bestRaw = snapshot.VerifiedBaskets.Select(gx => gx.ProfileResults.FirstOrDefault(p => p.ProfileName.Equals("RawOnly", StringComparison.OrdinalIgnoreCase))?.NetEdge ?? decimal.MinValue).DefaultIfEmpty(decimal.MinValue).Max();
                    var unresolved = Math.Max(0, multiOutcomeValidator.LoadedAllowlistCount - verifiedResolved);
                    verifiedScanCycle++;
                    var verifiedScanFingerprint = $"{multiOutcomeValidator.LoadedAllowlistCount}|{verifiedResolved}|{unresolved}|{verifiedEvaluated}|{verifiedExecutable}|{verifiedMismatch}|{bestConservativeNet}|{bestPoly}|{bestRaw}";
                    var shouldLogVerifiedScan = string.IsNullOrEmpty(lastVerifiedScanFingerprint)
                        || !options.Logging.LogVerifiedScanOnChangeOnly
                        || verifiedScanFingerprint != lastVerifiedScanFingerprint
                        || (options.Logging.LogVerifiedScanEveryNCycles > 0 && verifiedScanCycle % options.Logging.LogVerifiedScanEveryNCycles == 0);
                    if (shouldLogVerifiedScan) Console.WriteLine($"[MULTI_VERIFIED_SCAN] Configured={multiOutcomeValidator.LoadedAllowlistCount} Resolved={verifiedResolved} Unresolved={unresolved} Evaluated={verifiedEvaluated} Executable={verifiedExecutable} Mismatch={verifiedMismatch} BestConservativeNet={(bestConservativeNet.HasValue ? bestConservativeNet.Value : 0m)} BestPolymarketApproxNet={bestPoly} BestRaw={bestRaw}");
                    lastVerifiedScanFingerprint = verifiedScanFingerprint;
                    foreach (var ug in unresolvedConfiguredGroups.Take(3)) Console.WriteLine($"[VERIFIED_UNRESOLVED_SAMPLE] {System.Text.Json.JsonSerializer.Serialize(ug)}");

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
                            suggestedAllowlistTemplate = verifiedPricingExport.Select(x => System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(x)).RootElement)
                                .Where(e => e.TryGetProperty("groupKey", out var gk) && gk.GetString() == t.groupKey)
                                .Select(e => e.TryGetProperty("suggestedPrunedAllowlistTemplate", out var tpl) ? tpl : default)
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
                    var shouldLogPortfolio = string.IsNullOrEmpty(lastPortfolioFingerprint)
                        || !options.Logging.LogPortfolioOnChangeOnly
                        || !string.Equals(portfolioFingerprint, lastPortfolioFingerprint, StringComparison.Ordinal)
                        || (options.Logging.LogPortfolioEveryNCycles > 0 && portfolioCycle % options.Logging.LogPortfolioEveryNCycles == 0);
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
                if (options.LogEmptyExecutableRanking && (options.LogEmptyOpportunityCycles || options.LogNoOpportunityCycles))
                    Console.WriteLine($"[SCAN] Markets={filtered.Count} Books={scanStats.BookOk} Candidates={scanStats.Candidates} Positive={scanStats.PositiveEdgeFound} Executable=0 BestEdge={(scanStats.Candidates>0 && scanStats.BestEdgeSeen.HasValue ? scanStats.BestEdgeSeen.Value.ToString("0.####") : "N/A")} NearMiss={scanStats.NearMisses?.Count ?? 0} DurationMs={(long)(DateTime.UtcNow - started).TotalMilliseconds}");
            }
            if (options.LogCompactScanSummary && options.LogEveryScanCycle)
            {
                var coveragePercent = scanPool.Count == 0 ? 0 : Math.Min(100m, (decimal)Math.Min(scanPool.Count, cyclesCompletedSinceDiscovery * Math.Max(1, batchSize)) / scanPool.Count * 100m);
                var estimatedCyclesToFullCoverage = batchSize == 0 ? 0 : (int)Math.Ceiling(scanPool.Count / (double)batchSize);
                var topSkipReason = scanStats.SkipReasons?.OrderByDescending(x => x.Value).FirstOrDefault().Key ?? "None";
                var obStats = orderbookService.GetStats();
                var missSample = options.Logging.LogBookCacheMissDetails ? string.Join(",", orderbookService.GetBookCacheMissSamples(options.Logging.BookCacheMissSampleSize)) : "disabled";
                Console.WriteLine($"[SCAN] Id={scanId} Pool={scanPool.Count} Batch={batchSize} Range={batchStartIndex}-{batchEndIndex} NextOffset={currentRollingOffsetAfter} Coverage={coveragePercent:0.0}% Markets={filtered.Count} Tokens={filtered.Count*2} Books={scanStats.BookOk} BookCacheMisses={obStats.BookCacheMisses} BookCacheMissSample={missSample} Snapshots={filtered.Count} Candidates={scanStats.Candidates} Positive={scanStats.PositiveEdgeFound} Executable={scanStats.Executed} BestEdge={(scanStats.Candidates>0 && scanStats.BestEdgeSeen.HasValue ? scanStats.BestEdgeSeen.Value.ToString("0.####") : "N/A")} NearMiss={scanStats.NearMisses?.Count ?? 0} TopSkip={topSkipReason} FirstMarket={filtered.FirstOrDefault()?.id ?? "-"} LastMarket={filtered.LastOrDefault()?.id ?? "-"} DistinctMarketIds={distinctMarketIdsInBatch} DurationMs={(long)(DateTime.UtcNow - started).TotalMilliseconds}");
            }
            monitor.FlushCsv();
            SyncRuntimeState(state, monitor, positionBook, executionJournalPath, executionPolicy, orderbookService, paper, filtered.Count, started, null, scanStats, filtering, lastDiscoverySummary, rollingOffset, options.ScanBatchSize, discoveredMarkets.Count, discoveryStartedAt, discoveryCompletedAt, emptyCycles, options.MarketScanLimit, effectiveMarketLimit, options.MaxMarketsToDiscover, options, poolLimitReason, multiOutcomeReport);
            if (options.RuntimeHealth.Enabled && (runtimeHealthLastLoggedAt == DateTime.MinValue || DateTime.UtcNow - runtimeHealthLastLoggedAt >= TimeSpan.FromMinutes(Math.Max(1, options.RuntimeHealth.LogEveryMinutes))))
            {
                runtimeHealthLastLoggedAt = DateTime.UtcNow;
                Console.WriteLine($"[RUNTIME_HEALTH] MemoryMb={Math.Round(System.Diagnostics.Process.GetCurrentProcess().WorkingSet64/1024d/1024d,2)} Logs={state.Logs().Length} ScannerHistory={state.ScannerStatsHistoryCount} OrderbookCache=0 Uptime={(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime())}");
            }
            await PushUiUpdates(state, hub, uiLogger, options, verifiedExecution, contentRootPath);
            uiLogger.LogInfo("scanner", $"{{\"event\":\"scan_end\",\"durationMs\":{(long)(DateTime.UtcNow - started).TotalMilliseconds},\"marketsScanned\":{filtered.Count},\"detected\":{cycleTop.Count},\"executable\":{executableCount}}}");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            break;
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
            uiLogger.LogError("scanner", $"{{\"event\":\"scan_error\",\"message\":\"{ex.Message.Replace("\"", "'")}\"}}");
            SyncRuntimeState(state, monitor, positionBook, executionJournalPath, executionPolicy, orderbookService, paper, 0, started, lastError, new SingleMarketScanStats(0,0,0,0,0,0,0,0), filtering, lastDiscoverySummary, rollingOffset, options.ScanBatchSize, discoveredMarkets.Count, discoveryStartedAt, discoveryCompletedAt, emptyCycles, options.MarketScanLimit, 0, options.MaxMarketsToDiscover, options, "Error", new MultiOutcomeGroupArbEngine.MultiOutcomeScanReport(0,0,0,0,0,0,0,0m,0m,0m,string.Empty,"Error",new Dictionary<string,int>(),Array.Empty<MultiOutcomeGroupArbEngine.RejectedSample>(),Array.Empty<MultiOutcomeGroupArbEngine.CandidateGroupReview>()));
        }

        await Task.Delay(options.ScanIntervalMs, stoppingToken);
    }
}

static async Task PushUiUpdates(BotRuntimeState state, IHubContext<BotHub> hub, IBotUiLogger logger, TradingBotOptions options, VerifiedBasketExecutionCoordinator verifiedExecution, string contentRootPath)
{
    try
    {
        await hub.Clients.All.SendAsync("opportunitiesUpdated", state.Opportunities().TakeLast(options.SignalR.MaxPayloadItems).ToArray());
        await hub.Clients.All.SendAsync("tradeLogUpdated", state.Trades().TakeLast(300).ToArray());
        verifiedExecution.ExportAudit(Path.Combine(contentRootPath, "exports/execution-audit-latest.json"));
        verifiedExecution.ExportDryRunPlans(Path.Combine(contentRootPath, "exports/dry-run-order-plans-latest.json"), options.MultiOutcomeArbitrage.CostProfiles.ActiveProfile, true);
        verifiedExecution.ExportFillSimulations(Path.Combine(contentRootPath, "exports/dry-run-fill-simulations-latest.json"));
        File.WriteAllText(Path.Combine(contentRootPath, "exports/paper-positions-latest.json"), System.Text.Json.JsonSerializer.Serialize(state.Positions().TakeLast(200), new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(contentRootPath, "exports/paper-account-latest.json"), System.Text.Json.JsonSerializer.Serialize(new {
            initialCash = 1000m,
            cash = state.Status.Cash,
            lockedCapital = state.Status.LockedCapital,
            openExposure = state.Status.LockedCapital,
            realizedPnl = state.Status.RealizedPnl,
            unrealizedPnl = state.Positions().Where(p => p.MtmStatus != "Incomplete").Sum(p => p.UnrealizedPnl),
            equity = state.Status.Equity,
            openPositionsCount = state.Status.OpenPositions,
            openBasketPositionsCount = state.Status.OpenPositions,
            lastUpdatedAt = state.Status.LastScanTime
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        await hub.Clients.All.SendAsync("positionsUpdated", state.Positions());
        await hub.Clients.All.SendAsync("scannerStatsUpdated", state.ScannerStats);
        await hub.Clients.All.SendAsync("riskUpdated", state.Risk);
        await hub.Clients.All.SendAsync("controlsUpdated", state.Controls);
        await hub.Clients.All.SendAsync("botStatusUpdated", state.Status);
        await hub.Clients.All.SendAsync("equityUpdated", state.Equity().TakeLast(options.SignalR.MaxPayloadItems).ToArray());
        await hub.Clients.All.SendAsync("terminalLogsUpdated", state.Logs().TakeLast(options.SignalR.MaxRecentLogsToBroadcast).ToArray());
    }
    catch (Exception ex)
    {
        logger.LogError("signalr", $"{{\"event\":\"push_pipeline_error\",\"message\":\"{ex.Message.Replace("\"", "'")}\"}}");
    }
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

static void SyncRuntimeState(BotRuntimeState state, OpportunityMonitor monitor, PaperPositionBook pb, string executionJournalPath, ExecutionPolicy p, OrderBookService obs, PaperTradingEngine paper, int marketsScanned, DateTime scanStart, string? lastError, SingleMarketScanStats scanStats, OpportunityFilteringOptions filtering, MarketDiscoverySummary discovery, int rollingOffset, int batchSize, int totalDiscovered, DateTime discoveryStartedAt, DateTime discoveryCompletedAt, int emptyCycles, int configuredMarketScanLimit, int effectiveMarketLimit, int configuredMaxMarketsToDiscover, TradingBotOptions options, string poolLimitReason, MultiOutcomeGroupArbEngine.MultiOutcomeScanReport multiOutcomeReport)
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

    state.ReplacePositions(pb.OpenPositions.Concat(pb.ClosedPositions).Take(200).Select(pz => new PaperPositionDto(pz.PositionId, pz.OpenedAtUtc, pz.ClosedAtUtc, pz.Strategy, pz.GroupKey, pz.Legs.Select(l => $"{l.Outcome}:{l.Question}").ToList(), pz.Quantity, pz.TotalCost, pz.CostPerBasket, pz.GuaranteedPayout, pz.Quantity * pz.Legs.Count, pz.GrossEdgeAtOpen, pz.NetEdgeAtOpen, pz.ExpectedProfit, pz.LockedCapital, pz.ActiveProfile, pz.Source, pz.CurrentNoAskSum, pz.CurrentExitValue, pz.UnrealizedPnl, pz.MtmStatus, pz.MissingExitPrices, pz.RealizedPayout, pz.RealizedProfit, pz.OpenedFromSimulatedFills, pz.FillSimulationId, pz.Status.ToString().ToUpperInvariant(), state.NextSeq())));

    state.ReplaceTrades(ReadTradeEntries(executionJournalPath, state, filtering));

    var s = obs.GetStats();

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
    state.SetStatus(new BotStatusDto("PAPER", !state.Controls.IsPaused, "CONNECTED", paper.Balance, paper.LockedCapital, paper.Equity, 0m, paper.ExpectedProfit, pb.OpenPositions.Count, top.Count, DateTime.UtcNow, DateTime.UtcNow));
    state.AddEquity(new EquityPointDto(DateTime.UtcNow, paper.Equity, state.NextSeq()));
}



static object BuildVerifiedScreenerRow(VerifiedBasketScreener.ScreenResult row, VerifiedOpportunityStabilityTracker stability, ExecutionOptions executionOptions)
{
    var json = System.Text.Json.JsonSerializer.SerializeToNode(row)!.AsObject();
    var latest = stability.LatestReadiness(row.GroupKey);
    var state = stability.State(row.GroupKey);
    json["edgeStabilityStatus"] = state is VerifiedBasketState.EdgeStable or VerifiedBasketState.ExecutionReadinessPending or VerifiedBasketState.ExecutionStable or VerifiedBasketState.PaperOpened ? "EdgeStable" : state.ToString();
    json["executionReadinessStatus"] = state == VerifiedBasketState.ExecutionStable ? "ExecutionStable" : state == VerifiedBasketState.ExecutionReadinessPending ? "ExecutionReadinessPending" : "WaitingForExecutionReadiness";
    json["consecutiveEdgeScans"] = stability.Consecutive(row.GroupKey);
    json["consecutiveExecutionReadyScans"] = stability.ConsecutiveExecutionReady(row.GroupKey);
    json["requiredConsecutiveExecutionReadyScans"] = executionOptions.RequiredConsecutiveExecutionReadyScans;
    json["latestReadinessSample"] = latest is null ? null : System.Text.Json.JsonSerializer.SerializeToNode(latest);
    json["readinessHistorySummary"] = System.Text.Json.JsonSerializer.SerializeToNode(stability.ReadinessSummaries(executionOptions.RequiredConsecutiveExecutionReadyScans).FirstOrDefault(x => x.GroupKey.Equals(row.GroupKey, StringComparison.OrdinalIgnoreCase)));
    json["notReadyReason"] = latest?.NotReadyReason;
    json["uiStatus"] = state == VerifiedBasketState.ExecutionStable ? "Actionable" : "WaitingForExecutionReadiness";
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
        original.WriteLine(value);
        mirror(value);
    }
}
