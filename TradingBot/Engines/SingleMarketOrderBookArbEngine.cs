using System.Collections.Concurrent;
using System.Text.Json;
using TradingBot.Api;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;

namespace TradingBot.Engines;

public record SingleMarketScanStats(int Scanned,int BookOk,int BothAsks,int Candidates,int Executed,int PositiveEdgeFound,int NegativeEdgeSkipped,int ZeroEdgeSkipped, Dictionary<string,int>? SkipReasons = null, List<NearMissOpportunity>? NearMisses = null, decimal? BestEdgeSeen = null, decimal? WorstEdgeSeen = null, int ExecutionReady = 0, int FillPassed = 0, int CircuitBreakerSkippedMarkets = 0, int CircuitBreakerSkippedCycles = 0);

public class SingleMarketOrderBookArbEngine
{
    private const string StrategyName = "BUY_YES_AND_BUY_NO";
    private readonly IOrderBookProvider _orderBooks;
    private readonly decimal _minEdgePerShare;
    private readonly decimal _feeBuffer;
    private readonly decimal _slippageBuffer;
    private readonly OpportunityMonitor? _monitor;
    private readonly ExecutionSizingService _sizing;
    private readonly bool _sizingLogsEnabled;
    private readonly SingleMarketArbOptions _options;
    private readonly MultiOutcomeLoggingOptions _logging;
    private readonly bool _operationalQuietMode;
    private readonly BotRuntimeState? _state;
    private readonly TradingBotOptions? _botOptions;
    private readonly string? _contentRootPath;
    private readonly SingleMarketDataQualityValidator _dataQuality;
    private readonly SingleMarketFillSimulator _fillSimulator = new();
    private readonly VerifiedBasketExecutionCoordinator? _audit;
    private readonly QuietLogGate? _quietLogGate;
    private readonly OpportunityExecutionQueue? _executionQueue;
    private readonly StrategyMode _strategyMode;
    private readonly PaperPhase1RealWatchService? _realWatch;
    private readonly object _gate = new();
    private readonly Dictionary<string, MarketStability> _stability = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSummaryHash = string.Empty;
    private string _lastNearMissHash = string.Empty;
    private bool _hasLoggedDataQualitySummary;
    private int _lastLoggedDataQualityTotal;
    private Dictionary<string, int> _lastLoggedDataQualityCounts = new(StringComparer.OrdinalIgnoreCase);
    private long _currentDataQualityFullCycleId = -1;
    private int _highSeverityDataQualityLogsThisCycle;
    private int _highSeverityDataQualitySuppressedThisCycle;
    private int _dataQualityAuditSamplesThisCycle;
    private readonly SingleMarketDataQualityAuditHourlyCap _highSeverityDataQualityAuditHourlyCap = new();
    private readonly Dictionary<string, DateTime> _highSeverityDataQualityLastLoggedAt = new(StringComparer.OrdinalIgnoreCase);
    private long _scanId;
    private int _positionsOpenedThisCycle;
    private readonly Dictionary<string, SingleMarketOpportunityLogState> _opportunityLogStates = new(StringComparer.OrdinalIgnoreCase);

    public SingleMarketOrderBookArbEngine(
        IOrderBookProvider orderBooks,
        decimal minEdgePerShare = 0.005m,
        decimal feeBuffer = 0.001m,
        decimal slippageBuffer = 0.001m,
        OpportunityMonitor? monitor = null,
        ExecutionSizingService? sizing = null,
        SingleMarketArbOptions? options = null,
        BotRuntimeState? state = null,
        string? contentRootPath = null,
        VerifiedBasketExecutionCoordinator? audit = null,
        bool operationalQuietMode = false,
        MultiOutcomeLoggingOptions? logging = null,
        QuietLogGate? quietLogGate = null,
        OpportunityExecutionQueue? executionQueue = null,
        StrategyMode strategyMode = StrategyMode.PaperEligible,
        TradingBotOptions? botOptions = null,
        PaperPhase1RealWatchService? realWatch = null)
    {
        _orderBooks = orderBooks;
        _options = options ?? new SingleMarketArbOptions { MinEdgePerShare = minEdgePerShare };
        _minEdgePerShare = _options.MinEdgePerShare;
        _feeBuffer = feeBuffer;
        _slippageBuffer = slippageBuffer;
        _monitor = monitor;
        _sizing = sizing ?? new ExecutionSizingService(new ExecutionPolicy { MaxNotionalPerTrade = _options.MaxNotionalPerTrade, MinNotionalPerTrade = _options.MinNotional });
        _sizingLogsEnabled = _sizing.EnableSizingLogs;
        _state = state;
        _botOptions = botOptions;
        _contentRootPath = contentRootPath;
        _dataQuality = new SingleMarketDataQualityValidator(_options);
        _audit = audit;
        _operationalQuietMode = operationalQuietMode;
        _logging = logging ?? new MultiOutcomeLoggingOptions();
        _quietLogGate = quietLogGate;
        _executionQueue = executionQueue;
        _strategyMode = strategyMode;
        _realWatch = realWatch;
    }

    public async Task<SingleMarketScanStats> ScanAsync(List<Market> markets, PaperTradingEngine paper, SemaphoreSlim semaphore, CancellationToken ct = default)
        => await ScanAsync(markets, paper, semaphore, fullCycleId: null, isFullCycleComplete: false, suppressBatchDataQualitySummary: false, ct);

    public async Task<SingleMarketScanStats> ScanAsync(List<Market> markets, PaperTradingEngine paper, SemaphoreSlim semaphore, long? fullCycleId, bool isFullCycleComplete, bool suppressBatchDataQualitySummary = false, CancellationToken ct = default)
    {
        _positionsOpenedThisCycle = 0;
        var scanId = Interlocked.Increment(ref _scanId);
        BeginDataQualityFullCycle(fullCycleId ?? scanId);
        var diagnostics = new SingleMarketCycleDiagnostics(scanId);
        if (_orderBooks is OrderBookService orderBookService && orderBookService.GetStats().OrderbookCircuitBreakerActive)
        {
            var skipped = markets.Count;
            var skippedReasons = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["SingleMarketSkippedByCircuitBreaker"] = skipped };
            return new SingleMarketScanStats(0, 0, 0, 0, 0, 0, 0, 0, skippedReasons, CircuitBreakerSkippedMarkets: skipped, CircuitBreakerSkippedCycles: skipped > 0 ? 1 : 0);
        }
        var tasks = markets.Select(market => ScanMarketAsync(market, paper, semaphore, diagnostics, ct));
        var results = await Task.WhenAll(tasks);
        var summary = BuildAndPublishSnapshot(diagnostics);
        MaybeLogSummaries(summary, diagnostics, suppressBatchDataQualitySummary);
        if (isFullCycleComplete) MaybeLogSuppressedHighSeverityDataQuality();
        ExportLatest();

