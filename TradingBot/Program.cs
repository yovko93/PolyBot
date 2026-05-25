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
builder.Services.AddSingleton<DryRunLiveExecutor>();
builder.Services.AddSingleton<BotRuntimeState>();
builder.Services.AddSingleton<TextWriter>(originalOut);
builder.Services.AddSingleton<IBotUiLogger, BotUiLogger>();

var app = builder.Build();
app.UseCors("ui");
var options = builder.Configuration.GetSection(TradingBotOptions.SectionName).Get<TradingBotOptions>() ?? new TradingBotOptions();
var listenUrl = options.ListenUrl;

app.MapHealthChecks("/health");
app.MapGet("/api/bot/health", () => Results.Ok(new { ok = true, service = "PolyBot", timestamp = DateTime.UtcNow }));
app.MapGet("/api/bot/status", (BotRuntimeState s) => s.Status);
app.MapGet("/api/bot/opportunities", (BotRuntimeState s, IOptions<OpportunityFilteringOptions> f, bool? includeNegativeEdge, bool? debug) =>
{
    var include = (includeNegativeEdge ?? false) || (debug ?? false) || f.Value.EnableDebugNegativeEdgeView;
    return s.Opportunities().Where(o => include || OpportunityVisibilityFilter.IsVisibleOpportunity(o, f.Value)).ToArray();
});
app.MapGet("/api/bot/positions", (BotRuntimeState s) => s.Positions());
app.MapGet("/api/bot/trade-log", (BotRuntimeState s) => s.Trades());
app.MapGet("/api/bot/scanner-stats", (BotRuntimeState s) => s.ScannerStats);
app.MapGet("/api/bot/opportunity-diagnostics", (BotRuntimeState s) => s.OpportunityDiagnostics);
app.MapGet("/api/bot/risk", (BotRuntimeState s, IRiskManager risk) => Results.Ok(new { runtime = s.Risk, executionRisk = risk.GetRiskSnapshot() }));
app.MapGet("/api/bot/execution-audit", (ExecutionAuditLog audit) => audit.List());
app.MapGet("/api/bot/execution-plans", (BotRuntimeState s) => s.Trades().TakeLast(100));
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
app.MapGet("/api/bot/logs/recent", (BotRuntimeState s) => s.Logs());
app.MapGet("/api/bot/equity", (BotRuntimeState s) => s.Equity());
app.MapHub<BotHub>("/hubs/bot");

var apiTask = app.RunAsync(listenUrl);
var state = app.Services.GetRequiredService<BotRuntimeState>();
var logger = app.Services.GetRequiredService<IBotUiLogger>();
options = app.Services.GetRequiredService<IOptions<TradingBotOptions>>().Value;

Console.SetOut(new MultiTextWriter(originalOut, msg => logger.LogInfo("console", msg)));
logger.LogSuccess("startup", $"Bot API listening on {listenUrl}");
logger.LogSuccess("startup", $"ExecutionMode={options.ExecutionMode}; EnablePaperTrading={options.EnablePaperTrading}; EnableLiveExecution={options.EnableLiveExecution}");
logger.LogInfo("startup", $"[CONFIG] Scanner Mode={options.Mode} MarketScanLimit={options.MarketScanLimit} MaxMarketsToDiscover={options.MaxMarketsToDiscover} ScanBatchSize={options.ScanBatchSize} MaxOrderbooksPerCycle={options.MaxOrderbooksPerCycle} MaxConcurrentOrderbookRequests={options.MaxConcurrentOrderbookRequests} LogEmptyOpportunityCycles={options.LogEmptyOpportunityCycles}");

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

await RunScannerAsync(state, logger, app.Services.GetRequiredService<IHubContext<BotHub>>(), options, app.Services.GetRequiredService<IOptions<OpportunityFilteringOptions>>().Value, app.Lifetime.ApplicationStopping);
await apiTask;

static async Task RunScannerAsync(BotRuntimeState state, IBotUiLogger uiLogger, IHubContext<BotHub> hub, TradingBotOptions options, OpportunityFilteringOptions filtering, CancellationToken stoppingToken)
{
    var scannerInstanceId = Guid.NewGuid().ToString("N");
    var scannerStartedAt = DateTime.UtcNow;
    Console.WriteLine($"[SCANNER] Background scanner started InstanceId={scannerInstanceId}");
    using var http = new HttpClient();
    http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    http.Timeout = TimeSpan.FromSeconds(options.ExternalApiTimeoutSeconds);

    var marketService = new MarketDataService(http);
    var orderbookService = new OrderBookService(http) { DisableSingleBookHttpFallback = true, LogPrefetchDetails = options.LogPrefetchDetails };
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
                await PushUiUpdates(state, hub, uiLogger);
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
            Console.WriteLine($"[SCAN_CONFIG] ActiveMarkets={discoveredMarkets.Count} PoolLimit={effectiveMarketLimit} Reason={poolLimitReason}");
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
                Console.WriteLine($"[SCAN] Id={scanId} Pool={scanPool.Count} Batch={batchSize} Range={batchStartIndex}-{batchEndIndex} NextOffset={currentRollingOffsetAfter} Coverage={coveragePercent:0.0}% Markets={filtered.Count} Tokens={filtered.Count*2} Books={scanStats.BookOk} Snapshots={filtered.Count} Candidates={scanStats.Candidates} Positive={scanStats.PositiveEdgeFound} Executable={scanStats.Executed} BestEdge={(scanStats.Candidates>0 && scanStats.BestEdgeSeen.HasValue ? scanStats.BestEdgeSeen.Value.ToString("0.####") : "N/A")} NearMiss={scanStats.NearMisses?.Count ?? 0} TopSkip={topSkipReason} FirstMarket={filtered.FirstOrDefault()?.id ?? "-"} LastMarket={filtered.LastOrDefault()?.id ?? "-"} DistinctMarketIds={distinctMarketIdsInBatch} DurationMs={(long)(DateTime.UtcNow - started).TotalMilliseconds}");
            }
            monitor.FlushCsv();
            SyncRuntimeState(state, monitor, positionBook, executionJournalPath, executionPolicy, orderbookService, paper, filtered.Count, started, null, scanStats, filtering, lastDiscoverySummary, rollingOffset, options.ScanBatchSize, discoveredMarkets.Count, discoveryStartedAt, discoveryCompletedAt, emptyCycles, options.MarketScanLimit, effectiveMarketLimit, options.MaxMarketsToDiscover, options, poolLimitReason);
            await PushUiUpdates(state, hub, uiLogger);
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
            SyncRuntimeState(state, monitor, positionBook, executionJournalPath, executionPolicy, orderbookService, paper, 0, started, lastError, new SingleMarketScanStats(0,0,0,0,0,0,0,0), filtering, lastDiscoverySummary, rollingOffset, options.ScanBatchSize, discoveredMarkets.Count, discoveryStartedAt, discoveryCompletedAt, emptyCycles, options.MarketScanLimit, 0, options.MaxMarketsToDiscover, options, "Error");
        }

        await Task.Delay(options.ScanIntervalMs, stoppingToken);
    }
}

