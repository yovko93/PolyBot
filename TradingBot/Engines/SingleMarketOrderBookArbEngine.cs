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
    private readonly BotRuntimeState? _state;
    private readonly string? _contentRootPath;
    private readonly SingleMarketDataQualityValidator _dataQuality;
    private readonly SingleMarketFillSimulator _fillSimulator = new();
    private readonly VerifiedBasketExecutionCoordinator? _audit;
    private readonly object _gate = new();
    private readonly Dictionary<string, MarketStability> _stability = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _cooldownUntil = new(StringComparer.OrdinalIgnoreCase);
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
        VerifiedBasketExecutionCoordinator? audit = null)
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
    }

    public async Task<SingleMarketScanStats> ScanAsync(List<Market> markets, PaperTradingEngine paper, SemaphoreSlim semaphore, CancellationToken ct = default)
    {
        _positionsOpenedThisCycle = 0;
        var tasks = markets.Select(market => ScanMarketAsync(market, paper, semaphore, ct));
        var results = await Task.WhenAll(tasks);
        ExportLatest();
        var skipReasons = new Dictionary<string, int>();
        var nearMisses = new List<NearMissOpportunity>();
        var edges = new List<decimal>();
        foreach (var r in results)
        {
            if (r.Edge.HasValue) edges.Add(r.Edge.Value);
            if (!string.IsNullOrWhiteSpace(r.SkipReason)) skipReasons[r.SkipReason!] = skipReasons.GetValueOrDefault(r.SkipReason!, 0) + 1;
            if (r.NearMiss != null) nearMisses.Add(r.NearMiss);
        }
        return new SingleMarketScanStats(results.Length, results.Count(x => x.BookOk), results.Count(x => x.BothAsks), results.Count(x => x.Candidate), results.Count(x => x.Executed), results.Count(x => x.AdjustedCost.HasValue && 1m - x.AdjustedCost.Value > 0m), results.Count(x => x.AdjustedCost.HasValue && 1m - x.AdjustedCost.Value < 0m), results.Count(x => x.AdjustedCost.HasValue && 1m - x.AdjustedCost.Value == 0m), skipReasons, nearMisses, edges.Count>0?edges.Max():null, edges.Count>0?edges.Min():null);
    }

    private async Task<SingleMarketScanResult> ScanMarketAsync(Market market, PaperTradingEngine paper, SemaphoreSlim semaphore, CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            if (!_options.Enabled) return SingleMarketScanResult.Empty;
            var now = DateTime.UtcNow;
            var book = await _orderBooks.GetBinarySnapshotAsync(market, ct);
            if (book == null) return SingleMarketScanResult.Empty;
            if (book.TimestampUtc == default) book = book with { TimestampUtc = now };

            var yes = book.YesAsk?.Price ?? 0m;
            var no = book.NoAsk?.Price ?? 0m;
            var rawCost = yes + no;
            var adjustedCost = rawCost + _feeBuffer + _slippageBuffer;
            var edge = 1m - adjustedCost;
            var dq = _dataQuality.Validate(market, book, now);
            if (!dq.IsValid)
            {
                var reject = BuildDto(book, market.conditionId, SingleMarketArbState.Rejected, yes, no, rawCost, edge, 0m, 0m, 0m, "Rejected", "NotRun", "NotOpened", dq.Reason, 0, 0);
                RecordOpportunity(reject, "SingleMarketDataQualityRejected");
                Console.WriteLine($"[SINGLE_MARKET_DATA_QUALITY_REJECTED] Market={book.Question} Reason={dq.Reason} YesAsk={yes} NoAsk={no} RawSum={rawCost}");
                return new SingleMarketScanResult(true, book.YesAsk != null && book.NoAsk != null, false, false, adjustedCost, book.Question, edge, dq.Reason, null);
            }

            var quantityAvailable = Math.Min(book.YesAsk!.Size, book.NoAsk!.Size);
            var sizing = _sizing.SizeByNotional(quantityAvailable, adjustedCost);
            var quantity = sizing.ExecutableQuantity;
            if (_sizingLogsEnabled && edge >= _minEdgePerShare && quantity > 0m && sizing.WasClamped)
                Console.WriteLine($"[SIZING] Strategy={StrategyName} AvailableQty={sizing.QuantityAvailable:0.####} ExecutableQty={sizing.ExecutableQuantity:0.####} Notional={sizing.Notional:0.####} MaxNotional={sizing.MaxNotional:0.####} Edge={edge:0.####}");

            var expected = quantity * edge;
            RecordMonitor(book, edge, adjustedCost, quantity);
            RecordOpportunity(BuildDto(book, market.conditionId, SingleMarketArbState.CandidateDetected, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", null, GetStability(book.MarketId).EdgeScans, GetStability(book.MarketId).ExecutionScans), "SingleMarketDetected");

            if (edge < _minEdgePerShare || quantity <= 0m || expected < _options.MinExpectedProfit || quantity * adjustedCost < _options.MinNotional)
            {
                ResetStability(book.MarketId);
                var reason = edge < _minEdgePerShare ? "BelowMinEdge" : quantity <= 0m ? "InsufficientLiquidity" : expected < _options.MinExpectedProfit ? "BelowMinExpectedProfit" : "BelowMinNotional";
                RecordOpportunity(BuildDto(book, market.conditionId, SingleMarketArbState.EdgePending, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", reason, 0, 0), "SingleMarketEdgePending");
                Console.WriteLine($"[SINGLE_MARKET_EDGE_PENDING] MarketId={book.MarketId} Reason={reason} Edge={edge:0.####}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,reason,null);
            }

            var st = IncrementEdge(book.MarketId);
            if (st.EdgeScans < _options.RequiredConsecutiveEdgeScans)
            {
                RecordOpportunity(BuildDto(book, market.conditionId, SingleMarketArbState.EdgePending, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", "WaitingForEdgeStability", st.EdgeScans, st.ExecutionScans), "SingleMarketEdgePending");
                Console.WriteLine($"[SINGLE_MARKET_EDGE_PENDING] MarketId={book.MarketId} Consecutive={st.EdgeScans} Required={_options.RequiredConsecutiveEdgeScans} Edge={edge:0.####}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,"EdgePending",null);
            }

            RecordOpportunity(BuildDto(book, market.conditionId, SingleMarketArbState.EdgeStable, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", null, st.EdgeScans, st.ExecutionScans), "SingleMarketEdgeStable");

            var readinessReason = CheckExecutionReadiness(book, quantity, expected, quantity * adjustedCost);
            if (readinessReason != "Ok")
            {
                ResetExecution(book.MarketId);
                RecordOpportunity(BuildDto(book, market.conditionId, SingleMarketArbState.ExecutionReadinessPending, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", readinessReason, st.EdgeScans, 0), "SingleMarketEdgeStable");
                Console.WriteLine($"[SINGLE_MARKET_EXECUTION_READINESS_PENDING] MarketId={book.MarketId} Reason={readinessReason}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,readinessReason,null);
            }

            st = IncrementExecution(book.MarketId);
            if (st.ExecutionScans < _options.RequiredConsecutiveExecutionReadyScans)
            {
                RecordOpportunity(BuildDto(book, market.conditionId, SingleMarketArbState.ExecutionReadinessPending, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", "WaitingForExecutionReadinessStability", st.EdgeScans, st.ExecutionScans), "SingleMarketEdgeStable");
                Console.WriteLine($"[SINGLE_MARKET_EXECUTION_READINESS_PENDING] MarketId={book.MarketId} Consecutive={st.ExecutionScans} Required={_options.RequiredConsecutiveExecutionReadyScans}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,"ExecutionReadinessPending",null);
            }

            RecordOpportunity(BuildDto(book, market.conditionId, SingleMarketArbState.ExecutionStable, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "NotOpened", null, st.EdgeScans, st.ExecutionScans), "SingleMarketExecutionReadinessStable");
            Console.WriteLine($"[SINGLE_MARKET_EXECUTION_READINESS_STABLE] MarketId={book.MarketId} Edge={edge:0.####} Qty={quantity:0.####}");

            var suppression = CheckDuplicateAndCooldown(paper, book.MarketId);
            if (suppression != "Ok")
            {
                RecordSuppression(book.MarketId);
                RecordOpportunity(BuildDto(book, market.conditionId, SingleMarketArbState.SuppressedDuplicate, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "Suppressed", suppression, st.EdgeScans, st.ExecutionScans), "SingleMarketDuplicateSuppressed");
                Console.WriteLine($"[SINGLE_MARKET_EXECUTION_SUPPRESSED] MarketId={book.MarketId} Reason={suppression}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,suppression,null);
            }

            var risk = CheckRisk(paper, quantity * adjustedCost);
            if (risk == "Ok" && !TryReserveCycleSlot()) risk = "MaxPositionsPerCycleReached";
            if (risk != "Ok")
            {
                RecordOpportunity(BuildDto(book, market.conditionId, SingleMarketArbState.Rejected, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "NotRun", "Rejected", risk, st.EdgeScans, st.ExecutionScans), "SingleMarketRiskRejected");
                Console.WriteLine($"[SINGLE_MARKET_PRETRADE_REJECTED] MarketId={book.MarketId} Reason={risk}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,risk,null);
            }

            Console.WriteLine($"[SINGLE_MARKET_DRY_RUN_ORDER_PLAN_CREATED] MarketId={book.MarketId} PaperOnly={_options.PaperOnly.ToString().ToLowerInvariant()} Qty={quantity:0.####}");
            RecordOpportunity(BuildDto(book, market.conditionId, SingleMarketArbState.DryRunPlanCreated, yes, no, rawCost, edge, expected, quantity, quantity * adjustedCost, "Passed", "Pending", "NotOpened", null, st.EdgeScans, st.ExecutionScans), "SingleMarketDryRunOrderPlanCreated");

            var fill = _fillSimulator.Simulate(book, quantity, _feeBuffer, _slippageBuffer);
            if (!fill.Passed || fill.AdjustedEdgePerShare < _minEdgePerShare || fill.ExpectedProfit < _options.MinExpectedProfit)
            {
                var reason = !fill.Passed ? fill.Reason : "FillAdjustedProfitBelowThreshold";
                RecordOpportunity(BuildDto(book, market.conditionId, SingleMarketArbState.Rejected, yes, no, rawCost, fill.AdjustedEdgePerShare, fill.ExpectedProfit, quantity, fill.SimulatedCost, "Passed", "Rejected", "Rejected", reason, st.EdgeScans, st.ExecutionScans), "SingleMarketRiskRejected");
                Console.WriteLine($"[SINGLE_MARKET_FILL_REJECTED] MarketId={book.MarketId} Reason={reason} PlannedQty={quantity:0.####} FullyFillableQty={fill.FullyFillableQty:0.####}");
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,reason,null);
            }
            Console.WriteLine($"[SINGLE_MARKET_FILL_SIMULATION_PASSED] MarketId={book.MarketId} Qty={quantity:0.####} FullyFillableQty={fill.FullyFillableQty:0.####} SimulatedCost={fill.SimulatedCost:0.####}");
            RecordOpportunity(BuildDto(book, market.conditionId, SingleMarketArbState.FillSimulationPassed, yes, no, rawCost, fill.AdjustedEdgePerShare, fill.ExpectedProfit, quantity, fill.SimulatedCost, "Passed", "Passed", "NotOpened", null, st.EdgeScans, st.ExecutionScans), "SingleMarketFillSimulationPassed");

            var opportunity = new ArbOpportunity(new ArbLeg(book.MarketId, book.Question, "YES", fill.YesAveragePrice, book.YesAsk.Size), new ArbLeg(book.MarketId, book.Question, "NO", fill.NoAveragePrice, book.NoAsk.Size), quantity, fill.SimulatedCost / quantity, fill.AdjustedEdgePerShare, fill.ExpectedProfit, 1.0, "SingleMarketBuyBoth", StrategyName);
            var equityBefore = paper.Equity;
            var executed = paper.RecordArbitrage(opportunity);
            if (!executed) return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,"PaperRejected",null);
            RecordOpened(book.MarketId);
            var execution = new SingleMarketPaperExecutionDto(Guid.NewGuid().ToString("N"), DateTime.UtcNow, book.MarketId, book.Question, StrategyName, quantity, fill.YesAveragePrice, fill.NoAveragePrice, fill.SimulatedCost, fill.AdjustedEdgePerShare, fill.ExpectedProfit, paper.Balance, paper.LockedCapital, paper.Equity, "Opened", true);
            _state?.AddSingleMarketExecution(execution);
            RecordOpportunity(BuildDto(book, market.conditionId, SingleMarketArbState.PaperOpened, yes, no, rawCost, fill.AdjustedEdgePerShare, fill.ExpectedProfit, quantity, fill.SimulatedCost, "Passed", "Passed", $"Opened EquityUnchanged={paper.Equity == equityBefore}", null, st.EdgeScans, st.ExecutionScans), "SingleMarketPaperOpened");
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

    private void RecordOpened(string marketId) { lock (_gate) _cooldownUntil[marketId] = DateTime.UtcNow.AddSeconds(_options.CooldownSecondsPerMarket); }
    private void RecordSuppression(string marketId) { lock (_gate) _cooldownUntil[marketId] = DateTime.UtcNow.AddSeconds(_options.CooldownSecondsPerMarket); }
    private MarketStability GetStability(string marketId) { lock (_gate) return _stability.TryGetValue(marketId, out var s) ? s : new(); }
    private MarketStability IncrementEdge(string marketId) { lock (_gate) { var s = _stability.TryGetValue(marketId, out var cur) ? cur : new(); s = s with { EdgeScans = s.EdgeScans + 1 }; _stability[marketId] = s; return s; } }
    private MarketStability IncrementExecution(string marketId) { lock (_gate) { var s = _stability.TryGetValue(marketId, out var cur) ? cur : new(); s = s with { ExecutionScans = s.ExecutionScans + 1 }; _stability[marketId] = s; return s; } }
    private void ResetStability(string marketId) { lock (_gate) _stability[marketId] = new(); }
    private void ResetExecution(string marketId) { lock (_gate) { var s = _stability.TryGetValue(marketId, out var cur) ? cur : new(); _stability[marketId] = s with { ExecutionScans = 0 }; } }

    private SingleMarketArbOpportunityDto BuildDto(BinaryOrderBookSnapshot book, string? conditionId, SingleMarketArbState state, decimal yes, decimal no, decimal raw, decimal edge, decimal expected, decimal qty, decimal notional, string dq, string fill, string paper, string? reason, int edgeScans, int executionScans)
        => new($"{book.MarketId}:{DateTime.UtcNow:yyyyMMddHHmmssfff}:{state}", DateTime.UtcNow, book.MarketId, conditionId, book.Question, StrategyName, state, yes, no, raw, edge, expected, qty, notional, dq, fill, paper, reason, edgeScans, executionScans, true);

    private void RecordOpportunity(SingleMarketArbOpportunityDto dto, string auditStage)
    {
        _state?.AddSingleMarketOpportunity(dto);
        _audit?.Audit(new ExecutionAuditEvent(DateTime.UtcNow, dto.Id, dto.MarketId, dto.Strategy, auditStage, dto.State.ToString(), dto.Reason ?? "Ok", dto.EdgePerShare, dto.ExpectedProfit, dto.PlannedNotional, dto.Quantity, $"PaperOnly={dto.PaperOnly}; DataQuality={dto.DataQualityStatus}; Fill={dto.FillSimulationStatus}; Paper={dto.PaperStatus}"));
        Console.WriteLine($"[EXECUTION_AUDIT] Stage={auditStage} MarketId={dto.MarketId} State={dto.State} Reason={dto.Reason ?? "Ok"}");
    }

    private void ExportLatest()
    {
        if (_state is null || string.IsNullOrWhiteSpace(_contentRootPath)) return;
        var dir = Path.Combine(_contentRootPath, "exports");
        Directory.CreateDirectory(dir);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(dir, "single-market-arb-opportunities-latest.json"), JsonSerializer.Serialize(_state.SingleMarketOpportunities().TakeLast(_options is null ? 200 : 200), jsonOptions));
        File.WriteAllText(Path.Combine(dir, "single-market-paper-executions-latest.json"), JsonSerializer.Serialize(_state.SingleMarketExecutions().TakeLast(100), jsonOptions));
    }

    private sealed record MarketStability(int EdgeScans = 0, int ExecutionScans = 0);
    private record SingleMarketScanResult(bool BookOk,bool BothAsks,bool Candidate,bool Executed,decimal? AdjustedCost,string? Question,decimal? Edge,string? SkipReason,NearMissOpportunity? NearMiss)
    { public static SingleMarketScanResult Empty => new(false,false,false,false,null,null,null,null,null); }
}