        var skipReasons = new Dictionary<string, int>();
        var nearMisses = new List<NearMissOpportunity>();
        var edges = new List<decimal>();
        foreach (var r in results)
        {
            if (r.Candidate && r.Edge.HasValue) edges.Add(r.Edge.Value);
            if (!string.IsNullOrWhiteSpace(r.SkipReason)) skipReasons[r.SkipReason!] = skipReasons.GetValueOrDefault(r.SkipReason!, 0) + 1;
            if (r.NearMiss != null) nearMisses.Add(r.NearMiss);
        }
        var circuitSkipped = results.Count(x => x.SkipReason == "SingleMarketSkippedByCircuitBreaker");
        return new SingleMarketScanStats(Math.Max(0, results.Length - circuitSkipped), results.Count(x => x.BookOk), results.Count(x => x.BothAsks), results.Count(x => x.Candidate), results.Count(x => x.Executed), results.Count(x => x.Candidate && x.AdjustedCost.HasValue && 1m - x.AdjustedCost.Value > 0m), results.Count(x => x.Candidate && x.AdjustedCost.HasValue && 1m - x.AdjustedCost.Value < 0m), results.Count(x => x.Candidate && x.AdjustedCost.HasValue && 1m - x.AdjustedCost.Value == 0m), skipReasons, nearMisses, edges.Count>0?edges.Max():null, edges.Count>0?edges.Min():null, diagnostics.ExecutionReady, diagnostics.FillPassed, circuitSkipped, circuitSkipped > 0 ? 1 : 0);
    }

    private async Task<SingleMarketScanResult> ScanMarketAsync(Market market, PaperTradingEngine paper, SemaphoreSlim semaphore, SingleMarketCycleDiagnostics diagnostics, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            if (!_options.Enabled) return SingleMarketScanResult.Empty;
            if (_orderBooks is OrderBookService orderBookServiceForMarket)
            {
                if (orderBookServiceForMarket.GetStats().OrderbookCircuitBreakerActive)
                {
                    diagnostics.AddReject("SingleMarketSkippedByCircuitBreaker");
                    return new SingleMarketScanResult(false, false, false, false, null, market.question, null, "SingleMarketSkippedByCircuitBreaker", null);
                }
                if (orderBookServiceForMarket.IsMarketOrderbookQuarantined(market.id))
                {
                    diagnostics.AddReject("SingleMarketSkippedByMarketOrderbookQuarantine");
                    return new SingleMarketScanResult(false, false, false, false, null, market.question, null, "SingleMarketSkippedByMarketOrderbookQuarantine", null);
                }
            }
            diagnostics.IncrementScanned();
            var now = DateTime.UtcNow;
            var book = await _orderBooks.GetBinarySnapshotAsync(market, ct);
            if (book == null)
            {
                diagnostics.AddReject("OrderbookUnavailable");
                return new SingleMarketScanResult(false, false, false, false, null, market.question, null, "OrderbookUnavailable", null);
            }
            diagnostics.IncrementBookOk();
            if (book.TimestampUtc == default) book = book with { TimestampUtc = now };

            var yes = book.YesAsk?.Price ?? 0m;
            var no = book.NoAsk?.Price ?? 0m;
            var rawCost = yes + no;
            var adjustedCost = rawCost + _feeBuffer + _slippageBuffer;
            var edge = 1m - adjustedCost;

            var dq = _dataQuality.Validate(market, book, now);
            if (!dq.IsValid)
            {
                var rawEdge = 1m - rawCost;
                diagnostics.RecordAuditCandidate(AuditNearMiss(book, market.conditionId, yes, no, rawCost, rawEdge, edge, edge, 0m, 0m, 0m, dq.Reason, dq.Reason, false, false, false, false));
                if (rawEdge > 0m) diagnostics.IncrementDataQualityRejectedRawPositive();
                diagnostics.RecordRejectedRawEdge(rawEdge);
                diagnostics.AddDataQualityReject(dq.Reason, Sample(book, market.conditionId, yes, no, rawCost, dq.Reason, edge));
                var reject = BuildDto(book, market.conditionId, SingleMarketArbState.Rejected, yes, no, rawCost, edge, 0m, 0m, 0m, "Rejected", "NotRun", "NotOpened", dq.Reason, 0, 0);
                MaybeStoreOpportunity(reject, highValue: IsHighSeverityDataQuality(dq.Reason, rawCost));
                var highSeverityDataQuality = IsHighSeverityDataQuality(dq.Reason, rawCost);
                if (highSeverityDataQuality) diagnostics.IncrementHighSeverityDataQuality();
                var shouldLogDataQualityReject = ShouldLogDataQualityReject(book.MarketId, dq.Reason, rawCost, now);
                MaybeAuditDataQuality(reject, dq.Reason, rawCost, edge, diagnostics, suppressHighSeverityForCooldown: highSeverityDataQuality && !shouldLogDataQualityReject);
                if (shouldLogDataQualityReject)
                    Console.WriteLine($"[SINGLE_MARKET_DATA_QUALITY_REJECTED] Market={book.Question} Reason={dq.Reason} YesAsk={yes} NoAsk={no} RawSum={rawCost}");
                return new SingleMarketScanResult(true, book.YesAsk != null && book.NoAsk != null, false, false, adjustedCost, book.Question, edge, dq.Reason, null);
            }

            diagnostics.IncrementBothAsks();
            var validRawEdge = 1m - rawCost;
            diagnostics.RecordValidRawEdge(validRawEdge);
            diagnostics.RecordValidAfterCostEdge(edge);
            diagnostics.RecordValidAfterSafetyEdge(edge);
            diagnostics.RecordEdgeDistribution(validRawEdge, edge, edge);
            if (validRawEdge > 0m) diagnostics.IncrementValidRawPositive();
            if (edge > 0m) diagnostics.IncrementValidAfterCostPositive();
            if (edge > 0m) diagnostics.IncrementValidAfterSafetyPositive();
            diagnostics.RecordValidEdge(edge);
            var quantityAvailable = Math.Min(book.YesAsk!.Size, book.NoAsk!.Size);
            var sizing = _sizing.SizeByNotional(quantityAvailable, adjustedCost);
            var quantity = sizing.ExecutableQuantity;
            if (_sizingLogsEnabled && edge >= _minEdgePerShare && quantity > 0m && sizing.WasClamped)
                Console.WriteLine($"[SIZING] Strategy={StrategyName} AvailableQty={sizing.QuantityAvailable:0.####} ExecutableQty={sizing.ExecutableQuantity:0.####} Notional={sizing.Notional:0.####} MaxNotional={sizing.MaxNotional:0.####} Edge={edge:0.####}");

            var expected = quantity * edge;
            RecordMonitor(book, edge, adjustedCost, quantity);
            var detected = BuildDto(book, market.conditionId, SingleMarketArbState.CandidateDetected, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", null, GetStability(book.MarketId).EdgeScans, GetStability(book.MarketId).ExecutionScans);
            if (_options.AuditDetectedEvents) MaybeAudit(detected, "SingleMarketDetected", highValue: false, sampled: diagnostics.ShouldAuditSample(_options.MaxAuditSamplesPerCycle));

            if (edge < _minEdgePerShare || quantity <= 0m || expected < _options.MinExpectedProfit || quantity * adjustedCost < _options.MinNotional)
            {
                ResetStability(book.MarketId);
                var reason = edge < _minEdgePerShare ? "BelowMinEdge" : quantity <= 0m ? "InsufficientLiquidity" : expected < _options.MinExpectedProfit ? "BelowMinExpectedProfit" : "BelowMinNotional";
                diagnostics.AddReject(reason);
                if (edge < _minEdgePerShare) diagnostics.AddNearMiss(NearMiss(book, market.conditionId, yes, no, rawCost, edge));
                if (reason == "InsufficientLiquidity") diagnostics.IncrementRejectedByDepth();
                diagnostics.RecordAuditCandidate(AuditNearMiss(book, market.conditionId, yes, no, rawCost, validRawEdge, edge, edge, quantityAvailable, quantity, quantity * adjustedCost, reason, null, false, quantity > 0m, false, false));
                var pending = BuildDto(book, market.conditionId, SingleMarketArbState.EdgePending, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", reason, 0, 0);
                if (_options.AuditBelowMinEdgeEvents || reason != "BelowMinEdge") MaybeAudit(pending, "SingleMarketEdgePending", highValue: false, sampled: diagnostics.ShouldAuditSample(_options.MaxAuditSamplesPerCycle));
                if (!_operationalQuietMode && reason != "BelowMinEdge") Console.WriteLine($"[SINGLE_MARKET_EDGE_PENDING] MarketId={book.MarketId} Reason={reason} Edge={edge:0.####}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,reason,null);
            }

            diagnostics.IncrementPositiveEdge();
            var positive = BuildDto(book, market.conditionId, SingleMarketArbState.EdgePending, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", "WaitingForEdgeStability", GetStability(book.MarketId).EdgeScans, GetStability(book.MarketId).ExecutionScans);
            diagnostics.AddPositiveCandidate(positive);
            MaybeStoreOpportunity(positive, highValue: true);
            MaybeAudit(positive, "SingleMarketPositiveEdgeDetected", highValue: true, sampled: true);
            LogOpportunityState(book.MarketId, "EdgePending", edge, GetStability(book.MarketId).EdgeScans);
            Console.WriteLine($"[SINGLE_MARKET_POSITIVE_EDGE_DETECTED] MarketId={book.MarketId} Edge={edge:0.####} ExpectedProfit={expected:0.####} Qty={quantity:0.####}");

            var st = IncrementEdge(book.MarketId);
            if (st.EdgeScans < _options.RequiredConsecutiveEdgeScans)
            {
                diagnostics.RecordAuditCandidate(AuditNearMiss(book, market.conditionId, yes, no, rawCost, validRawEdge, edge, edge, quantityAvailable, quantity, quantity * adjustedCost, "EdgePending", null, false, true, false, false));
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,"EdgePending",null);
            }

            diagnostics.IncrementEdgeStable();
            RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.EdgeStable, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", null, st.EdgeScans, st.ExecutionScans), "SingleMarketEdgeStable");
            LogOpportunityState(book.MarketId, "EdgeStable", edge, st.EdgeScans);
            Console.WriteLine($"[SINGLE_MARKET_EDGE_STABLE] MarketId={book.MarketId} Edge={edge:0.####} Consecutive={st.EdgeScans}");

            var readinessReason = CheckExecutionReadiness(book, quantity, expected, quantity * adjustedCost);
            if (readinessReason != "Ok")
            {
                ResetExecution(book.MarketId);
                diagnostics.AddReject(readinessReason);
                diagnostics.IncrementRejectedByRisk();
                diagnostics.RecordAuditCandidate(AuditNearMiss(book, market.conditionId, yes, no, rawCost, validRawEdge, edge, edge, quantityAvailable, quantity, quantity * adjustedCost, readinessReason, null, false, true, false, false));
                RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.ExecutionReadinessPending, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", readinessReason, st.EdgeScans, 0), "SingleMarketRiskRejected");
                Console.WriteLine($"[SINGLE_MARKET_EXECUTION_READINESS_PENDING] MarketId={book.MarketId} Reason={readinessReason}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,readinessReason,null);
            }

            st = IncrementExecution(book.MarketId);
            if (st.ExecutionScans < _options.RequiredConsecutiveExecutionReadyScans)
            {
                diagnostics.RecordAuditCandidate(AuditNearMiss(book, market.conditionId, yes, no, rawCost, validRawEdge, edge, edge, quantityAvailable, quantity, quantity * adjustedCost, "ExecutionReadinessPending", null, false, true, false, false));
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,"ExecutionReadinessPending",null);
            }

            diagnostics.IncrementExecutionReady();
            diagnostics.RecordBestExecutableEdge(edge);
            RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.ExecutionStable, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", null, st.EdgeScans, st.ExecutionScans), "SingleMarketExecutionReadinessStable");
            LogOpportunityState(book.MarketId, "ExecutionStable", edge, st.EdgeScans);
            Console.WriteLine($"[SINGLE_MARKET_EXECUTION_READINESS_STABLE] MarketId={book.MarketId} Edge={edge:0.####} Qty={quantity:0.####}");

            var duplicate = paper.GetSingleMarketDuplicateDiagnostics(book.MarketId, StrategyName);
            if (duplicate.ExistingPositionFound)
            {
                LogDuplicateDiagnostics(duplicate.WithAction("Suppress"));
                return SuppressExecution(book, market.conditionId, diagnostics, adjustedCost, yes, no, rawCost, edge, expected, quantity, st.EdgeScans, st.ExecutionScans, "DuplicateOpenPosition");
            }
            if (duplicate.InFlightFound)
            {
                LogDuplicateDiagnostics(duplicate.WithAction("Suppress"));
                return SuppressExecution(book, market.conditionId, diagnostics, adjustedCost, yes, no, rawCost, edge, expected, quantity, st.EdgeScans, st.ExecutionScans, "InFlightDuplicate");
            }
            if (duplicate.DedupeRegistryContains)
            {
                LogDuplicateDiagnostics(duplicate.WithAction("ClearStaleAndContinue"));
                paper.ClearSingleMarketDedupe(book.MarketId, StrategyName);
                _state?.RecordPaperStaleDedupeEntryCleared();
                Console.WriteLine($"[PAPER_DUPLICATE_STALE_ENTRY_CLEARED] MarketId={book.MarketId} PositionKey={duplicate.PositionKey} DedupeKey={duplicate.DedupeKey}");
            }

            var suppression = CheckCooldown(book.MarketId);
            if (suppression != "Ok")
            {
                return SuppressExecution(book, market.conditionId, diagnostics, adjustedCost, yes, no, rawCost, edge, expected, quantity, st.EdgeScans, st.ExecutionScans, suppression);
            }

            var risk = TryReserveCycleSlot() ? "Ok" : "MaxPositionsPerCycleReached";
            if (risk != "Ok")
            {
                LogExecutionDecision(book.MarketId, edge, quantity, quantity * adjustedCost, expected, "RejectRisk", risk, $"single-market:{book.MarketId}");
                diagnostics.AddReject(risk);
                diagnostics.IncrementRejectedByRisk();
                diagnostics.RecordAuditCandidate(AuditNearMiss(book, market.conditionId, yes, no, rawCost, validRawEdge, edge, edge, quantityAvailable, quantity, quantity * adjustedCost, risk, null, false, true, false, false));
                RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.Rejected, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "Rejected", risk, st.EdgeScans, st.ExecutionScans), "SingleMarketRiskRejected");
                Console.WriteLine($"[PAPER_PRETRADE_REJECTED] MarketId={book.MarketId} Reason={risk}");
                Console.WriteLine($"[SINGLE_MARKET_PRETRADE_REJECTED] MarketId={book.MarketId} Reason={risk}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,risk,null);
            }

            LogExecutionDecision(book.MarketId, edge, quantity, quantity * adjustedCost, expected, "Pretrade", "Ok", $"single-market:{book.MarketId}");
            Console.WriteLine($"[PAPER_PRETRADE_APPROVED] MarketId={book.MarketId} PositionKey=single-market:{book.MarketId} Qty={quantity:0.####} Notional={quantity * adjustedCost:0.####}");
            Console.WriteLine($"[SINGLE_MARKET_DRY_RUN_ORDER_PLAN_CREATED] MarketId={book.MarketId} PaperOnly={_options.PaperOnly.ToString().ToLowerInvariant()} Qty={quantity:0.####}");
            RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.DryRunPlanCreated, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "Pending", "NotOpened", null, st.EdgeScans, st.ExecutionScans), "SingleMarketDryRunOrderPlanCreated");

            var fill = _fillSimulator.Simulate(book, quantity, _feeBuffer, _slippageBuffer);
            if (!fill.Passed || fill.AdjustedEdgePerShare < _minEdgePerShare || fill.ExpectedProfit < _options.MinExpectedProfit)
            {
                var reason = !fill.Passed ? fill.Reason : "FillAdjustedProfitBelowThreshold";
                LogExecutionDecision(book.MarketId, edge, quantity, fill.SimulatedCost, fill.ExpectedProfit, "RejectFill", reason, $"single-market:{book.MarketId}");
                diagnostics.AddReject(reason);
                diagnostics.IncrementRejectedByFill();
                diagnostics.RecordAuditCandidate(AuditNearMiss(book, market.conditionId, yes, no, rawCost, validRawEdge, edge, fill.AdjustedEdgePerShare, quantityAvailable, quantity, fill.SimulatedCost, reason, null, false, true, true, false));
                RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.Rejected, yes, no, rawCost, fill.AdjustedEdgePerShare, fill.ExpectedProfit, quantity, fill.SimulatedCost, "Passed", "Rejected", "Rejected", reason, st.EdgeScans, st.ExecutionScans), "SingleMarketFillRejected");
                Console.WriteLine($"[PAPER_FILL_SIMULATION_FAILED] MarketId={book.MarketId} Reason={reason} PlannedQty={quantity:0.####} FullyFillableQty={fill.FullyFillableQty:0.####}");
                Console.WriteLine($"[SINGLE_MARKET_FILL_REJECTED] MarketId={book.MarketId} Reason={reason} PlannedQty={quantity:0.####} FullyFillableQty={fill.FullyFillableQty:0.####}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,reason,null);
            }
            diagnostics.IncrementFillPassed();
            Console.WriteLine($"[PAPER_FILL_SIMULATION_PASSED] MarketId={book.MarketId} Qty={quantity:0.####} FullyFillableQty={fill.FullyFillableQty:0.####} SimulatedCost={fill.SimulatedCost:0.####}");
            Console.WriteLine($"[SINGLE_MARKET_FILL_SIMULATION_PASSED] MarketId={book.MarketId} Qty={quantity:0.####} FullyFillableQty={fill.FullyFillableQty:0.####} SimulatedCost={fill.SimulatedCost:0.####}");
            RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.FillSimulationPassed, yes, no, rawCost, fill.AdjustedEdgePerShare, fill.ExpectedProfit, quantity, fill.SimulatedCost, "Passed", "Passed", "NotOpened", null, st.EdgeScans, st.ExecutionScans), "SingleMarketFillSimulationPassed");

            if (_orderBooks is OrderBookService orderBookServiceForPaper && orderBookServiceForPaper.GetStats().OrderbookCircuitBreakerActive)
            {
                diagnostics.AddReject("OrderbookCircuitBreakerActive");
                Console.WriteLine($"[SINGLE_MARKET_PAPER_OPEN_BLOCKED] MarketId={book.MarketId} Reason=OrderbookCircuitBreakerActive");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,"OrderbookCircuitBreakerActive",null);
            }
            var blockedByDiscoveryMode = _state is not null && (_state.PaperExecutionGloballyBlockedByDiscovery || _state.DiscoveryReducedUniverse || !_state.DiscoveryHealthy || !_state.DiscoveryScannerSafeSourceAvailable || _state.DiscoverySelectedSource.Equals("Blocked", StringComparison.OrdinalIgnoreCase) || _state.DiscoverySelectedSource.Equals("ReducedUniverseDiagnosticsOnly", StringComparison.OrdinalIgnoreCase));
            var limitedGate = EvaluatePaperDiagnosticsLimitedGate(fill.SimulatedCost);
            if (blockedByDiscoveryMode && !limitedGate.Allowed)
            {
                diagnostics.AddReject(limitedGate.CounterReason);
                diagnostics.IncrementRejectedByPaperDiagnosticsLimitedGate();
                diagnostics.RecordAuditCandidate(AuditNearMiss(book, market.conditionId, yes, no, rawCost, validRawEdge, edge, fill.AdjustedEdgePerShare, quantityAvailable, quantity, fill.SimulatedCost, limitedGate.CounterReason, null, true, true, true, false));
                _state?.RecordPaperPretradeReject(limitedGate.CounterReason);
                var shouldLogPaperBlock = _quietLogGate?.ShouldLog(
                    new LogEventKey("single-market", "PAPER_BLOCKED_BY_DISCOVERY_MODE", MarketId: book.MarketId, Strategy: StrategyName),
                    new LogEventFingerprint($"{StrategyName}|{_state?.DiscoverySelectedSource ?? "Unknown"}|{_state?.DiscoveryReducedUniverse}", "PaperBlockedByDiscoveryMode"),
                    LogImportance.Important,
                    QuietPolicy(Math.Max(1, _logging.QuietModeDefaultEveryNCycles), Math.Max(1, _logging.MaxVerifiedPretradeBlockedAuditPerHour))) ?? true;
                if (shouldLogPaperBlock) Console.WriteLine($"[PAPER_DIAGNOSTICS_LIMITED_BLOCKED] Strategy=SingleMarketBuyBoth MarketId={book.MarketId} Reason={limitedGate.Reason}");
                Console.WriteLine($"[SINGLE_MARKET_PAPER_OPEN_BLOCKED] MarketId={book.MarketId} Reason={limitedGate.CounterReason}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,limitedGate.CounterReason,null);
            }

            if (!paper.TryMarkSingleMarketOpenInFlight(book.MarketId, StrategyName, out var ttlSeconds))
            {
                return SuppressExecution(book, market.conditionId, diagnostics, adjustedCost, yes, no, rawCost, edge, expected, quantity, st.EdgeScans, st.ExecutionScans, "InFlightDuplicate");
            }
            Console.WriteLine($"[PAPER_OPEN_IN_FLIGHT] MarketId={book.MarketId} PositionKey=single-market:{book.MarketId} TtlSeconds={ttlSeconds}");
            _state?.SetPaperInFlightOpens(paper.PaperInFlightOpenCount);
            var opportunity = new ArbOpportunity(new ArbLeg(book.MarketId, book.Question, "YES", fill.YesAveragePrice, book.YesAsk.Size), new ArbLeg(book.MarketId, book.Question, "NO", fill.NoAveragePrice, book.NoAsk.Size), quantity, fill.SimulatedCost / quantity, fill.AdjustedEdgePerShare, fill.ExpectedProfit, 1.0, "SingleMarketBuyBoth", StrategyName);
            var candidateId = $"SingleMarketBuyBoth:{book.MarketId}:{_scanId}";
            if (_realWatch is not null && _botOptions is not null && (_botOptions.RuntimeProfile.Equals(RuntimeProfileService.ReducedDiagnosticsPaperPhase1, StringComparison.OrdinalIgnoreCase) || _botOptions.RuntimeProfile.Equals(RuntimeProfileService.ReducedDiagnosticsPaperPhase1Canary, StringComparison.OrdinalIgnoreCase)) && !_realWatch.AllowRealOpen(candidateId, book.MarketId, fill.AdjustedEdgePerShare, out var watchReason))
            {
                paper.ClearSingleMarketOpenInFlight(book.MarketId);
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,watchReason,null);
            }
            var equityBefore = paper.Equity;
            var executed = _executionQueue == null
                ? paper.RecordArbitrage(opportunity)
                : await _executionQueue.EnqueueAsync(new OpportunityExecutionCandidate(
                    "SingleMarketBuyBoth",
                    _strategyMode,
                    $"single-market:{book.MarketId}",
                    fill.SimulatedCost,
                    _ => Task.FromResult(paper.RecordArbitrage(opportunity))), ct);
            if (!executed)
            {
                _realWatch?.RecordOpenResult(candidateId, book.MarketId, fill.AdjustedEdgePerShare, fill.SimulatedCost, fill.ExpectedProfit, false);
                paper.ClearSingleMarketOpenInFlight(book.MarketId);
                _state?.SetPaperInFlightOpens(paper.PaperInFlightOpenCount);
                diagnostics.AddReject("PaperOpenBlocked");
                Console.WriteLine($"[PAPER_OPEN_FAILED] MarketId={book.MarketId} Reason=PaperRejected PositionKey=single-market:{book.MarketId}");
                Console.WriteLine($"[SINGLE_MARKET_PAPER_OPEN_BLOCKED] MarketId={book.MarketId} Reason=PaperRejected");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,"PaperRejected",null);
            }
            _realWatch?.RecordOpenResult(candidateId, book.MarketId, fill.AdjustedEdgePerShare, fill.SimulatedCost, fill.ExpectedProfit, true);
            paper.ClearSingleMarketOpenInFlight(book.MarketId);
            _state?.SetPaperInFlightOpens(paper.PaperInFlightOpenCount);
            diagnostics.IncrementPaperOpened();
            RecordOpened(book.MarketId);
            var execution = new SingleMarketPaperExecutionDto(PaperPhase1RealWatchService.ExecutionId(candidateId, book.MarketId), DateTime.UtcNow, book.MarketId, book.Question, StrategyName, quantity, fill.YesAveragePrice, fill.NoAveragePrice, fill.SimulatedCost, fill.AdjustedEdgePerShare, fill.ExpectedProfit, paper.Balance, paper.LockedCapital, paper.Equity, "Opened", true);
            _state?.AddSingleMarketExecution(execution);
            diagnostics.AddExecution(execution);
            LogExecutionDecision(book.MarketId, fill.AdjustedEdgePerShare, quantity, fill.SimulatedCost, fill.ExpectedProfit, "OpenPaper", "Ok", $"single-market:{book.MarketId}");
            RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.PaperOpened, yes, no, rawCost, fill.AdjustedEdgePerShare, fill.ExpectedProfit, quantity, fill.SimulatedCost, "Passed", "Passed", $"Opened EquityUnchanged={paper.Equity == equityBefore}", null, st.EdgeScans, st.ExecutionScans), "SingleMarketPaperOpened");
            diagnostics.RecordAuditCandidate(AuditNearMiss(book, market.conditionId, yes, no, rawCost, validRawEdge, edge, fill.AdjustedEdgePerShare, quantityAvailable, quantity, fill.SimulatedCost, "None", null, true, true, true, true));
            return new SingleMarketScanResult(true,true,true,true,adjustedCost,book.Question,edge,null,null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SINGLE_MARKET_ERROR] {ex.Message}");
            return SingleMarketScanResult.Empty;
        }
        finally { semaphore.Release(); }
    }

    private (bool Allowed, string Reason, string CounterReason) EvaluatePaperDiagnosticsLimitedGate(decimal notional)
    {
        if (_state is null || _botOptions is null || !_botOptions.PaperDiagnosticsLimited.Enabled)
            return (false, "PaperDiagnosticsLimitedDisabled", "PaperBlockedByDiscoveryMode");

        var cfg = _botOptions.PaperDiagnosticsLimited;
        var stats = _state.OrderBookServiceStats;
        var reasons = new List<string>();
        if (cfg.RequireExplicitFlag && !cfg.Enabled) reasons.Add("ExplicitFlagNotSatisfied");
        if (!string.Equals(cfg.AllowedStrategy, "SingleMarketBuyBoth", StringComparison.OrdinalIgnoreCase)) reasons.Add("StrategyNotAllowed");
        if (cfg.RequireReducedUniverse)
        {
            if (!string.Equals(_state.DiscoverySelectedSource, "ReducedUniverseDiagnosticsOnly", StringComparison.OrdinalIgnoreCase)) reasons.Add("DiscoveryModeNotReducedUniverseDiagnosticsOnly");
            if (!_state.DiscoveryReducedUniverse) reasons.Add("DiscoveryReducedUniverseFalse");
            if (!string.Equals(_state.DiagnosticsUniverse, "Reduced", StringComparison.OrdinalIgnoreCase)) reasons.Add("DiagnosticsUniverseNotReduced");
            if (_state.ReducedUniverseMarkets <= 0) reasons.Add("ReducedUniverseMarketsEmpty");
        }
        if (cfg.RequirePaperDiagnosticsLimitedEligible)
        {
            var eligibleReason = PaperDiagnosticsLimitedEligibilityReason(_state);
            if (!string.Equals(eligibleReason, "None", StringComparison.OrdinalIgnoreCase)) reasons.Add(eligibleReason);
        }
        if (cfg.RequireOrderbookStableNow && !OrderbookStableNow(_state)) reasons.Add("OrderbookStableNowFalse");
        if (cfg.RequireNoCircuitBreaker && (stats.OrderbookCircuitBreakerActive || !string.Equals(stats.OrderbookCircuitBreakerState, "Closed", StringComparison.OrdinalIgnoreCase))) reasons.Add("OrderbookCircuitBreakerNotClosed");
        if (cfg.RequireNoBadRequestDeltas && (stats.BatchBadRequests > 0 || stats.BatchInvalidTokens > 0 || stats.TruePostBreakerBadRequests > 0)) reasons.Add("BadRequestDeltasNonZero");
        if (_state.DiagnosticsCounterMismatchCount > 0) reasons.Add("DiagnosticsCounterMismatch");
        if (_state.MemoryCriticals > 0 || _state.ScannerPausedByMemoryGuard) reasons.Add("MemoryUnstable");
        if (cfg.RequireLiveTradingFalse && _botOptions.TradingMode.LiveTradingEnabled) reasons.Add("LiveTradingEnabled");
        if (cfg.RequireNoSigningAttempts && LiveTradingGuard.SigningAttempts > 0) reasons.Add("SigningAttemptDetected");
        if (_state.PaperOpenPositions >= cfg.MaxOpenPositions) reasons.Add("MaxOpenPositions");
        if (_state.PaperTotalExposure + notional > cfg.MaxPaperTotalExposure) reasons.Add("MaxPaperTotalExposure");
        if (notional > cfg.MaxPaperNotionalPerTrade) reasons.Add("MaxPaperNotionalPerTrade");
        if (_state.PaperOpenCountLastHour >= cfg.MaxPaperOpensPerHour) reasons.Add("MaxPaperOpensPerHour");

        var reason = reasons.Count == 0 ? "None" : string.Join("|", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
        return (reasons.Count == 0, reason, reasons.Count == 0 ? "None" : "PaperBlockedByDiagnosticsLimitedGate");
    }

    private static bool OrderbookStableNow(BotRuntimeState state)
    {
        var stats = state.OrderBookServiceStats;
        return string.Equals(stats.OrderbookCircuitBreakerState, "Closed", StringComparison.OrdinalIgnoreCase)
            && !stats.OrderbookCircuitBreakerActive
            && !stats.ReducedUniverseOrderbookRecoveryMode
            && !stats.ReducedUniverseScanPausedByOrderbookHealth
            && stats.TruePostBreakerBadRequests == 0
            && stats.MarketOrderbookQuarantineActive == 0
            && stats.InvalidTokenQuarantineActive == 0;
    }

    private static string PaperDiagnosticsLimitedEligibilityReason(BotRuntimeState state)
    {
        var reasons = new List<string>();
        if (!state.ReducedUniverseExplicitFlagSatisfied) reasons.Add("NotExplicitlyEnabled");
        if (!state.DiscoveryReducedUniverse) reasons.Add("ReducedUniverseNotActive");
        if (!string.Equals(state.DiagnosticsUniverse, "Reduced", StringComparison.OrdinalIgnoreCase)) reasons.Add("ReducedUniverseNotActive");
        if (!OrderbookStableNow(state)) reasons.Add("OrderbookStableNowFalse");
        if (state.DiagnosticsCounterMismatchCount > 0) reasons.Add("DiagnosticsCounterMismatch");
        if (state.LiveTradingBlockedCount > 0) reasons.Add("LiveTradingSafety");
        if (LiveTradingGuard.SigningAttempts > 0) reasons.Add("SigningAttemptDetected");
        return reasons.Count == 0 ? "None" : string.Join("|", reasons.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private void RecordMonitor(BinaryOrderBookSnapshot book, decimal edge, decimal adjustedCost, decimal quantity)
    {
        if (ShouldEmitFullArbAlert(book.MarketId, edge)) _monitor?.Record(new ArbMonitorRecord(DateTime.UtcNow, "SingleMarketBuyBoth", StrategyName, book.MarketId, edge, adjustedCost, 1m, quantity, quantity * edge, false, $"BUY YES @ {book.YesAsk!.Price} | {book.Question}", $"BUY NO @ {book.NoAsk!.Price} | {book.Question}", null)
        { OrderLegs = new List<OrderLegCandidate> { new(StrategyName, book.MarketId, book.Question, book.YesTokenId, "YES", LiveOrderSide.BUY, book.YesAsk.Price, quantity, edge), new(StrategyName, book.MarketId, book.Question, book.NoTokenId, "NO", LiveOrderSide.BUY, book.NoAsk.Price, quantity, edge) } });
    }

    private string CheckExecutionReadiness(BinaryOrderBookSnapshot book, decimal qty, decimal expected, decimal notional)
    {
        if (book.YesAsk is null) return "MissingYesAsk";
        if (book.NoAsk is null) return "MissingNoAsk";
        if (qty <= 0m) return "InsufficientLiquidity";
        if (notional < _options.MinNotional) return "BelowMinNotional";
        if (expected < _options.MinExpectedProfit) return "BelowMinExpectedProfit";
        if (notional > _options.MaxNotionalPerTrade) return "MaxNotionalPerTradeExceeded";
        return "Ok";
    }

    private string CheckCooldown(string marketId)
    {
        lock (_gate)
        {
            if (_cooldownUntil.TryGetValue(marketId, out var until) && DateTime.UtcNow < until) return "CooldownActive";
        }
        return "Ok";
    }

    private bool TryReserveCycleSlot()
    {
        while (true)
        {
            var cur = Volatile.Read(ref _positionsOpenedThisCycle);
            if (cur >= _options.MaxPositionsPerCycle) return false;
            if (Interlocked.CompareExchange(ref _positionsOpenedThisCycle, cur + 1, cur) == cur) return true;
        }
    }

    private string CheckRisk(PaperTradingEngine paper, decimal notional)
    {
        if (Volatile.Read(ref _positionsOpenedThisCycle) >= _options.MaxPositionsPerCycle) return "MaxPositionsPerCycleReached";
        if (notional > _options.MaxNotionalPerTrade) return "MaxNotionalPerTradeExceeded";
        if (paper.Balance < notional) return "InsufficientPaperCash";
        if (paper.CountOpenSingleMarketPositions() >= _options.MaxOpenSingleMarketPositions) return "MaxOpenPositionsExceeded";
        if (paper.GetOpenSingleMarketExposure() + notional > _options.MaxTotalSingleMarketExposure) return "MaxExposureExceeded";
        return "Ok";
    }

    private SingleMarketScanSummaryDto BuildAndPublishSnapshot(SingleMarketCycleDiagnostics diagnostics)
    {
        var rejectCounts = diagnostics.RejectCounts.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        var dataQualityCounts = diagnostics.DataQualityCounts.ToDictionary(k => k.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
        var topReject = rejectCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var topNearMisses = diagnostics.NearMisses
            .Where(x => x.EdgePerShare >= _options.NearMissMinEdge && x.EdgePerShare < _minEdgePerShare)
            .OrderByDescending(x => x.EdgePerShare)
            .Take(_options.TopNearMissCount)
            .ToArray();
        var positive = diagnostics.PositiveCandidates.OrderByDescending(x => x.EdgePerShare).Take(_options.TopPositiveCandidateCount).ToArray();
        var dqSamples = diagnostics.DataQualitySamples.Take(_options.TopDataQualityRejectSampleCount).ToArray();
        var executions = _state?.SingleMarketExecutions().TakeLast(_options.TopExecutionCount).ToArray() ?? diagnostics.Executions.Take(_options.TopExecutionCount).ToArray();
        var auditNearMisses = diagnostics.AuditNearMisses.OrderByDescending(x => x.AfterSafetyEdge).Take(50).ToArray();
        var edgeDistribution = diagnostics.BuildEdgeDistribution();
        var summary = new SingleMarketScanSummaryDto(
            DateTime.UtcNow,
            diagnostics.ScanId,
            diagnostics.Scanned,
            diagnostics.BookOk,
            diagnostics.BothAsks,
            diagnostics.DataQualityRejected,
            diagnostics.BelowMinEdge,
            diagnostics.PositiveEdge,
            diagnostics.EdgeStable,
            diagnostics.ExecutionReady,
            diagnostics.FillPassed,
            diagnostics.PaperOpened,
            diagnostics.BestEdgeSeen,
            diagnostics.BestRejectedRawEdge,
            topReject.Key ?? "None",
            topReject.Value,
            rejectCounts,
            dataQualityCounts,
            diagnostics.DataQualityRejectedRawPositive,
            diagnostics.ValidRawPositive,
            diagnostics.ValidAfterCostPositive,
            diagnostics.ValidAfterSafetyPositive,
            diagnostics.RejectedByFill,
            diagnostics.RejectedByDepth,
            diagnostics.RejectedByRisk,
            diagnostics.RejectedByPaperDiagnosticsLimitedGate,
            diagnostics.BestRawEdge,
            diagnostics.BestAfterCostEdge,
            diagnostics.BestAfterSafetyEdge,
            diagnostics.BestExecutableEdge,
            auditNearMisses.FirstOrDefault()?.RejectedReason ?? "None",
            edgeDistribution);
        var snapshot = new SingleMarketArbSnapshotDto(DateTime.UtcNow, diagnostics.ScanId, summary, positive, topNearMisses, auditNearMisses, dqSamples, executions);
        _state?.SetSingleMarketSnapshot(snapshot);
        return summary;
    }

    private void MaybeLogSummaries(SingleMarketScanSummaryDto summary, SingleMarketCycleDiagnostics diagnostics, bool suppressDataQualitySummary = false)
    {
        if (!suppressDataQualitySummary)
        {
            var summaryHash = $"{Bucket(summary.Scanned, 25)}|{summary.PositiveEdge}|{summary.TopRejectReason}|{summary.PaperOpened}|{Bucket(summary.BestEdgeSeen ?? 0m, 0.001m)}";
            if (ShouldLog(ref _lastSummaryHash, summaryHash, summary.ScanId, _logging.LogSingleMarketSummaryEveryNCycles, _logging.LogSingleMarketSummaryOnChangeOnly))
            {
                var bestEdgeText = summary.BestEdgeSeen.HasValue ? summary.BestEdgeSeen.Value.ToString("0.####") : "N/A";
                var bestRejectedText = summary.BestRejectedRawEdge.HasValue ? summary.BestRejectedRawEdge.Value.ToString("0.####") : "N/A";
                Console.WriteLine($"[SINGLE_MARKET_SCAN_SUMMARY] Scanned={summary.Scanned} DataQualityRejected={summary.DataQualityRejected} BelowMinEdge={summary.BelowMinEdge} PositiveEdge={summary.PositiveEdge} EdgeStable={summary.EdgeStable} ExecutionReady={summary.ExecutionReady} FillPassed={summary.FillPassed} PaperOpened={summary.PaperOpened} TopReject={summary.TopRejectReason}:{summary.TopRejectCount} BestEdge={bestEdgeText} BestRejectedRawEdge={bestRejectedText}");
            }
        }

        if (!suppressDataQualitySummary && ShouldLogDataQualitySummary(summary, diagnostics))
        {
            var counts = string.Join(" ", summary.DataQualityRejectedByReason.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}={x.Value}"));
            Console.WriteLine($"[SINGLE_MARKET_DATA_QUALITY_SUMMARY] TotalRejected={summary.DataQualityRejected} {counts} Samples={Math.Min(_options.TopDataQualityRejectSampleCount, diagnostics.DataQualitySamples.Count)}");
        }

        if (!_options.LogTopNearMissesToConsole) return;
        var topNearMisses = diagnostics.NearMisses.Where(x => x.EdgePerShare >= _options.NearMissMinEdge && x.EdgePerShare < _minEdgePerShare).OrderByDescending(x => x.EdgePerShare).Take(Math.Max(0, _options.ConsoleTopNearMissCount)).ToArray();
        var nearHash = string.Join("|", topNearMisses.Select(x => $"{x.MarketId}:{Bucket(x.EdgePerShare, 0.001m)}"));
        if (topNearMisses.Length > 0 && ShouldLog(ref _lastNearMissHash, nearHash, summary.ScanId, _logging.LogSingleMarketNearMissEveryNCycles, _logging.LogSingleMarketNearMissOnChangeOnly))
        {
            foreach (var n in topNearMisses)
                Console.WriteLine($"[SINGLE_MARKET_TOP_NEAR_MISS] MarketId={n.MarketId} Edge={n.EdgePerShare:0.####} RequiredImprovement={n.RequiredImprovement:0.####}");
        }
    }


    private bool ShouldLogDataQualitySummary(SingleMarketScanSummaryDto summary, SingleMarketCycleDiagnostics diagnostics)
    {
        if (summary.DataQualityRejected <= 0) return false;
        var criticalDataQuality = diagnostics.HighSeverityDataQuality > 0;
        var periodic = _logging.LogSingleMarketDataQualityEveryNCycles > 0
            && summary.ScanId % _logging.LogSingleMarketDataQualityEveryNCycles == 0;
        var shouldLog = criticalDataQuality
            || !_logging.LogSingleMarketDataQualityOnChangeOnly
            || !_hasLoggedDataQualitySummary
            || periodic
            || IsMaterialDataQualityChange(summary.DataQualityRejected, summary.DataQualityRejectedByReason);
        if (!shouldLog) return false;
        _hasLoggedDataQualitySummary = true;
        _lastLoggedDataQualityTotal = summary.DataQualityRejected;
        _lastLoggedDataQualityCounts = summary.DataQualityRejectedByReason.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        return true;
    }

    private bool IsMaterialDataQualityChange(int totalRejected, IReadOnlyDictionary<string, int> reasonCounts)
    {
        var delta = Math.Max(1, _logging.SingleMarketDataQualitySignificantDelta);
        if (!_hasLoggedDataQualitySummary) return true;
        if (Math.Abs(totalRejected - _lastLoggedDataQualityTotal) >= delta) return true;
        var previousTopReason = _lastLoggedDataQualityCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault().Key ?? string.Empty;
        var currentTopReason = reasonCounts.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase).FirstOrDefault().Key ?? string.Empty;
        if (!string.Equals(previousTopReason, currentTopReason, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var reason in reasonCounts.Keys.Concat(_lastLoggedDataQualityCounts.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var current = reasonCounts.TryGetValue(reason, out var c) ? c : 0;
            var previous = _lastLoggedDataQualityCounts.TryGetValue(reason, out var p) ? p : 0;
            if (Math.Abs(current - previous) >= delta) return true;
        }
        return false;
    }


    private void BeginDataQualityFullCycle(long cycleId)
    {
        lock (_gate)
        {
            if (_currentDataQualityFullCycleId == cycleId) return;
            _currentDataQualityFullCycleId = cycleId;
            _highSeverityDataQualityLogsThisCycle = 0;
            _highSeverityDataQualitySuppressedThisCycle = 0;
            _dataQualityAuditSamplesThisCycle = 0;
        }
    }

    private bool TryReserveHighSeverityDataQualityLog()
    {
        lock (_gate)
        {
            if (_highSeverityDataQualityLogsThisCycle < Math.Max(0, _options.MaxHighSeverityDataQualityLogsPerCycle))
            {
                _highSeverityDataQualityLogsThisCycle++;
                return true;
            }
            _highSeverityDataQualitySuppressedThisCycle++;
            return false;
        }
    }

    private bool TryReserveDataQualityAuditSample(int maxSamples)
    {
        lock (_gate)
        {
            if (_dataQualityAuditSamplesThisCycle >= Math.Max(0, maxSamples)) return false;
            _dataQualityAuditSamplesThisCycle++;
            return true;
        }
    }

    private void MaybeLogSuppressedHighSeverityDataQuality()
    {
        int suppressed;
        lock (_gate) suppressed = _highSeverityDataQualitySuppressedThisCycle;
        if (suppressed > 0)
            Console.WriteLine($"[SINGLE_MARKET_DATA_QUALITY_HIGH_SEVERITY_SUPPRESSED] Count={suppressed}");
    }

    private bool ShouldLog(ref string lastHash, string hash, long scanId, int everyNCycles, bool onChangeOnly)
    {
        var changed = !string.Equals(lastHash, hash, StringComparison.Ordinal);
        var periodic = everyNCycles > 0 && scanId % everyNCycles == 0;
        if (!onChangeOnly || changed || periodic)
        {
            lastHash = hash;
            return true;
        }
        return false;
    }

    private static int Bucket(int value, int size) => size <= 1 ? value : value / size * size;
    private static decimal Bucket(decimal value, decimal size) => size <= 0m ? value : Math.Round(value / size, 0, MidpointRounding.AwayFromZero) * size;

    private bool ShouldLogDataQualityReject(string marketId, string reason, decimal rawSum, DateTime nowUtc)
    {
        if (!IsHighSeverityDataQuality(reason, rawSum)) return false;
        if (_operationalQuietMode && IsHighSeverityDataQualityInCooldown(marketId, reason, nowUtc))
        {
            IncrementSuppressedHighSeverityDataQuality();
            return false;
        }
        if (_operationalQuietMode && !TryReserveHighSeverityDataQualityLog()) return false;

        var shouldLog = _quietLogGate?.ShouldLog(
            new LogEventKey("single-market", "SINGLE_MARKET_DATA_QUALITY_REJECTED", MarketId: marketId),
            new LogEventFingerprint($"{marketId}|{reason}", reason),
            LogImportance.Important,
            QuietPolicy(Math.Max(1, _logging.LogSingleMarketDataQualityEveryNCycles), Math.Max(1, _options.MaxRepeatedSameMarketDataQualityLogsPerHour))) ?? true;
        if (!shouldLog)
        {
            IncrementSuppressedHighSeverityDataQuality();
            return false;
        }

        MarkHighSeverityDataQualityLogged(marketId, reason, nowUtc);
        return true;
    }

    private QuietLogPolicy QuietPolicy(int everyNCycles, int maxPerHour)
        => new(
            _operationalQuietMode,
            everyNCycles,
            everyNCycles == 0 ? 0 : Math.Max(1, _logging.QuietModeDefaultEveryMinutes),
            _logging.QuietModeSuppressRepeatedHash,
            maxPerHour,
            !_operationalQuietMode);

    private bool IsHighSeverityDataQualityInCooldown(string marketId, string reason, DateTime nowUtc)
    {
        var cooldown = TimeSpan.FromMinutes(Math.Max(0, _options.HighSeverityDataQualityCooldownMinutes));
        if (cooldown <= TimeSpan.Zero) return false;
        var key = $"{marketId}|{reason}";
        lock (_gate)
            return _highSeverityDataQualityLastLoggedAt.TryGetValue(key, out var last) && nowUtc - last < cooldown;
    }

    private void MarkHighSeverityDataQualityLogged(string marketId, string reason, DateTime nowUtc)
    {
        var cooldown = TimeSpan.FromMinutes(Math.Max(0, _options.HighSeverityDataQualityCooldownMinutes));
        if (cooldown <= TimeSpan.Zero) return;
        var key = $"{marketId}|{reason}";
        lock (_gate)
        {
            _highSeverityDataQualityLastLoggedAt[key] = nowUtc;
            var cutoff = nowUtc - cooldown;
            foreach (var stale in _highSeverityDataQualityLastLoggedAt.Where(x => x.Value < cutoff).Select(x => x.Key).ToArray())
                _highSeverityDataQualityLastLoggedAt.Remove(stale);
        }
    }

    private void IncrementSuppressedHighSeverityDataQuality()
    {
        lock (_gate) _highSeverityDataQualitySuppressedThisCycle++;
    }

    private bool IsHighSeverityDataQuality(string reason, decimal rawSum)
    {
        if (reason is "SameYesNoTokenId" or "MarketIdMismatch") return true;
        if (reason != "SuspiciousYesNoAskSum") return false;
        var distance = rawSum < _options.MinReasonableYesNoAskSum
            ? _options.MinReasonableYesNoAskSum - rawSum
            : rawSum > _options.MaxReasonableYesNoAskSum
                ? rawSum - _options.MaxReasonableYesNoAskSum
                : 0m;
        return distance >= _options.HighSeveritySuspiciousAskSumDistance;
    }

    private void RecordHighValue(SingleMarketArbOpportunityDto dto, string auditStage)
    {
        MaybeStoreOpportunity(dto, highValue: true);
        MaybeAudit(dto, auditStage, highValue: true, sampled: true);
    }

    private void MaybeStoreOpportunity(SingleMarketArbOpportunityDto dto, bool highValue)
    {
        if (highValue) _state?.AddSingleMarketOpportunity(dto);
    }

    private void MaybeAuditDataQuality(SingleMarketArbOpportunityDto dto, string reason, decimal rawSum, decimal edge, SingleMarketCycleDiagnostics diagnostics, bool suppressHighSeverityForCooldown = false)
    {
        if (suppressHighSeverityForCooldown) return;
        var firstReason = diagnostics.MarkDataQualityAuditReason(reason);
        var highSeverity = IsHighSeverityDataQuality(reason, rawSum);
        var positiveRejected = edge >= _minEdgePerShare && reason is not ("MissingYesAsk" or "MissingNoAsk");
        var enabledSample = _options.AuditDataQualityRejectedEvents && firstReason;
        var highSeveritySample = _options.AuditHighSeverityDataQualityRejectedEvents && highSeverity;
        var positiveSample = positiveRejected && firstReason;
        if (!(enabledSample || highSeveritySample || positiveSample)) return;
        if (!TryReserveDataQualityAuditSample(_options.MaxDataQualityAuditSamplesPerCycle)) return;
        if (highSeverity && reason == "SuspiciousYesNoAskSum" && !_highSeverityDataQualityAuditHourlyCap.TryReserve(_options.MaxHighSeverityDataQualityAuditLogsPerHour, DateTime.UtcNow, out var capLogDue, out var cappedCount))
        {
            if (capLogDue) Console.WriteLine($"[SINGLE_MARKET_DATA_QUALITY_AUDIT_HOURLY_CAP_REACHED] Count={cappedCount}");
            return;
        }
        MaybeAudit(dto, "SingleMarketDataQualityRejected", highValue: highSeverity || positiveRejected, sampled: true);
    }

    private void MaybeAudit(SingleMarketArbOpportunityDto dto, string auditStage, bool highValue, bool sampled)
    {
        if (!highValue && !sampled) return;
        if (auditStage == "SingleMarketDataQualityRejected")
        {
            var shouldAudit = _quietLogGate?.ShouldLog(
                new LogEventKey("execution-audit", "EXECUTION_AUDIT", MarketId: dto.MarketId, Strategy: dto.Strategy),
                new LogEventFingerprint($"{dto.MarketId}|{dto.Reason ?? "Ok"}", dto.Reason ?? "Ok"),
                LogImportance.Normal,
                QuietPolicy(Math.Max(1, _logging.QuietModeDefaultEveryNCycles), Math.Max(1, _logging.MaxDataQualityAuditLogsPerHour))) ?? true;
            if (!shouldAudit) return;
        }
        _audit?.Audit(new ExecutionAuditEvent(DateTime.UtcNow, dto.Id, dto.MarketId, dto.Strategy, auditStage, dto.State.ToString(), dto.Reason ?? "Ok", dto.EdgePerShare, dto.ExpectedProfit, dto.PlannedNotional, dto.Quantity, $"PaperOnly={dto.PaperOnly}; DataQuality={dto.DataQualityStatus}; Fill={dto.FillSimulationStatus}; Paper={dto.PaperStatus}"));
        if (highValue || !_operationalQuietMode)
            Console.WriteLine($"[EXECUTION_AUDIT] Stage={auditStage} MarketId={dto.MarketId} State={dto.State} Reason={dto.Reason ?? "Ok"}");
    }

    private void ExportLatest()
    {
        if (_state is null || string.IsNullOrWhiteSpace(_contentRootPath)) return;
        var dir = Path.Combine(_contentRootPath, "exports");
        Directory.CreateDirectory(dir);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(dir, "single-market-arb-opportunities-latest.json"), JsonSerializer.Serialize(_state.SingleMarketSnapshot, jsonOptions));
        File.WriteAllText(Path.Combine(dir, "single-market-paper-executions-latest.json"), JsonSerializer.Serialize(_state.SingleMarketExecutions().TakeLast(100), jsonOptions));
        File.WriteAllText(Path.Combine(dir, "single-market-near-misses-latest.json"), JsonSerializer.Serialize(_state.SingleMarketSnapshot.TopOpportunityAuditNearMisses.Take(50), jsonOptions));
        ExportEdgeDistributionLatest(dir, jsonOptions);
    }


    private SingleMarketScanResult SuppressExecution(BinaryOrderBookSnapshot book, string? conditionId, SingleMarketCycleDiagnostics diagnostics, decimal? adjustedCost, decimal yes, decimal no, decimal rawCost, decimal edge, decimal expected, decimal quantity, int edgeScans, int executionScans, string reason)
    {
        if (!reason.Equals("InFlightDuplicate", StringComparison.OrdinalIgnoreCase)) RecordSuppression(book.MarketId);
        diagnostics.AddReject(reason);
        if (reason.Contains("Duplicate", StringComparison.OrdinalIgnoreCase)) _state?.RecordPaperDuplicateSuppression();
        LogOpportunityState(book.MarketId, "SuppressedDuplicate", edge, edgeScans);
        LogExecutionDecision(book.MarketId, edge, quantity, quantity * (adjustedCost ?? rawCost), expected, "SuppressDuplicate", reason, $"single-market:{book.MarketId}");
        RecordHighValue(BuildDto(book, conditionId, SingleMarketArbState.SuppressedDuplicate, yes, no, rawCost, edge, expected, quantity, quantity * (adjustedCost ?? rawCost), "Passed", "NotRun", "Suppressed", reason, edgeScans, executionScans), "SingleMarketDuplicateSuppressed");
        Console.WriteLine($"[PAPER_EXECUTION_SUPPRESSED] MarketId={book.MarketId} Reason={reason}");
        Console.WriteLine($"[SINGLE_MARKET_EXECUTION_SUPPRESSED] MarketId={book.MarketId} Reason={reason}");
        return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,reason,null);
    }

    private void LogDuplicateDiagnostics(PaperDuplicateSuppressionDiagnostics d)
    {
        var age = d.DedupeEntryAge.HasValue ? $"{d.DedupeEntryAge.Value.TotalSeconds:0}s" : "None";
        Console.WriteLine($"[PAPER_DUPLICATE_SUPPRESSION_DIAG] MarketId={d.MarketId} Strategy=SingleMarketBuyBoth PositionKey={d.PositionKey} DedupeKey={d.DedupeKey} ExistingPositionFound={d.ExistingPositionFound.ToString().ToLowerInvariant()} ExistingPositionId={d.ExistingPositionId} ExistingPositionStatus={d.ExistingPositionStatus} PaperPortfolioOpenCount={d.PaperPortfolioOpenCount} PaperOpenPositionsForMarket={d.PaperOpenPositionsForMarket} PaperOpenPositionsForStrategy={d.PaperOpenPositionsForStrategy} PaperTotalExposure={d.PaperTotalExposure:0.####} DedupeRegistryContains={d.DedupeRegistryContains.ToString().ToLowerInvariant()} DedupeEntryAge={age} DedupeSource={d.DedupeSource} Action={d.Action}");
        if (!d.ExistingPositionFound && !d.InFlightFound && d.PaperPortfolioOpenCount == 0 && d.Action == "Suppress")
            Console.WriteLine($"[PAPER_DUPLICATE_STATE_INCONSISTENCY] MarketId={d.MarketId} Reason=DuplicateOpenPositionWithoutOpenPaperPosition");
    }

    private void LogExecutionDecision(string marketId, decimal edge, decimal qty, decimal notional, decimal expectedProfit, string decision, string reason, string positionKey)
    {
        Console.WriteLine($"[SINGLE_MARKET_EXECUTION_DECISION] MarketId={marketId} Edge={edge:0.####} Qty={qty:0.####} Notional={notional:0.####} ExpectedProfit={expectedProfit:0.####} Decision={decision} Reason={reason} PositionKey={positionKey} PaperPhase=2 PaperTradingEnabled=true LiveTrading=false");
    }

    private void LogOpportunityState(string marketId, string state, decimal edge, int consecutive)
    {
        if (!_operationalQuietMode) return;
        lock (_gate)
        {
            if (_opportunityLogStates.TryGetValue(marketId, out var previous)
                && previous.State == state
                && Math.Abs(previous.Edge - edge) < _options.AlertMaterialEdgeChange) return;
            _opportunityLogStates[marketId] = new SingleMarketOpportunityLogState(state, edge, DateTime.UtcNow);
        }
        Console.WriteLine($"[SINGLE_MARKET_OPPORTUNITY_STATE] MarketId={marketId} State={state} Edge={edge:0.####} Consecutive={consecutive}");
    }

    private bool ShouldEmitFullArbAlert(string marketId, decimal edge)
    {
        if (!_operationalQuietMode || !_options.SuppressDuplicateArbAlertInQuietMode) return true;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (!_opportunityLogStates.TryGetValue(marketId, out var state))
            {
                _opportunityLogStates[marketId] = new SingleMarketOpportunityLogState("Detected", edge, now);
                return true;
            }
            if (now - state.LastFullAlertAt >= TimeSpan.FromMinutes(Math.Max(1, _options.AlertRepeatCooldownMinutes)))
            {
                _opportunityLogStates[marketId] = state with { Edge = edge, LastFullAlertAt = now };
                return true;
            }
            return false;
        }
    }

    private void ExportEdgeDistributionLatest(string dir, JsonSerializerOptions jsonOptions)
    {
        if (_state is null) return;
        var summary = _state.SingleMarketSnapshot.Summary;
        var payload = new
        {
            generatedAtUtc = DateTime.UtcNow,
            processRunId = _state.ProcessRunId,
            uptime = DateTime.UtcNow - _state.StartedAtUtc,
            validEdgeSamples = summary.EdgeDistribution?.ValidEdgeSamples ?? 0,
            rawEdge = summary.EdgeDistribution?.RawEdge ?? new SingleMarketEdgeQuantilesDto(),
            afterCostEdge = summary.EdgeDistribution?.AfterCostEdge ?? new SingleMarketEdgeQuantilesDto(),
            afterSafetyEdge = summary.EdgeDistribution?.AfterSafetyEdge ?? new SingleMarketEdgeQuantilesDto(),
            thresholdBuckets = summary.EdgeDistribution?.ThresholdBuckets ?? new SingleMarketAfterSafetyEdgeBucketsDto(),
            bestRawEdge = summary.BestRawEdge,
            bestAfterCostEdge = summary.BestAfterCostEdge,
            bestAfterSafetyEdge = summary.BestAfterSafetyEdge,
            positiveBeforeCost = summary.ValidRawPositive,
            positiveAfterCost = summary.ValidAfterCostPositive,
            positiveAfterSafety = summary.ValidAfterSafetyPositive,
            executionReady = summary.ExecutionReady,
            paperDiagnosticsLimitedEligible = RuntimeHealthSnapshot.From(_state, _botOptions).PaperDiagnosticsLimitedEligible,
            paperOpened = summary.PaperOpened
        };
        WriteJsonAtomic(Path.Combine(dir, "single-market-edge-distribution-latest.json"), payload, jsonOptions);
    }

    private static void WriteJsonAtomic<T>(string path, T payload, JsonSerializerOptions jsonOptions)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(payload, jsonOptions));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return;
            }
            catch (IOException)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (attempt >= 2) return;
                Thread.Sleep(25 * (attempt + 1));
            }
            catch (UnauthorizedAccessException)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                if (attempt >= 2) return;
                Thread.Sleep(25 * (attempt + 1));
            }
        }
        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
    }

    private SingleMarketOpportunityAuditDto AuditNearMiss(BinaryOrderBookSnapshot book, string? conditionId, decimal yes, decimal no, decimal rawCost, decimal rawEdge, decimal afterCostEdge, decimal afterSafetyEdge, decimal availableQty, decimal executableQty, decimal notionalAtCap, string rejectedReason, string? dataQualityReason, bool fillPassed, bool depthPassed, bool riskPassed, bool paperDiagnosticsLimitedGatePassed)
    {
        var audit = new SingleMarketOpportunityAuditDto(book.MarketId, conditionId, book.Question, yes, no, rawCost, rawEdge, afterCostEdge, afterSafetyEdge, availableQty, executableQty, notionalAtCap, rejectedReason, dataQualityReason, fillPassed, depthPassed, riskPassed, paperDiagnosticsLimitedGatePassed, DateTime.UtcNow);
        PaperPhase1PositiveCaptureService.Observe(book, audit, _scanId);
        return audit;
    }

    private SingleMarketDataQualityRejectSampleDto Sample(BinaryOrderBookSnapshot book, string? conditionId, decimal yes, decimal no, decimal raw, string reason, decimal edge)
        => new(DateTime.UtcNow, book.MarketId, conditionId, book.Question, reason, yes == 0m ? null : yes, no == 0m ? null : no, raw, edge);

    private SingleMarketNearMissDto NearMiss(BinaryOrderBookSnapshot book, string? conditionId, decimal yes, decimal no, decimal raw, decimal edge)
        => new(DateTime.UtcNow, book.MarketId, conditionId, book.Question, yes, no, raw, edge, _minEdgePerShare - edge);

    private void RecordOpened(string marketId) { lock (_gate) _cooldownUntil[marketId] = DateTime.UtcNow.AddSeconds(_options.CooldownSecondsPerMarket); }
    private void RecordSuppression(string marketId) { lock (_gate) _cooldownUntil[marketId] = DateTime.UtcNow.AddSeconds(_options.CooldownSecondsPerMarket); }
    private MarketStability GetStability(string marketId) { lock (_gate) return _stability.TryGetValue(marketId, out var s) ? s : new(); }
    private MarketStability IncrementEdge(string marketId) { lock (_gate) { var s = _stability.TryGetValue(marketId, out var cur) ? cur : new(); s = s with { EdgeScans = s.EdgeScans + 1 }; _stability[marketId] = s; return s; } }
    private MarketStability IncrementExecution(string marketId) { lock (_gate) { var s = _stability.TryGetValue(marketId, out var cur) ? cur : new(); s = s with { ExecutionScans = s.ExecutionScans + 1 }; _stability[marketId] = s; return s; } }
    private void ResetStability(string marketId) { lock (_gate) _stability[marketId] = new(); }
    private void ResetExecution(string marketId) { lock (_gate) { var s = _stability.TryGetValue(marketId, out var cur) ? cur : new(); _stability[marketId] = s with { ExecutionScans = 0 }; } }

    private SingleMarketArbOpportunityDto BuildDto(BinaryOrderBookSnapshot book, string? conditionId, SingleMarketArbState state, decimal yes, decimal no, decimal raw, decimal edge, decimal expected, decimal qty, decimal notional, string dq, string fill, string paper, string? reason, int edgeScans, int executionScans)
        => new($"{book.MarketId}:{DateTime.UtcNow:yyyyMMddHHmmssfff}:{state}", DateTime.UtcNow, book.MarketId, conditionId, book.Question, StrategyName, state, yes, no, raw, edge, expected, qty, notional, dq, fill, paper, reason, edgeScans, executionScans, true);

    private sealed record MarketStability(int EdgeScans = 0, int ExecutionScans = 0);
    private sealed record SingleMarketOpportunityLogState(string State, decimal Edge, DateTime LastFullAlertAt);
    private record SingleMarketScanResult(bool BookOk,bool BothAsks,bool Candidate,bool Executed,decimal? AdjustedCost,string? Question,decimal? Edge,string? SkipReason,NearMissOpportunity? NearMiss)
    { public static SingleMarketScanResult Empty => new(false,false,false,false,null,null,null,null,null); }

    private sealed class SingleMarketCycleDiagnostics(long scanId)
    {
        private int _scanned;
        private int _bookOk;
        private int _bothAsks;
        private int _dataQualityRejected;
        private int _belowMinEdge;
        private int _positiveEdge;
        private int _edgeStable;
        private int _executionReady;
        private int _fillPassed;
        private int _paperOpened;
        private int _auditSamples;
        private int _dataQualityAuditSamples;
        private int _highSeverityDataQuality;
        private decimal? _bestEdgeSeen;
        private decimal? _bestRejectedRawEdge;
        private decimal? _bestRawEdge;
        private decimal? _bestAfterCostEdge;
        private decimal? _bestAfterSafetyEdge;
        private decimal? _bestExecutableEdge;
        private int _dataQualityRejectedRawPositive;
        private int _validRawPositive;
        private int _validAfterCostPositive;
        private int _validAfterSafetyPositive;
        private int _rejectedByFill;
        private int _rejectedByDepth;
        private int _rejectedByRisk;
        private int _rejectedByPaperDiagnosticsLimitedGate;
        private const int EdgeDistributionCapacity = 4096;
        private readonly object _distributionGate = new();
        private readonly Random _distributionRandom = new(unchecked((int)scanId));
        private readonly List<EdgeDistributionSample> _edgeDistributionSamples = new(EdgeDistributionCapacity);
        private long _edgeDistributionTotal;
        private int _bucketBelowMinus5bp;
        private int _bucketMinus5bpToMinus2bp;
        private int _bucketMinus2bpToMinus1bp;
        private int _bucketMinus1bpTo0;
        private int _bucket0To1bp;
        private int _bucket1bpTo5bp;
        private int _bucketAbove5bp;
        private readonly object _edgeGate = new();
        public long ScanId { get; } = scanId;
        public int Scanned => Volatile.Read(ref _scanned);
        public int BookOk => Volatile.Read(ref _bookOk);
        public int BothAsks => Volatile.Read(ref _bothAsks);
        public int DataQualityRejected => Volatile.Read(ref _dataQualityRejected);
        public int BelowMinEdge => Volatile.Read(ref _belowMinEdge);
        public int PositiveEdge => Volatile.Read(ref _positiveEdge);
        public int EdgeStable => Volatile.Read(ref _edgeStable);
        public int ExecutionReady => Volatile.Read(ref _executionReady);
        public int FillPassed => Volatile.Read(ref _fillPassed);
        public int PaperOpened => Volatile.Read(ref _paperOpened);
        public int HighSeverityDataQuality => Volatile.Read(ref _highSeverityDataQuality);
        public decimal? BestEdgeSeen { get { lock (_edgeGate) return _bestEdgeSeen; } }
        public decimal? BestRejectedRawEdge { get { lock (_edgeGate) return _bestRejectedRawEdge; } }
        public decimal? BestRawEdge { get { lock (_edgeGate) return _bestRawEdge; } }
        public decimal? BestAfterCostEdge { get { lock (_edgeGate) return _bestAfterCostEdge; } }
        public decimal? BestAfterSafetyEdge { get { lock (_edgeGate) return _bestAfterSafetyEdge; } }
        public decimal? BestExecutableEdge { get { lock (_edgeGate) return _bestExecutableEdge; } }
        public int DataQualityRejectedRawPositive => Volatile.Read(ref _dataQualityRejectedRawPositive);
        public int ValidRawPositive => Volatile.Read(ref _validRawPositive);
        public int ValidAfterCostPositive => Volatile.Read(ref _validAfterCostPositive);
        public int ValidAfterSafetyPositive => Volatile.Read(ref _validAfterSafetyPositive);
        public int RejectedByFill => Volatile.Read(ref _rejectedByFill);
        public int RejectedByDepth => Volatile.Read(ref _rejectedByDepth);
        public int RejectedByRisk => Volatile.Read(ref _rejectedByRisk);
        public int RejectedByPaperDiagnosticsLimitedGate => Volatile.Read(ref _rejectedByPaperDiagnosticsLimitedGate);
        public ConcurrentDictionary<string, int> RejectCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, int> DataQualityCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        private ConcurrentDictionary<string, byte> DataQualityAuditReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentBag<SingleMarketDataQualityRejectSampleDto> DataQualitySamples { get; } = new();
        public ConcurrentBag<SingleMarketNearMissDto> NearMisses { get; } = new();
        public ConcurrentBag<SingleMarketOpportunityAuditDto> AuditNearMisses { get; } = new();
        public ConcurrentBag<SingleMarketArbOpportunityDto> PositiveCandidates { get; } = new();
        public ConcurrentBag<SingleMarketPaperExecutionDto> Executions { get; } = new();
        public void IncrementScanned() => Interlocked.Increment(ref _scanned);
        public void IncrementBookOk() => Interlocked.Increment(ref _bookOk);
        public void IncrementBothAsks() => Interlocked.Increment(ref _bothAsks);
        public void IncrementPositiveEdge() => Interlocked.Increment(ref _positiveEdge);
        public void IncrementEdgeStable() => Interlocked.Increment(ref _edgeStable);
        public void IncrementExecutionReady() => Interlocked.Increment(ref _executionReady);
        public void IncrementFillPassed() => Interlocked.Increment(ref _fillPassed);
        public void IncrementPaperOpened() => Interlocked.Increment(ref _paperOpened);
        public void IncrementHighSeverityDataQuality() => Interlocked.Increment(ref _highSeverityDataQuality);
        public void IncrementDataQualityRejectedRawPositive() => Interlocked.Increment(ref _dataQualityRejectedRawPositive);
        public void IncrementValidRawPositive() => Interlocked.Increment(ref _validRawPositive);
        public void IncrementValidAfterCostPositive() => Interlocked.Increment(ref _validAfterCostPositive);
        public void IncrementValidAfterSafetyPositive() => Interlocked.Increment(ref _validAfterSafetyPositive);
        public void IncrementRejectedByFill() => Interlocked.Increment(ref _rejectedByFill);
        public void IncrementRejectedByDepth() => Interlocked.Increment(ref _rejectedByDepth);
        public void IncrementRejectedByRisk() => Interlocked.Increment(ref _rejectedByRisk);
        public void IncrementRejectedByPaperDiagnosticsLimitedGate() => Interlocked.Increment(ref _rejectedByPaperDiagnosticsLimitedGate);
        public void RecordValidEdge(decimal edge) { lock (_edgeGate) if (!_bestEdgeSeen.HasValue || edge > _bestEdgeSeen.Value) _bestEdgeSeen = edge; }
        public void RecordRejectedRawEdge(decimal edge) { lock (_edgeGate) if (!_bestRejectedRawEdge.HasValue || edge > _bestRejectedRawEdge.Value) _bestRejectedRawEdge = edge; }
        public void RecordValidRawEdge(decimal edge) { lock (_edgeGate) if (!_bestRawEdge.HasValue || edge > _bestRawEdge.Value) _bestRawEdge = edge; }
        public void RecordValidAfterCostEdge(decimal edge) { lock (_edgeGate) if (!_bestAfterCostEdge.HasValue || edge > _bestAfterCostEdge.Value) _bestAfterCostEdge = edge; }
        public void RecordValidAfterSafetyEdge(decimal edge) { lock (_edgeGate) if (!_bestAfterSafetyEdge.HasValue || edge > _bestAfterSafetyEdge.Value) _bestAfterSafetyEdge = edge; }
        public void RecordBestExecutableEdge(decimal edge) { lock (_edgeGate) if (!_bestExecutableEdge.HasValue || edge > _bestExecutableEdge.Value) _bestExecutableEdge = edge; }
        public void RecordEdgeDistribution(decimal rawEdge, decimal afterCostEdge, decimal afterSafetyEdge)
        {
            lock (_distributionGate)
            {
                _edgeDistributionTotal++;
                var sample = new EdgeDistributionSample(rawEdge, afterCostEdge, afterSafetyEdge);
                if (_edgeDistributionSamples.Count < EdgeDistributionCapacity) _edgeDistributionSamples.Add(sample);
                else
                {
                    var replacement = _distributionRandom.NextInt64(_edgeDistributionTotal);
                    if (replacement < EdgeDistributionCapacity) _edgeDistributionSamples[(int)replacement] = sample;
                }
            }
            IncrementAfterSafetyBucket(afterSafetyEdge);
        }

        public SingleMarketEdgeDistributionDto BuildEdgeDistribution()
        {
            EdgeDistributionSample[] samples;
            long total;
            lock (_distributionGate)
            {
                samples = _edgeDistributionSamples.ToArray();
                total = _edgeDistributionTotal;
            }
            return new SingleMarketEdgeDistributionDto(
                ValidEdgeSamples: checked((int)Math.Min(int.MaxValue, total)),
                SampleMode: "Reservoir",
                Capacity: EdgeDistributionCapacity,
                DroppedSamples: Math.Max(0, total - samples.Length),
                RawEdge: Quantiles(samples.Select(x => x.RawEdge)),
                AfterCostEdge: Quantiles(samples.Select(x => x.AfterCostEdge)),
                AfterSafetyEdge: Quantiles(samples.Select(x => x.AfterSafetyEdge)),
                ThresholdBuckets: new SingleMarketAfterSafetyEdgeBucketsDto(
                    BelowMinus5bp: Volatile.Read(ref _bucketBelowMinus5bp),
                    Minus5bpToMinus2bp: Volatile.Read(ref _bucketMinus5bpToMinus2bp),
                    Minus2bpToMinus1bp: Volatile.Read(ref _bucketMinus2bpToMinus1bp),
                    Minus1bpTo0: Volatile.Read(ref _bucketMinus1bpTo0),
                    ZeroTo1bp: Volatile.Read(ref _bucket0To1bp),
                    OnebpTo5bp: Volatile.Read(ref _bucket1bpTo5bp),
                    Above5bp: Volatile.Read(ref _bucketAbove5bp)));
        }

        private void IncrementAfterSafetyBucket(decimal edge)
        {
            if (edge < -0.0005m) Interlocked.Increment(ref _bucketBelowMinus5bp);
            else if (edge < -0.0002m) Interlocked.Increment(ref _bucketMinus5bpToMinus2bp);
            else if (edge < -0.0001m) Interlocked.Increment(ref _bucketMinus2bpToMinus1bp);
            else if (edge < 0m) Interlocked.Increment(ref _bucketMinus1bpTo0);
            else if (edge < 0.0001m) Interlocked.Increment(ref _bucket0To1bp);
            else if (edge <= 0.0005m) Interlocked.Increment(ref _bucket1bpTo5bp);
            else Interlocked.Increment(ref _bucketAbove5bp);
        }

        private static SingleMarketEdgeQuantilesDto Quantiles(IEnumerable<decimal> values)
        {
            var sorted = values.OrderBy(x => x).ToArray();
            if (sorted.Length == 0) return new SingleMarketEdgeQuantilesDto();
            decimal Q(decimal p) => sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
            return new SingleMarketEdgeQuantilesDto(sorted[0], Q(0.01m), Q(0.05m), Q(0.10m), Q(0.25m), Q(0.50m), Q(0.75m), Q(0.90m), Q(0.95m), Q(0.99m), sorted[^1]);
        }

        private sealed record EdgeDistributionSample(decimal RawEdge, decimal AfterCostEdge, decimal AfterSafetyEdge);
        public void AddReject(string reason)
        {
            RejectCounts.AddOrUpdate(reason, 1, (_, x) => x + 1);
            if (reason == "BelowMinEdge") Interlocked.Increment(ref _belowMinEdge);
        }
        public void AddDataQualityReject(string reason, SingleMarketDataQualityRejectSampleDto sample)
        {
            Interlocked.Increment(ref _dataQualityRejected);
            AddReject(reason);
            DataQualityCounts.AddOrUpdate(reason, 1, (_, x) => x + 1);
            DataQualitySamples.Add(sample);
        }
        public bool MarkDataQualityAuditReason(string reason) => DataQualityAuditReasons.TryAdd(reason, 0);
        public void AddNearMiss(SingleMarketNearMissDto nearMiss) => NearMisses.Add(nearMiss);
        public void RecordAuditCandidate(SingleMarketOpportunityAuditDto nearMiss) => AuditNearMisses.Add(nearMiss);
        public void AddPositiveCandidate(SingleMarketArbOpportunityDto dto) => PositiveCandidates.Add(dto);
        public void AddExecution(SingleMarketPaperExecutionDto dto) => Executions.Add(dto);
        public bool ShouldAuditSample(int maxSamples) => TryReserve(ref _auditSamples, maxSamples);
        public bool ShouldAuditDataQualitySample(int maxSamples) => TryReserve(ref _dataQualityAuditSamples, maxSamples);
        private static bool TryReserve(ref int counter, int maxSamples)
        {
            if (maxSamples <= 0) return false;
            while (true)
            {
                var cur = Volatile.Read(ref counter);
                if (cur >= maxSamples) return false;
                if (Interlocked.CompareExchange(ref counter, cur + 1, cur) == cur) return true;
            }
        }
    }
}
