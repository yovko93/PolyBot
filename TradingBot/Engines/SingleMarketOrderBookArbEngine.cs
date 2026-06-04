using System.Collections.Concurrent;
using System.Text.Json;
using TradingBot.Api;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;

namespace TradingBot.Engines;

public record SingleMarketScanStats(int Scanned,int BookOk,int BothAsks,int Candidates,int Executed,int PositiveEdgeFound,int NegativeEdgeSkipped,int ZeroEdgeSkipped, Dictionary<string,int>? SkipReasons = null, List<NearMissOpportunity>? NearMisses = null, decimal? BestEdgeSeen = null, decimal? WorstEdgeSeen = null);

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
    private readonly string? _contentRootPath;
    private readonly SingleMarketDataQualityValidator _dataQuality;
    private readonly SingleMarketFillSimulator _fillSimulator = new();
    private readonly VerifiedBasketExecutionCoordinator? _audit;
    private readonly object _gate = new();
    private readonly Dictionary<string, MarketStability> _stability = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loggedDataQualityReasons = new(StringComparer.OrdinalIgnoreCase);
    private string _lastSummaryHash = string.Empty;
    private string _lastDataQualityHash = string.Empty;
    private string _lastNearMissHash = string.Empty;
    private long _scanId;
    private int _positionsOpenedThisCycle;

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
        MultiOutcomeLoggingOptions? logging = null)
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
        _contentRootPath = contentRootPath;
        _dataQuality = new SingleMarketDataQualityValidator(_options);
        _audit = audit;
        _operationalQuietMode = operationalQuietMode;
        _logging = logging ?? new MultiOutcomeLoggingOptions();
    }

    public async Task<SingleMarketScanStats> ScanAsync(List<Market> markets, PaperTradingEngine paper, SemaphoreSlim semaphore, CancellationToken ct = default)
    {
        _positionsOpenedThisCycle = 0;
        var scanId = Interlocked.Increment(ref _scanId);
        var diagnostics = new SingleMarketCycleDiagnostics(scanId);
        var tasks = markets.Select(market => ScanMarketAsync(market, paper, semaphore, diagnostics, ct));
        var results = await Task.WhenAll(tasks);
        var summary = BuildAndPublishSnapshot(diagnostics);
        MaybeLogSummaries(summary, diagnostics);
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
        return new SingleMarketScanStats(results.Length, results.Count(x => x.BookOk), results.Count(x => x.BothAsks), results.Count(x => x.Candidate), results.Count(x => x.Executed), results.Count(x => x.Candidate && x.AdjustedCost.HasValue && 1m - x.AdjustedCost.Value > 0m), results.Count(x => x.Candidate && x.AdjustedCost.HasValue && 1m - x.AdjustedCost.Value < 0m), results.Count(x => x.Candidate && x.AdjustedCost.HasValue && 1m - x.AdjustedCost.Value == 0m), skipReasons, nearMisses, edges.Count>0?edges.Max():null, edges.Count>0?edges.Min():null);
    }

    private async Task<SingleMarketScanResult> ScanMarketAsync(Market market, PaperTradingEngine paper, SemaphoreSlim semaphore, SingleMarketCycleDiagnostics diagnostics, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            diagnostics.IncrementScanned();
            if (!_options.Enabled) return SingleMarketScanResult.Empty;
            var now = DateTime.UtcNow;
            var book = await _orderBooks.GetBinarySnapshotAsync(market, ct);
            if (book == null) return SingleMarketScanResult.Empty;
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
                diagnostics.RecordRejectedRawEdge(edge);
                diagnostics.AddDataQualityReject(dq.Reason, Sample(book, market.conditionId, yes, no, rawCost, dq.Reason, edge));
                var reject = BuildDto(book, market.conditionId, SingleMarketArbState.Rejected, yes, no, rawCost, edge, 0m, 0m, 0m, "Rejected", "NotRun", "NotOpened", dq.Reason, 0, 0);
                MaybeStoreOpportunity(reject, highValue: IsHighSeverityDataQuality(dq.Reason, rawCost));
                var highSeverityDataQuality = IsHighSeverityDataQuality(dq.Reason, rawCost);
                if (highSeverityDataQuality) diagnostics.IncrementHighSeverityDataQuality();
                MaybeAuditDataQuality(reject, dq.Reason, rawCost, edge, diagnostics);
                if (ShouldLogDataQualityReject(dq.Reason, rawCost))
                    Console.WriteLine($"[SINGLE_MARKET_DATA_QUALITY_REJECTED] Market={book.Question} Reason={dq.Reason} YesAsk={yes} NoAsk={no} RawSum={rawCost}");
                return new SingleMarketScanResult(true, book.YesAsk != null && book.NoAsk != null, false, false, adjustedCost, book.Question, edge, dq.Reason, null);
            }

            diagnostics.IncrementBothAsks();
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
            Console.WriteLine($"[SINGLE_MARKET_POSITIVE_EDGE_DETECTED] MarketId={book.MarketId} Edge={edge:0.####} ExpectedProfit={expected:0.####} Qty={quantity:0.####}");

            var st = IncrementEdge(book.MarketId);
            if (st.EdgeScans < _options.RequiredConsecutiveEdgeScans)
            {
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,"EdgePending",null);
            }

            diagnostics.IncrementEdgeStable();
            RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.EdgeStable, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", null, st.EdgeScans, st.ExecutionScans), "SingleMarketEdgeStable");
            Console.WriteLine($"[SINGLE_MARKET_EDGE_STABLE] MarketId={book.MarketId} Edge={edge:0.####} Consecutive={st.EdgeScans}");

            var readinessReason = CheckExecutionReadiness(book, quantity, expected, quantity * adjustedCost);
            if (readinessReason != "Ok")
            {
                ResetExecution(book.MarketId);
                diagnostics.AddReject(readinessReason);
                RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.ExecutionReadinessPending, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", readinessReason, st.EdgeScans, 0), "SingleMarketRiskRejected");
                Console.WriteLine($"[SINGLE_MARKET_EXECUTION_READINESS_PENDING] MarketId={book.MarketId} Reason={readinessReason}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,readinessReason,null);
            }

            st = IncrementExecution(book.MarketId);
            if (st.ExecutionScans < _options.RequiredConsecutiveExecutionReadyScans)
            {
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,"ExecutionReadinessPending",null);
            }

            diagnostics.IncrementExecutionReady();
            RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.ExecutionStable, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", null, st.EdgeScans, st.ExecutionScans), "SingleMarketExecutionReadinessStable");
            Console.WriteLine($"[SINGLE_MARKET_EXECUTION_READINESS_STABLE] MarketId={book.MarketId} Edge={edge:0.####} Qty={quantity:0.####}");

            var suppression = CheckDuplicateAndCooldown(paper, book.MarketId);
            if (suppression != "Ok")
            {
                RecordSuppression(book.MarketId);
                diagnostics.AddReject(suppression);
                RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.SuppressedDuplicate, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "Suppressed", suppression, st.EdgeScans, st.ExecutionScans), "SingleMarketDuplicateSuppressed");
                Console.WriteLine($"[SINGLE_MARKET_EXECUTION_SUPPRESSED] MarketId={book.MarketId} Reason={suppression}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,suppression,null);
            }

            var risk = CheckRisk(paper, quantity * adjustedCost);
            if (risk == "Ok" && !TryReserveCycleSlot()) risk = "MaxPositionsPerCycleReached";
            if (risk != "Ok")
            {
                diagnostics.AddReject(risk);
                RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.Rejected, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "Rejected", risk, st.EdgeScans, st.ExecutionScans), "SingleMarketRiskRejected");
                Console.WriteLine($"[SINGLE_MARKET_PRETRADE_REJECTED] MarketId={book.MarketId} Reason={risk}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,risk,null);
            }

            Console.WriteLine($"[SINGLE_MARKET_DRY_RUN_ORDER_PLAN_CREATED] MarketId={book.MarketId} PaperOnly={_options.PaperOnly.ToString().ToLowerInvariant()} Qty={quantity:0.####}");
            RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.DryRunPlanCreated, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "Pending", "NotOpened", null, st.EdgeScans, st.ExecutionScans), "SingleMarketDryRunOrderPlanCreated");

            var fill = _fillSimulator.Simulate(book, quantity, _feeBuffer, _slippageBuffer);
            if (!fill.Passed || fill.AdjustedEdgePerShare < _minEdgePerShare || fill.ExpectedProfit < _options.MinExpectedProfit)
            {
                var reason = !fill.Passed ? fill.Reason : "FillAdjustedProfitBelowThreshold";
                diagnostics.AddReject(reason);
                RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.Rejected, yes, no, rawCost, fill.AdjustedEdgePerShare, fill.ExpectedProfit, quantity, fill.SimulatedCost, "Passed", "Rejected", "Rejected", reason, st.EdgeScans, st.ExecutionScans), "SingleMarketFillRejected");
                Console.WriteLine($"[SINGLE_MARKET_FILL_REJECTED] MarketId={book.MarketId} Reason={reason} PlannedQty={quantity:0.####} FullyFillableQty={fill.FullyFillableQty:0.####}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,reason,null);
            }
            diagnostics.IncrementFillPassed();
            Console.WriteLine($"[SINGLE_MARKET_FILL_SIMULATION_PASSED] MarketId={book.MarketId} Qty={quantity:0.####} FullyFillableQty={fill.FullyFillableQty:0.####} SimulatedCost={fill.SimulatedCost:0.####}");
            RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.FillSimulationPassed, yes, no, rawCost, fill.AdjustedEdgePerShare, fill.ExpectedProfit, quantity, fill.SimulatedCost, "Passed", "Passed", "NotOpened", null, st.EdgeScans, st.ExecutionScans), "SingleMarketFillSimulationPassed");

            var opportunity = new ArbOpportunity(new ArbLeg(book.MarketId, book.Question, "YES", fill.YesAveragePrice, book.YesAsk.Size), new ArbLeg(book.MarketId, book.Question, "NO", fill.NoAveragePrice, book.NoAsk.Size), quantity, fill.SimulatedCost / quantity, fill.AdjustedEdgePerShare, fill.ExpectedProfit, 1.0, "SingleMarketBuyBoth", StrategyName);
            var equityBefore = paper.Equity;
            var executed = paper.RecordArbitrage(opportunity);
            if (!executed)
            {
                diagnostics.AddReject("PaperOpenBlocked");
                Console.WriteLine($"[SINGLE_MARKET_PAPER_OPEN_BLOCKED] MarketId={book.MarketId} Reason=PaperRejected");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,"PaperRejected",null);
            }
            diagnostics.IncrementPaperOpened();
            RecordOpened(book.MarketId);
            var execution = new SingleMarketPaperExecutionDto(Guid.NewGuid().ToString("N"), DateTime.UtcNow, book.MarketId, book.Question, StrategyName, quantity, fill.YesAveragePrice, fill.NoAveragePrice, fill.SimulatedCost, fill.AdjustedEdgePerShare, fill.ExpectedProfit, paper.Balance, paper.LockedCapital, paper.Equity, "Opened", true);
            _state?.AddSingleMarketExecution(execution);
            diagnostics.AddExecution(execution);
            RecordHighValue(BuildDto(book, market.conditionId, SingleMarketArbState.PaperOpened, yes, no, rawCost, fill.AdjustedEdgePerShare, fill.ExpectedProfit, quantity, fill.SimulatedCost, "Passed", "Passed", $"Opened EquityUnchanged={paper.Equity == equityBefore}", null, st.EdgeScans, st.ExecutionScans), "SingleMarketPaperOpened");
            return new SingleMarketScanResult(true,true,true,true,adjustedCost,book.Question,edge,null,null);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SINGLE_MARKET_ERROR] {ex.Message}");
            return SingleMarketScanResult.Empty;
        }
        finally { semaphore.Release(); }
    }

    private void RecordMonitor(BinaryOrderBookSnapshot book, decimal edge, decimal adjustedCost, decimal quantity)
    {
        _monitor?.Record(new ArbMonitorRecord(DateTime.UtcNow, "SingleMarketBuyBoth", StrategyName, book.MarketId, edge, adjustedCost, 1m, quantity, quantity * edge, false, $"BUY YES @ {book.YesAsk!.Price} | {book.Question}", $"BUY NO @ {book.NoAsk!.Price} | {book.Question}", null)
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

    private string CheckDuplicateAndCooldown(PaperTradingEngine paper, string marketId)
    {
        if (paper.HasOpenSingleMarketPosition(marketId, StrategyName)) return "DuplicateOpenPosition";
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
            dataQualityCounts);
        var snapshot = new SingleMarketArbSnapshotDto(DateTime.UtcNow, diagnostics.ScanId, summary, positive, topNearMisses, dqSamples, executions);
        _state?.SetSingleMarketSnapshot(snapshot);
        return summary;
    }

    private void MaybeLogSummaries(SingleMarketScanSummaryDto summary, SingleMarketCycleDiagnostics diagnostics)
    {
        var summaryHash = $"{Bucket(summary.Scanned, 25)}|{summary.PositiveEdge}|{summary.TopRejectReason}|{summary.PaperOpened}|{Bucket(summary.BestEdgeSeen ?? 0m, 0.001m)}";
        if (ShouldLog(ref _lastSummaryHash, summaryHash, summary.ScanId, _logging.LogSingleMarketSummaryEveryNCycles, _logging.LogSingleMarketSummaryOnChangeOnly))
        {
            var bestEdgeText = summary.BestEdgeSeen.HasValue ? summary.BestEdgeSeen.Value.ToString("0.####") : "N/A";
            var bestRejectedText = summary.BestRejectedRawEdge.HasValue ? summary.BestRejectedRawEdge.Value.ToString("0.####") : "N/A";
            Console.WriteLine($"[SINGLE_MARKET_SCAN_SUMMARY] Scanned={summary.Scanned} DataQualityRejected={summary.DataQualityRejected} BelowMinEdge={summary.BelowMinEdge} PositiveEdge={summary.PositiveEdge} EdgeStable={summary.EdgeStable} ExecutionReady={summary.ExecutionReady} FillPassed={summary.FillPassed} PaperOpened={summary.PaperOpened} TopReject={summary.TopRejectReason}:{summary.TopRejectCount} BestEdge={bestEdgeText} BestRejectedRawEdge={bestRejectedText}");
        }

        var dqDelta = Math.Max(1, _logging.SingleMarketDataQualitySignificantDelta);
        var dqHash = $"total:{Bucket(summary.DataQualityRejected, dqDelta)}|" + string.Join("|", summary.DataQualityRejectedByReason.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{Bucket(x.Value, dqDelta)}"));
        var criticalDataQuality = diagnostics.HighSeverityDataQuality > 0;
        if (summary.DataQualityRejected > 0 && (criticalDataQuality || ShouldLog(ref _lastDataQualityHash, dqHash, summary.ScanId, _logging.LogSingleMarketDataQualityEveryNCycles, _logging.LogSingleMarketDataQualityOnChangeOnly)))
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

    private bool ShouldLogDataQualityReject(string reason, decimal rawSum)
    {
        if (!_operationalQuietMode) return IsHighSeverityDataQuality(reason, rawSum);
        if (!IsHighSeverityDataQuality(reason, rawSum)) return false;
        lock (_gate) return _loggedDataQualityReasons.Add(reason);
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

    private void MaybeAuditDataQuality(SingleMarketArbOpportunityDto dto, string reason, decimal rawSum, decimal edge, SingleMarketCycleDiagnostics diagnostics)
    {
        var firstReason = diagnostics.MarkDataQualityAuditReason(reason);
        var highSeverity = IsHighSeverityDataQuality(reason, rawSum);
        var positiveRejected = edge >= _minEdgePerShare && reason is not ("MissingYesAsk" or "MissingNoAsk");
        var enabledSample = _options.AuditDataQualityRejectedEvents && firstReason;
        var highSeveritySample = _options.AuditHighSeverityDataQualityRejectedEvents && highSeverity;
        var positiveSample = positiveRejected && firstReason;
        if (!(enabledSample || highSeveritySample || positiveSample)) return;
        if (!diagnostics.ShouldAuditDataQualitySample(_options.MaxDataQualityAuditSamplesPerCycle)) return;
        MaybeAudit(dto, "SingleMarketDataQualityRejected", highValue: highSeverity || positiveRejected, sampled: true);
    }

    private void MaybeAudit(SingleMarketArbOpportunityDto dto, string auditStage, bool highValue, bool sampled)
    {
        if (!highValue && !sampled) return;
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
        public ConcurrentDictionary<string, int> RejectCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, int> DataQualityCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        private ConcurrentDictionary<string, byte> DataQualityAuditReasons { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentBag<SingleMarketDataQualityRejectSampleDto> DataQualitySamples { get; } = new();
        public ConcurrentBag<SingleMarketNearMissDto> NearMisses { get; } = new();
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
        public void RecordValidEdge(decimal edge) { lock (_edgeGate) if (!_bestEdgeSeen.HasValue || edge > _bestEdgeSeen.Value) _bestEdgeSeen = edge; }
        public void RecordRejectedRawEdge(decimal edge) { lock (_edgeGate) if (!_bestRejectedRawEdge.HasValue || edge > _bestRejectedRawEdge.Value) _bestRejectedRawEdge = edge; }
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
