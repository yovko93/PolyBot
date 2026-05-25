using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Engines;

public record SingleMarketScanStats(int Scanned,int BookOk,int BothAsks,int Candidates,int Executed,int PositiveEdgeFound,int NegativeEdgeSkipped,int ZeroEdgeSkipped, Dictionary<string,int>? SkipReasons = null, List<NearMissOpportunity>? NearMisses = null, decimal? BestEdgeSeen = null, decimal? WorstEdgeSeen = null);

public class SingleMarketOrderBookArbEngine
{
    private readonly IOrderBookProvider _orderBooks;
    private readonly decimal _minEdgePerShare;
    private readonly decimal _feeBuffer;
    private readonly decimal _slippageBuffer;
    private readonly OpportunityMonitor? _monitor;
    private readonly ExecutionSizingService _sizing;
    private readonly bool _sizingLogsEnabled;

    public SingleMarketOrderBookArbEngine(
        IOrderBookProvider orderBooks,
        decimal minEdgePerShare = 0.005m,
        decimal feeBuffer = 0.001m,
        decimal slippageBuffer = 0.001m,
        OpportunityMonitor? monitor = null,
        ExecutionSizingService? sizing = null)
    {
        _orderBooks = orderBooks;
        _minEdgePerShare = minEdgePerShare;
        _feeBuffer = feeBuffer;
        _slippageBuffer = slippageBuffer;
        _monitor = monitor;
        _sizing = sizing ?? new ExecutionSizingService(new ExecutionPolicy());
        _sizingLogsEnabled = _sizing.EnableSizingLogs;
    }

    public async Task<SingleMarketScanStats> ScanAsync(
        List<Market> markets,
        PaperTradingEngine paper,
        SemaphoreSlim semaphore,
        CancellationToken ct = default)
    {
        var tasks = markets.Select(market => ScanMarketAsync(market, paper, semaphore, ct));

        var results = await Task.WhenAll(tasks);
        var skipReasons = new Dictionary<string, int>();
        var nearMisses = new List<NearMissOpportunity>();
        var edges = new List<decimal>();

        var scanned = results.Length;
        var bookOk = results.Count(x => x.BookOk);
        var bothAsks = results.Count(x => x.BothAsks);
        var candidates = results.Count(x => x.Candidate);
        var executed = results.Count(x => x.Executed);

        var positiveEdge = results.Count(x => x.AdjustedCost.HasValue && 1m - x.AdjustedCost.Value > 0m);
        var negativeEdgeSkipped = results.Count(x => x.AdjustedCost.HasValue && 1m - x.AdjustedCost.Value < 0m);
        var zeroEdgeSkipped = results.Count(x => x.AdjustedCost.HasValue && 1m - x.AdjustedCost.Value == 0m);
        foreach (var r in results)
        {
            if (r.Edge.HasValue) edges.Add(r.Edge.Value);
            if (!string.IsNullOrWhiteSpace(r.SkipReason)) skipReasons[r.SkipReason!] = skipReasons.GetValueOrDefault(r.SkipReason!, 0) + 1;
            if (r.NearMiss != null) nearMisses.Add(r.NearMiss);
        }

        return new SingleMarketScanStats(scanned, bookOk, bothAsks, candidates, executed, positiveEdge, negativeEdgeSkipped, zeroEdgeSkipped, skipReasons, nearMisses, edges.Count>0?edges.Max():null, edges.Count>0?edges.Min():null);
    }

    private async Task<SingleMarketScanResult> ScanMarketAsync(
        Market market,
        PaperTradingEngine paper,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);

