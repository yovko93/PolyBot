using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Engines;

public class TrueArbitrageEngine
{
    private readonly Dictionary<string, DateTime> _recentOpportunities = new();
    private readonly TimeSpan _cooldown = TimeSpan.FromMinutes(5);

    private readonly IOrderBookProvider _orderBooks;
    private readonly SemanticMarketMatcher _matcher;

    private readonly decimal _minEdgePerShare;
    private readonly decimal _feeBuffer;
    private readonly decimal _slippageBuffer;
    private readonly OpportunityMonitor? _monitor;
    private readonly ExecutionSizingService _sizing;
    private readonly bool _sizingLogsEnabled;

    public TrueArbitrageEngine(
        IOrderBookProvider orderBooks,
        SemanticMarketMatcher matcher,
        decimal minEdgePerShare = 0.01m,
        decimal feeBuffer = 0.002m,
        decimal slippageBuffer = 0.002m,
        OpportunityMonitor? monitor = null,
        ExecutionSizingService? sizing = null)
    {
        _orderBooks = orderBooks;
        _matcher = matcher;
        _minEdgePerShare = minEdgePerShare;
        _feeBuffer = feeBuffer;
        _slippageBuffer = slippageBuffer;
        _monitor = monitor;
        _sizing = sizing ?? new ExecutionSizingService(new ExecutionPolicy());
        _sizingLogsEnabled = _sizing.EnableSizingLogs;
    }

    public async Task ScanAsync(
        List<Market> markets,
        PaperTradingEngine paper,
        SemaphoreSlim semaphore,
        CancellationToken ct = default)
    {
        var pairs = _matcher
            .FindEquivalentBinaryMarkets(markets)
            .Take(200)
            .ToList();

        if (pairs.Count == 0)
        {
            Console.WriteLine("[TRUE ARB] No semantic pairs found.");
            return;
        }

        var tasks = pairs.Select(pair => ScanPairAsync(pair, paper, semaphore, ct));
        await Task.WhenAll(tasks);
    }

    private async Task ScanPairAsync(
        SemanticMarketPair pair,
        PaperTradingEngine paper,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);

