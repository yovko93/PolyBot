using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Services;

class Program
{
    static async Task Main()
    {
        using var http = new HttpClient();

        http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
        http.Timeout = TimeSpan.FromSeconds(10);

        var marketService = new MarketDataService(http);
        var orderbookService = new OrderBookService(http);
        orderbookService.DisableSingleBookHttpFallback = true;

        var executionPolicy = new ExecutionPolicy
        {
            MaxNotionalPerTrade = 100m,
            MinNotionalPerTrade = 5m,
            MinEdgePerShare = 0.003m,
            MinExpectedProfit = 0.25m,
            MaxLockedCapital = 300m,
            MaxOpenPositions = 5,
            MaxExposurePerGroup = 100m,

            AllowBasketArbs = true,
            AllowSingleMarketArbs = true,
            AllowCompleteSetSellArbs = true,
            AllowThresholdArbs = true
        };
        var sizing = new ExecutionSizingService(executionPolicy);

        var executionDecisionService = new ExecutionDecisionService(executionPolicy);

        var executionJournal = new ExecutionJournal(
            csvPath: Path.Combine(AppContext.BaseDirectory, "data", "execution-journal.csv")
        );

        var positionBook = new PaperPositionBook(
            csvPath: Path.Combine(AppContext.BaseDirectory, "data", "paper-positions.csv")
        );

        var paper = new PaperTradingEngine(
            executionPolicy,
            executionJournal,
            executionDecisionService,
            positionBook
        );

        var settlementService = new PaperSettlementService(
            paper,
            positionBook
        );

        var dryRunOrderBuilder = new DryRunLiveOrderBuilder(
            minEdgePerShare: -0.01m,
            maxPlanCost: 100000m,
            minSize: 1m,
            tickSize: 0.001m,
            orderType: LiveOrderType.FOK
            ,
            policy: executionPolicy
        );

        var monitor = new OpportunityMonitor(
            csvPath: Path.Combine(AppContext.BaseDirectory, "data", "arb-opportunities.csv"),
            alertEdgeThreshold: 0.003m,
            minRecordEdgePerShare: -0.02m,
            minAlertExpectedProfit: 0.25m,
            alertCooldown: TimeSpan.FromMinutes(2),
            dryRunOrderBuilder: dryRunOrderBuilder
        );

        var semaphore = new SemaphoreSlim(5);

        var depthSimulator = new OrderBookDepthSimulator(orderbookService);
        var requoteGate = new RequoteExecutionGate(depthSimulator);

        var singleMarketArb = new SingleMarketOrderBookArbEngine(
            orderbookService,
            minEdgePerShare: 0.003m,
            feeBuffer: 0.001m,
            slippageBuffer: 0.001m,
            monitor: monitor,
            sizing: sizing
        );

        var completeSetSellArb = new CompleteSetSellArbEngine(
            orderbookService,
            minEdgePerShare: 0.003m,
            feeBuffer: 0.001m,
            slippageBuffer: 0.001m,
            monitor: monitor,
            sizing: sizing
        );

        var thresholdArb = new ThresholdDominanceArbEngine(
            orderbookService,
            minEdgePerShare: 0.005m,
            feeBuffer: 0.001m,
            slippageBuffer: 0.001m,
            monitor: monitor,
            sizing: sizing
        );

        var multiOutcomeArb = new MultiOutcomeGroupArbEngine(
            orderbookService,
            minEdgePerShare: 0.005m,
            feeBufferPerLeg: 0.001m,
            slippageBufferPerLeg: 0.001m,
            enableYesBasket: false,
            monitor: monitor,
            requoteGate: requoteGate, //todo use this
            //requoteGate: null,
            decisionService: executionDecisionService
        );

        var semanticMatcher = new SemanticMarketMatcher(
            minScore: 0.93,
            debug: false
        );

        var trueArb = new TrueArbitrageEngine(
            orderBooks: orderbookService,
            matcher: semanticMatcher,
            minEdgePerShare: 0.01m,
            feeBuffer: 0.002m,
            slippageBuffer: 0.002m,
            monitor: monitor,
            sizing: sizing
        );

        var simResolution = new SimulatedResolutionEngine(TimeSpan.FromMinutes(2));

        var nextMarketRefreshUtc = DateTime.MinValue;
        var nextTrueArbScanUtc = DateTime.MinValue;

        List<Market> cachedMarkets = new();

        var settlementTestDone = false;

        while (true)
        {
            try
            {
                monitor.BeginCycle();

                if (DateTime.UtcNow >= nextMarketRefreshUtc)
                {
                    cachedMarkets = await marketService.GetMarketsAsync();
                    nextMarketRefreshUtc = DateTime.UtcNow.AddMinutes(1);

                    Console.WriteLine();
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Market universe refreshed: {cachedMarkets.Count}");
                }

                var filtered = cachedMarkets
                    .Where(m =>
                        m != null &&
                        !string.IsNullOrWhiteSpace(m.question) &&
                        m.outcomes != null &&
                        m.outcomes.Count == 2 &&
                        m.clobTokenIds != null &&
                        m.clobTokenIds.Count >= 2)
                    .Take(1000)
                    .ToList();

                Console.WriteLine();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Markets loaded: {cachedMarkets.Count}");
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Markets scanned: {filtered.Count}");

                await orderbookService.PrefetchBinarySnapshotsAsync(filtered);

                await singleMarketArb.ScanAsync(filtered, paper, semaphore);
                await completeSetSellArb.ScanAsync(filtered, paper, semaphore);
                await thresholdArb.ScanAsync(filtered, paper, semaphore);
                await multiOutcomeArb.ScanAsync(filtered, paper, semaphore);

                if (DateTime.UtcNow >= nextTrueArbScanUtc)
                {
                    await trueArb.ScanAsync(filtered, paper, semaphore);

                    nextTrueArbScanUtc = DateTime.UtcNow.AddMinutes(1);
                }

                simResolution.Scan(paper);

                monitor.PrintCycleRanking(top: 15, executableOnly: false);
                //monitor.BuildDebugDryRunForTopRecorded(top: 3);
                monitor.FlushCsv();

                var stats = orderbookService.GetStats();

                Console.WriteLine();
                Console.WriteLine("========== ORDERBOOK HEALTH ==========");
                Console.WriteLine($"Batch requests: {stats.BatchRequests}");
                Console.WriteLine($"Batch books loaded: {stats.BatchBooksLoaded}");
                Console.WriteLine($"Single /book requests: {stats.SingleRequests}");
                Console.WriteLine($"Book cache hits: {stats.CacheHits}");
                Console.WriteLine($"Snapshot cache hits: {stats.SnapshotCacheHits}");
                Console.WriteLine($"Timeouts: {stats.Timeouts}");
                Console.WriteLine($"HTTP errors: {stats.HttpErrors}");
                Console.WriteLine($"Parse errors: {stats.ParseErrors}");
                Console.WriteLine("======================================");
                Console.WriteLine();

                orderbookService.ResetStats();

                Console.WriteLine(
                    $"💰 Cash: {paper.Balance:0.##} | " +
                    $"Locked: {paper.LockedCapital:0.##} | " +
                    $"Expected Profit: {paper.ExpectedProfit:0.####} | " +
                    $"Equity: {paper.Equity:0.####}"
                );

                positionBook.PrintOpenPositions(top: 5);
                settlementService.PrintSettlementCandidates();
                positionBook.PrintSessionStatistics(
                    monitor: monitor,
                    feeRatePerLeg: 0.001m,
                    topOpportunities: 5
                );

                // settlement по GroupKey го пускай само временно, когато искаш да тестваш close flow.
                //if (!settlementTestDone)
                //{
                //    settlementService.SettleGroupAtGuaranteedPayout(
                //        "winner:ohio senate race in 2026|kind:party"
                //    );

                //    settlementTestDone = true;
                //}

                orderbookService.ClearExpiredCache();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PROGRAM ERROR] {ex.Message}");
            }

            await Task.Delay(3000);
        }
    }
}