        try
        {
            var book = await _orderBooks.GetBinarySnapshotAsync(market, ct);

            if (book == null)
                return SingleMarketScanResult.Empty;

            if (book.YesAsk == null || book.NoAsk == null)
                return new SingleMarketScanResult(
                    BookOk: true,
                    BothAsks: false,
                    Candidate: false,
                    Executed: false,
                    AdjustedCost: null,
                    Question: book.Question,
                    Edge: null,
                    SkipReason: book.YesAsk == null ? OpportunitySkipReason.MissingYesAsk.ToString() : OpportunitySkipReason.MissingNoAsk.ToString(),
                    NearMiss: null
                );

            var rawCost = book.YesAsk.Price + book.NoAsk.Price;
            var adjustedCost = rawCost + _feeBuffer + _slippageBuffer;
            var edge = 1m - adjustedCost;
            if (book.YesAsk.Price is < 0m or > 1m || book.NoAsk.Price is < 0m or > 1m)
            {
                return new SingleMarketScanResult(true, true, false, false, adjustedCost, book.Question, edge, OpportunitySkipReason.InvalidPriceNormalization.ToString(), null);
            }

            //todo remove the guard
            if (rawCost < 0.90m)
            {
                Console.WriteLine();
                Console.WriteLine("[DATA QUALITY WARNING] Suspicious YES+NO ask sum.");
                Console.WriteLine($"Market: {book.Question}");
                Console.WriteLine($"YES ask: {book.YesAsk.Price}");
                Console.WriteLine($"NO ask : {book.NoAsk.Price}");
                Console.WriteLine($"Raw sum: {rawCost}");
                Console.WriteLine("Skipping this market until token/book mapping is verified.");
                Console.WriteLine();

                return new SingleMarketScanResult(
                    true,
                    true,
                    false,
                    false,
                    adjustedCost,
                    book.Question,
                    edge,
                    OpportunitySkipReason.BelowMinEdgeThreshold.ToString(),
                    edge < 0
                        ? new NearMissOpportunity(
                            book.MarketId,
                            book.Question,
                            "BUY_YES_AND_BUY_NO",
                            book.YesAsk.Price,
                            book.NoAsk.Price,
                            rawCost,
                            1m - rawCost,
                            _feeBuffer,
                            _slippageBuffer,
                            0m,
                            adjustedCost,
                            edge,
                            0m,
                            0m,
                            OpportunitySkipReason.NegativeEdge.ToString(),
                            Math.Max(0m, rawCost - 1m),
                            Math.Max(0m, adjustedCost - 1m),
                            0)
                        : null);
            }

            var quantityAvailable = Math.Min(book.YesAsk.Size, book.NoAsk.Size);
            var sizing = _sizing.SizeByNotional(quantityAvailable, adjustedCost);
            var executableQuantity = sizing.ExecutableQuantity;
            if (_sizingLogsEnabled && edge >= _minEdgePerShare && executableQuantity > 0m && sizing.WasClamped)
                Console.WriteLine($"[SIZING] Strategy=BUY_YES_AND_BUY_NO AvailableQty={sizing.QuantityAvailable:0.####} ExecutableQty={sizing.ExecutableQuantity:0.####} Notional={sizing.Notional:0.####} MaxNotional={sizing.MaxNotional:0.####} Edge={edge:0.####}");

            var orderLegs = new List<OrderLegCandidate>
            {
                new OrderLegCandidate(
                    Strategy: "BUY_YES_AND_BUY_NO",
                    GroupKey: book.MarketId,
                    Question: book.Question,
                    TokenId: book.YesTokenId,
                    Outcome: "YES",
                    Side: LiveOrderSide.BUY,
                    Price: book.YesAsk.Price,
                    Size: executableQuantity,
                    EdgePerShare: edge
                ),

                new OrderLegCandidate(
                    Strategy: "BUY_YES_AND_BUY_NO",
                    GroupKey: book.MarketId,
                    Question: book.Question,
                    TokenId: book.NoTokenId,
                    Outcome: "NO",
                    Side: LiveOrderSide.BUY,
                    Price: book.NoAsk.Price,
                    Size: executableQuantity,
                    EdgePerShare: edge
                )
            };

            _monitor?.Record(new ArbMonitorRecord(
                TimestampUtc: DateTime.UtcNow,
                Engine: "SingleMarketBuyBoth",
                Strategy: "BUY_YES_AND_BUY_NO",
                Key: book.MarketId,
                EdgePerShare: edge,
                CostOrProceeds: adjustedCost,
                GuaranteedPayout: 1m,
                QuantityAvailable: executableQuantity,
                ExpectedProfit: executableQuantity * edge,
                IsExecutable: edge >= _minEdgePerShare && executableQuantity > 0,
                Leg1: $"BUY YES @ {book.YesAsk.Price} | {book.Question}",
                Leg2: $"BUY NO @ {book.NoAsk.Price} | {book.Question}",
                GroupKey: null
            )
            {
                OrderLegs = orderLegs
            });

            if (edge < _minEdgePerShare)
            {
                return new SingleMarketScanResult(true,true,true,false,adjustedCost,book.Question,edge,OpportunitySkipReason.NegativeEdge.ToString(),null);
            }

            var quantity = executableQuantity;

            if (quantity <= 0)
            {
                return new SingleMarketScanResult(
                    BookOk: true,
                    BothAsks: true,
                    Candidate: true,
                    Executed: false,
                    AdjustedCost: adjustedCost,
                    Question: book.Question,
                    Edge: edge,
                    SkipReason: OpportunitySkipReason.InsufficientLiquidity.ToString(),
                    NearMiss: null
                );
            }

            var opportunity = new ArbOpportunity(
                Leg1: new ArbLeg(
                    MarketId: book.MarketId,
                    Question: book.Question,
                    Outcome: "YES",
                    Price: book.YesAsk.Price,
                    Size: book.YesAsk.Size
                ),
                Leg2: new ArbLeg(
                    MarketId: book.MarketId,
                    Question: book.Question,
                    Outcome: "NO",
                    Price: book.NoAsk.Price,
                    Size: book.NoAsk.Size
                ),
                Quantity: quantity,
                CostPerShare: adjustedCost,
                GrossEdgePerShare: edge,
                ExpectedProfit: quantity * edge,
                SemanticScore: 1.0
            );

            var executed = paper.RecordArbitrage(opportunity);

            if (executed)
            {
                Console.WriteLine();
                Console.WriteLine("========== SINGLE MARKET ORDERBOOK ARB ==========");
                Console.WriteLine(book.Question);
                Console.WriteLine($"YES ask: {book.YesAsk.Price} | Size: {book.YesAsk.Size}");
                Console.WriteLine($"NO ask : {book.NoAsk.Price} | Size: {book.NoAsk.Size}");
                Console.WriteLine($"Raw cost/share: {rawCost}");
                Console.WriteLine($"Adjusted cost/share: {adjustedCost}");
                Console.WriteLine($"Edge/share: {edge}");
                Console.WriteLine($"Quantity available: {quantity}");
                Console.WriteLine("=================================================");
                Console.WriteLine();
            }

            return new SingleMarketScanResult(true,true,true,executed,adjustedCost,book.Question,edge,executed?null:OpportunitySkipReason.AlreadyExecuted.ToString(),null);
        }
        catch
        {
            return SingleMarketScanResult.Empty;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private record SingleMarketScanResult(
        bool BookOk,
        bool BothAsks,
        bool Candidate,
        bool Executed,
        decimal? AdjustedCost,
        string? Question,
        decimal? Edge,
        string? SkipReason,
        NearMissOpportunity? NearMiss)
    {
        public static SingleMarketScanResult Empty =>
            new(
                BookOk: false,
                BothAsks: false,
                Candidate: false,
                Executed: false,
                AdjustedCost: null,
                Question: null,
                Edge: null,
                SkipReason: null,
                NearMiss: null
            );
    }
}