static async Task PushUiUpdates(BotRuntimeState state, IHubContext<BotHub> hub, IBotUiLogger logger)
{
    try
    {
        await hub.Clients.All.SendAsync("opportunitiesUpdated", state.Opportunities());
        await hub.Clients.All.SendAsync("tradeLogUpdated", state.Trades());
        await hub.Clients.All.SendAsync("positionsUpdated", state.Positions());
        await hub.Clients.All.SendAsync("scannerStatsUpdated", state.ScannerStats);
        await hub.Clients.All.SendAsync("riskUpdated", state.Risk);
        await hub.Clients.All.SendAsync("controlsUpdated", state.Controls);
        await hub.Clients.All.SendAsync("botStatusUpdated", state.Status);
        await hub.Clients.All.SendAsync("equityUpdated", state.Equity());
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

static void SyncRuntimeState(BotRuntimeState state, OpportunityMonitor monitor, PaperPositionBook pb, string executionJournalPath, ExecutionPolicy p, OrderBookService obs, PaperTradingEngine paper, int marketsScanned, DateTime scanStart, string? lastError, SingleMarketScanStats scanStats, OpportunityFilteringOptions filtering, MarketDiscoverySummary discovery, int rollingOffset, int batchSize, int totalDiscovered, DateTime discoveryStartedAt, DateTime discoveryCompletedAt, int emptyCycles, int configuredMarketScanLimit, int effectiveMarketLimit, int configuredMaxMarketsToDiscover, TradingBotOptions options, string poolLimitReason)
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

    state.ReplacePositions(pb.OpenPositions.Concat(pb.ClosedPositions).Take(200).Select(pz => new PaperPositionDto(pz.PositionId, pz.OpenedAtUtc, pz.ClosedAtUtc, pz.Strategy, pz.GroupKey, pz.Legs.Select(l => $"{l.Outcome}:{l.Question}").ToList(), pz.TotalCost, pz.GuaranteedPayout, pz.ExpectedProfit, pz.RealizedPayout, pz.RealizedProfit, pz.Status.ToString().ToUpperInvariant(), state.NextSeq())));

    state.ReplaceTrades(ReadTradeEntries(executionJournalPath, state, filtering));

    var s = obs.GetStats();

    var edges = top.Select(x => x.EdgePerShare).OrderBy(x => x).ToList();
    var median = edges.Count == 0 ? 0m : edges[edges.Count / 2];
    var nearMissTop = (scanStats.NearMisses ?? new List<NearMissOpportunity>()).OrderByDescending(x => x.EdgePerShare).Take(options.Diagnostics.NearMissTopN).Select((x,i)=>x with { Rank = i+1 }).ToList();
    var skipReasonList = (scanStats.SkipReasons ?? new Dictionary<string,int>()).OrderByDescending(x=>x.Value).Select(x=>new SkipReasonSummary(x.Key, x.Value, null, null)).ToList();
    var diag = new OpportunityDiagnosticsSnapshot(Guid.NewGuid().ToString("N"), DateTime.UtcNow, marketsScanned, (int)Math.Min(int.MaxValue, s.BatchBooksLoaded), scanStats.BothAsks, scanStats.Candidates, scanStats.PositiveEdgeFound, scanStats.Executed, nearMissTop.Count, scanStats.BestEdgeSeen ?? 0m, scanStats.WorstEdgeSeen ?? 0m, edges.Count==0?0m:edges.Average(), median, nearMissTop.Count==0?0m:nearMissTop.First().MissingToBreakEven, skipReasonList, new [] { new StrategyBreakdownItem("BUY_YES_AND_BUY_NO", marketsScanned, scanStats.Candidates, scanStats.PositiveEdgeFound, scanStats.Executed, scanStats.BestEdgeSeen ?? 0m, nearMissTop.Count, skipReasonList.FirstOrDefault()?.Reason ?? "None") }, (long)(DateTime.UtcNow-scanStart).TotalMilliseconds, nearMissTop, new Dictionary<string,int>{{"minEdge 0.000", edges.Count(x=>x>0m)}, {"minEdge 0.001", edges.Count(x=>x>=0.001m)}, {"minEdge 0.005", edges.Count(x=>x>=0.005m)}});
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
