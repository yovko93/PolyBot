using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Engines;

public class CompleteSetSellArbEngine
{
    private readonly IOrderBookProvider _orderBooks;
    private readonly decimal _minEdgePerShare;
    private readonly decimal _feeBuffer;
    private readonly decimal _slippageBuffer;
    private readonly OpportunityMonitor? _monitor;

    public CompleteSetSellArbEngine(
        IOrderBookProvider orderBooks,
        decimal minEdgePerShare = 0.003m,
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
        var bothBids = results.Count(x => x.BothBids);
        var candidates = results.Count(x => x.Candidate);
        var executed = results.Count(x => x.Executed);

        var top = results
            .Where(x => x.AdjustedProceeds.HasValue)
            .OrderByDescending(x => x.AdjustedProceeds!.Value)
            .Take(10)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("========== COMPLETE SET SELL SCAN ==========");
        Console.WriteLine($"Scanned: {scanned}");
        Console.WriteLine($"Book OK: {bookOk}");
        Console.WriteLine($"Both YES/NO bids: {bothBids}");
        Console.WriteLine($"Candidates: {candidates}");
        Console.WriteLine($"Executed: {executed}");

        //if (top.Count > 0)
        //{
        //    Console.WriteLine();
        //    Console.WriteLine("Top closest YES+NO bid markets:");

        //    foreach (var item in top)
        //    {
        //        var edge = item.AdjustedProceeds!.Value - 1m;

        //        Console.WriteLine("----------------------------------------");
        //        Console.WriteLine($"Adjusted proceeds: {item.AdjustedProceeds.Value:0.####} | Edge: {edge:0.####}");
        //        Console.WriteLine($"Market: {item.Question}");
        //    }
        //}

        Console.WriteLine("==========================================");
        Console.WriteLine();
    }

    private async Task<CompleteSetSellScanResult> ScanMarketAsync(
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
                return CompleteSetSellScanResult.Empty;

            if (book.YesBid == null || book.NoBid == null)
            {
                return new CompleteSetSellScanResult(
                    BookOk: true,
                    BothBids: false,
                    Candidate: false,
                    Executed: false,
                    AdjustedProceeds: null,
                    Question: book.Question
                );
            }

            var rawProceeds = book.YesBid.Price + book.NoBid.Price;
            var adjustedProceeds = rawProceeds - _feeBuffer - _slippageBuffer;
            var edge = adjustedProceeds - 1m;

            var quantityAvailable = Math.Min(book.YesBid.Size, book.NoBid.Size);

            var orderLegs = new List<OrderLegCandidate>
            {
                new OrderLegCandidate(
                    Strategy: "MINT_AND_SELL_YES_NO",
                    GroupKey: book.MarketId,
                    Question: book.Question,
                    TokenId: book.YesTokenId,
                    Outcome: "YES",
                    Side: LiveOrderSide.SELL,
                    Price: book.YesBid.Price,
                    Size: quantityAvailable,
                    EdgePerShare: edge
                ),

                new OrderLegCandidate(
                    Strategy: "MINT_AND_SELL_YES_NO",
                    GroupKey: book.MarketId,
                    Question: book.Question,
                    TokenId: book.NoTokenId,
                    Outcome: "NO",
                    Side: LiveOrderSide.SELL,
                    Price: book.NoBid.Price,
                    Size: quantityAvailable,
                    EdgePerShare: edge
                )
            };

            _monitor?.Record(new ArbMonitorRecord(
                TimestampUtc: DateTime.UtcNow,
                Engine: "CompleteSetSell",
                Strategy: "MINT_AND_SELL_YES_NO",
                Key: book.MarketId,
                EdgePerShare: edge,
                CostOrProceeds: adjustedProceeds,
                GuaranteedPayout: 1m,
                QuantityAvailable: quantityAvailable,
                ExpectedProfit: quantityAvailable * edge,
                IsExecutable: edge >= _minEdgePerShare && quantityAvailable > 0,
                Leg1: $"SELL YES @ {book.YesBid.Price} | {book.Question}",
                Leg2: $"SELL NO @ {book.NoBid.Price} | {book.Question}",
                GroupKey: null
            )
            {
                OrderLegs = orderLegs
            });

            if (edge < _minEdgePerShare)
            {
                return new CompleteSetSellScanResult(
                    BookOk: true,
                    BothBids: true,
                    Candidate: false,
                    Executed: false,
                    AdjustedProceeds: adjustedProceeds,
                    Question: book.Question
                );
            }

            var quantity = quantityAvailable;

            if (quantity <= 0)
            {
                return new CompleteSetSellScanResult(
                    BookOk: true,
                    BothBids: true,
                    Candidate: true,
                    Executed: false,
                    AdjustedProceeds: adjustedProceeds,
                    Question: book.Question
                );
            }

            var opportunity = new ArbOpportunity(
                Leg1: new ArbLeg(
                    MarketId: book.MarketId,
                    Question: book.Question,
                    Outcome: "SELL_YES",
                    Price: book.YesBid.Price,
                    Size: book.YesBid.Size
                ),
                Leg2: new ArbLeg(
                    MarketId: book.MarketId,
                    Question: book.Question,
                    Outcome: "SELL_NO",
                    Price: book.NoBid.Price,
                    Size: book.NoBid.Size
                ),
                Quantity: quantity,
                CostPerShare: 1m,
                GrossEdgePerShare: edge,
                ExpectedProfit: quantity * edge,
                SemanticScore: 1.0
            );

            var executed = paper.RecordCompleteSetSellArbitrage(opportunity);

            if (executed)
            {
                Console.WriteLine();
                Console.WriteLine("========== COMPLETE SET SELL ARB ==========");
                Console.WriteLine(book.Question);
                Console.WriteLine($"SELL YES bid: {book.YesBid.Price} | Size: {book.YesBid.Size}");
                Console.WriteLine($"SELL NO bid : {book.NoBid.Price} | Size: {book.NoBid.Size}");
                Console.WriteLine($"Raw proceeds/share: {rawProceeds}");
                Console.WriteLine($"Adjusted proceeds/share: {adjustedProceeds}");
                Console.WriteLine($"Mint cost/share: 1.000");
                Console.WriteLine($"Edge/share: {edge}");
                Console.WriteLine($"Quantity available: {quantity}");
                Console.WriteLine("===========================================");
                Console.WriteLine();
            }

            return new CompleteSetSellScanResult(
                BookOk: true,
                BothBids: true,
                Candidate: true,
                Executed: executed,
                AdjustedProceeds: adjustedProceeds,
                Question: book.Question
            );
        }
        catch
        {
            return CompleteSetSellScanResult.Empty;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private record CompleteSetSellScanResult(
        bool BookOk,
        bool BothBids,
        bool Candidate,
        bool Executed,
        decimal? AdjustedProceeds,
        string? Question)
    {
        public static CompleteSetSellScanResult Empty =>
            new(
                BookOk: false,
                BothBids: false,
                Candidate: false,
                Executed: false,
                AdjustedProceeds: null,
                Question: null
            );
    }
}