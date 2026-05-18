using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Engines;

public class SingleMarketOrderBookArbEngine
{
    private readonly IOrderBookProvider _orderBooks;
    private readonly decimal _minEdgePerShare;
    private readonly decimal _feeBuffer;
    private readonly decimal _slippageBuffer;
    private readonly OpportunityMonitor? _monitor;

    public SingleMarketOrderBookArbEngine(
        IOrderBookProvider orderBooks,
        decimal minEdgePerShare = 0.005m,
        decimal feeBuffer = 0.001m,
        decimal slippageBuffer = 0.001m,
        OpportunityMonitor? monitor = null)
    {
        _orderBooks = orderBooks;
        _minEdgePerShare = minEdgePerShare;
        _feeBuffer = feeBuffer;
        _slippageBuffer = slippageBuffer;
        _monitor = monitor;
    }

    public async Task ScanAsync(
        List<Market> markets,
        PaperTradingEngine paper,
        SemaphoreSlim semaphore,
        CancellationToken ct = default)
    {
        var tasks = markets.Select(market => ScanMarketAsync(market, paper, semaphore, ct));

        var results = await Task.WhenAll(tasks);

        var scanned = results.Length;
        var bookOk = results.Count(x => x.BookOk);
        var bothAsks = results.Count(x => x.BothAsks);
        var candidates = results.Count(x => x.Candidate);
        var executed = results.Count(x => x.Executed);

        var best = results
            .Where(x => x.AdjustedCost.HasValue)
            .OrderBy(x => x.AdjustedCost!.Value)
            .FirstOrDefault();

        var top = results
            .Where(x => x.AdjustedCost.HasValue)
            .OrderBy(x => x.AdjustedCost!.Value)
            .Take(10)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("========== SINGLE MARKET SCAN ==========");
        Console.WriteLine($"Scanned: {scanned}");
        Console.WriteLine($"Book OK: {bookOk}");
        Console.WriteLine($"Both YES/NO asks: {bothAsks}");
        Console.WriteLine($"Candidates: {candidates}");
        Console.WriteLine($"Executed: {executed}");

        //if (top.Count > 0)
        //{
        //    Console.WriteLine();
        //    Console.WriteLine("Top closest YES+NO markets:");

        //    foreach (var item in top)
        //    {
        //        var edge = 1m - item.AdjustedCost!.Value;

        //        Console.WriteLine("----------------------------------------");
        //        Console.WriteLine($"Cost: {item.AdjustedCost.Value:0.####} | Edge: {edge:0.####}");
        //        Console.WriteLine($"Market: {item.Question}");
        //    }
        //}

        Console.WriteLine("========================================");
        Console.WriteLine();
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
                    Question: book.Question
                );

            var rawCost = book.YesAsk.Price + book.NoAsk.Price;
            var adjustedCost = rawCost + _feeBuffer + _slippageBuffer;
            var edge = 1m - adjustedCost;

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
                    BookOk: true,
                    BothAsks: true,
                    Candidate: false,
                    Executed: false,
                    AdjustedCost: adjustedCost,
                    Question: book.Question
                );
            }

            var quantityAvailable = Math.Min(book.YesAsk.Size, book.NoAsk.Size);

            _monitor?.Record(new ArbMonitorRecord(
                TimestampUtc: DateTime.UtcNow,
                Engine: "SingleMarketBuyBoth",
                Strategy: "BUY_YES_AND_BUY_NO",
                Key: book.MarketId,
                EdgePerShare: edge,
                CostOrProceeds: adjustedCost,
                GuaranteedPayout: 1m,
                QuantityAvailable: quantityAvailable,
                ExpectedProfit: quantityAvailable * edge,
                IsExecutable: edge >= _minEdgePerShare && quantityAvailable > 0,
                Leg1: $"BUY YES @ {book.YesAsk.Price} | {book.Question}",
                Leg2: $"BUY NO @ {book.NoAsk.Price} | {book.Question}",
                GroupKey: null
            ));

            if (edge < _minEdgePerShare)
            {
                return new SingleMarketScanResult(
                    BookOk: true,
                    BothAsks: true,
                    Candidate: false,
                    Executed: false,
                    AdjustedCost: adjustedCost,
                    Question: book.Question
                );
            }

            var quantity = quantityAvailable;

            if (quantity <= 0)
            {
                return new SingleMarketScanResult(
                    BookOk: true,
                    BothAsks: true,
                    Candidate: false,
                    Executed: false,
                    AdjustedCost: adjustedCost,
                    Question: book.Question
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

            return new SingleMarketScanResult(
                BookOk: true,
                BothAsks: true,
                Candidate: true,
                Executed: executed,
                AdjustedCost: adjustedCost,
                Question: book.Question
            );
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
        string? Question)
    {
        public static SingleMarketScanResult Empty =>
            new(
                BookOk: false,
                BothAsks: false,
                Candidate: false,
                Executed: false,
                AdjustedCost: null,
                Question: null
            );
    }
}