        try
        {
            var bookA = await _orderBooks.GetBinarySnapshotAsync(pair.A, ct);
            var bookB = await _orderBooks.GetBinarySnapshotAsync(pair.B, ct);

            if (bookA == null || bookB == null)
                return;

            CheckBuyYesBuyNo(bookA, bookB, pair.Score, paper);
            CheckBuyYesBuyNo(bookB, bookA, pair.Score, paper);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TRUE ARB ERROR] {ex.Message}");
        }
        finally
        {
            semaphore.Release();
        }
    }

    private void CheckBuyYesBuyNo(
        BinaryOrderBookSnapshot yesMarket,
        BinaryOrderBookSnapshot noMarket,
        double semanticScore,
        PaperTradingEngine paper)
    {
        if (yesMarket.YesAsk == null || noMarket.NoAsk == null)
            return;

        var yesAsk = yesMarket.YesAsk;
        var noAsk = noMarket.NoAsk;

        if (yesAsk.Price <= 0 || noAsk.Price <= 0)
            return;

        var rawCost = yesAsk.Price + noAsk.Price;
        var adjustedCost = rawCost + _feeBuffer + _slippageBuffer;

        var edge = 1m - adjustedCost;

        var quantityAvailable = Math.Min(yesAsk.Size, noAsk.Size);
        var sizing = _sizing.SizeByNotional(quantityAvailable, adjustedCost);
        var quantity = sizing.ExecutableQuantity;

        if (_sizingLogsEnabled && edge >= _minEdgePerShare && quantity > 0m && sizing.WasClamped)
            Console.WriteLine($"[SIZING] Strategy=BUY_YES_MARKET_A_BUY_NO_MARKET_B AvailableQty={sizing.QuantityAvailable:0.####} ExecutableQty={sizing.ExecutableQuantity:0.####} Notional={sizing.Notional:0.####} MaxNotional={sizing.MaxNotional:0.####} Edge={edge:0.####}");

        var orderLegs = new List<OrderLegCandidate>
        {
            new OrderLegCandidate(
                Strategy: "BUY_YES_MARKET_A_BUY_NO_MARKET_B",
                GroupKey: $"semantic-score:{semanticScore:0.0000}",
                Question: yesMarket.Question,
                TokenId: yesMarket.YesTokenId,
                Outcome: "YES",
                Side: LiveOrderSide.BUY,
                Price: yesAsk.Price,
                Size: quantity,
                EdgePerShare: edge
            ),

            new OrderLegCandidate(
                Strategy: "BUY_YES_MARKET_A_BUY_NO_MARKET_B",
                GroupKey: $"semantic-score:{semanticScore:0.0000}",
                Question: noMarket.Question,
                TokenId: noMarket.NoTokenId,
                Outcome: "NO",
                Side: LiveOrderSide.BUY,
                Price: noAsk.Price,
                Size: quantity,
                EdgePerShare: edge
            )
        };

        _monitor?.Record(new ArbMonitorRecord(
            TimestampUtc: DateTime.UtcNow,
            Engine: "TrueSemanticCrossMarket",
            Strategy: "BUY_YES_MARKET_A_BUY_NO_MARKET_B",
            Key: $"{yesMarket.MarketId}|YES|{noMarket.MarketId}|NO",
            EdgePerShare: edge,
            CostOrProceeds: adjustedCost,
            GuaranteedPayout: 1m,
            QuantityAvailable: quantity,
            ExpectedProfit: quantity * edge,
            IsExecutable: edge >= _minEdgePerShare && quantity > 0,
            Leg1: $"BUY YES @ {yesAsk.Price} | {yesMarket.Question}",
            Leg2: $"BUY NO @ {noAsk.Price} | {noMarket.Question}",
            GroupKey: $"semantic-score:{semanticScore:0.0000}"
        )
        {
            OrderLegs = orderLegs
        });


        if (edge < _minEdgePerShare)
            return;

        if (quantity <= 0)
            return;

        var expectedProfit = quantity * edge;

        var opportunity = new ArbOpportunity(
            Leg1: new ArbLeg(
                MarketId: yesMarket.MarketId,
                Question: yesMarket.Question,
                Outcome: "YES",
                Price: yesAsk.Price,
                Size: yesAsk.Size
            ),
            Leg2: new ArbLeg(
                MarketId: noMarket.MarketId,
                Question: noMarket.Question,
                Outcome: "NO",
                Price: noAsk.Price,
                Size: noAsk.Size
            ),
            Quantity: quantity,
            CostPerShare: adjustedCost,
            GrossEdgePerShare: edge,
            ExpectedProfit: expectedProfit,
            SemanticScore: semanticScore
        );

        var opportunityKey = BuildOpportunityKey(opportunity);

        if (IsOnCooldown(opportunityKey))
            return;

        var executed = paper.RecordArbitrage(opportunity);

        if (!executed)
            return;

        MarkCooldown(opportunityKey);

        paper.RecordArbitrage(opportunity);

        Console.WriteLine();
        Console.WriteLine("========== TRUE ARB FOUND ==========");
        Console.WriteLine($"Semantic score: {semanticScore:P2}");
        Console.WriteLine($"BUY YES: {yesMarket.Question}");
        Console.WriteLine($"Price: {yesAsk.Price}, Size: {yesAsk.Size}");
        Console.WriteLine($"BUY NO : {noMarket.Question}");
        Console.WriteLine($"Price: {noAsk.Price}, Size: {noAsk.Size}");
        Console.WriteLine($"Cost/share: {adjustedCost}");
        Console.WriteLine($"Edge/share: {edge}");
        Console.WriteLine($"Quantity: {quantity}");
        Console.WriteLine($"Expected profit: {expectedProfit}");
        Console.WriteLine("====================================");
        Console.WriteLine();
    }

    private bool IsOnCooldown(string key)
    {
        lock (_recentOpportunities)
        {
            if (!_recentOpportunities.TryGetValue(key, out var lastSeen))
                return false;

            return DateTime.UtcNow - lastSeen < _cooldown;
        }
    }

    private void MarkCooldown(string key)
    {
        lock (_recentOpportunities)
        {
            _recentOpportunities[key] = DateTime.UtcNow;
        }
    }

    private static string BuildOpportunityKey(ArbOpportunity opportunity)
    {
        return string.Join("|",
            opportunity.Leg1.MarketId,
            opportunity.Leg1.Outcome,
            opportunity.Leg2.MarketId,
            opportunity.Leg2.Outcome
        );
    }
}
