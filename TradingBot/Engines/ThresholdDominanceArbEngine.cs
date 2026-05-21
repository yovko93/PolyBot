using System.Text.RegularExpressions;
using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Engines;

public class ThresholdDominanceArbEngine
{
    private readonly IOrderBookProvider _orderBooks;
    private readonly decimal _minEdgePerShare;
    private readonly decimal _feeBuffer;
    private readonly decimal _slippageBuffer;
    private readonly OpportunityMonitor? _monitor;
    private readonly ExecutionSizingService _sizing;
    private readonly bool _sizingLogsEnabled;

    public ThresholdDominanceArbEngine(
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

    public async Task ScanAsync(
        List<Market> markets,
        PaperTradingEngine paper,
        SemaphoreSlim semaphore,
        CancellationToken ct = default)
    {
        var thresholdMarkets = markets
            .Select(m => new
            {
                Market = m,
                Threshold = ExtractThreshold(m.question)
            })
            .Where(x => x.Threshold != null)
            .ToList();

        var groups = thresholdMarkets
            .GroupBy(x => $"{x.Threshold!.BaseKey}|{x.Threshold.Direction}|{x.Threshold.Unit}")
            .Where(g => g.Count() >= 2)
            .ToList();

        var pairs = new List<(Market Low, Market High, ThresholdInfo LowInfo, ThresholdInfo HighInfo)>();

        foreach (var group in groups)
        {
            var sorted = group
                .OrderBy(x => x.Threshold!.Value)
                .ToList();

            for (int i = 0; i < sorted.Count; i++)
            {
                for (int j = i + 1; j < sorted.Count; j++)
                {
                    var low = sorted[i];
                    var high = sorted[j];

                    pairs.Add((
                        Low: low.Market,
                        High: high.Market,
                        LowInfo: low.Threshold!,
                        HighInfo: high.Threshold!
                    ));
                }
            }
        }

        var tasks = pairs.Select(pair => ScanPairAsync(pair, paper, semaphore, ct));
        var results = await Task.WhenAll(tasks);

        var candidates = results.Count(x => x.Candidate);
        var executed = results.Count(x => x.Executed);

        var top = results
            .Where(x => x.AdjustedCost.HasValue)
            .OrderBy(x => x.AdjustedCost!.Value)
            .Take(10)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("========== THRESHOLD DOMINANCE SCAN ==========");
        Console.WriteLine($"Threshold markets: {thresholdMarkets.Count}");
        Console.WriteLine($"Comparable groups: {groups.Count}");
        Console.WriteLine($"Comparable pairs: {pairs.Count}");
        Console.WriteLine($"Candidates: {candidates}");
        Console.WriteLine($"Executed: {executed}");

        //if (top.Count > 0)
        //{
        //    Console.WriteLine();
        //    Console.WriteLine("Top closest threshold pairs:");

        //    foreach (var item in top)
        //    {
        //        var edge = 1m - item.AdjustedCost!.Value;

        //        Console.WriteLine("----------------------------------------");
        //        Console.WriteLine($"Cost: {item.AdjustedCost.Value:0.####} | Edge: {edge:0.####}");
        //        Console.WriteLine($"Leg 1: {item.Leg1}");
        //        Console.WriteLine($"Leg 2: {item.Leg2}");
        //    }
        //}

        Console.WriteLine("==============================================");
        Console.WriteLine();
    }

    private async Task<ThresholdScanResult> ScanPairAsync(
        (Market Low, Market High, ThresholdInfo LowInfo, ThresholdInfo HighInfo) pair,
        PaperTradingEngine paper,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);

        try
        {
            var lowBookTask = _orderBooks.GetBinarySnapshotAsync(pair.Low, ct);
            var highBookTask = _orderBooks.GetBinarySnapshotAsync(pair.High, ct);

            await Task.WhenAll(lowBookTask, highBookTask);

            var lowBook = lowBookTask.Result;
            var highBook = highBookTask.Result;

            if (lowBook == null || highBook == null)
                return ThresholdScanResult.Empty;

            BookQuote? yesLeg;
            BookQuote? noLeg;
            BinaryOrderBookSnapshot yesMarket;
            BinaryOrderBookSnapshot noMarket;

            if (pair.LowInfo.Direction == ThresholdDirection.GreaterThan)
            {
                // For > thresholds:
                // BUY YES lower threshold + BUY NO higher threshold
                yesLeg = lowBook.YesAsk;
                noLeg = highBook.NoAsk;
                yesMarket = lowBook;
                noMarket = highBook;
            }
            else
            {
                // For < thresholds:
                // BUY YES higher threshold + BUY NO lower threshold
                yesLeg = highBook.YesAsk;
                noLeg = lowBook.NoAsk;
                yesMarket = highBook;
                noMarket = lowBook;
            }

            if (yesLeg == null || noLeg == null)
                return ThresholdScanResult.Empty;

            var rawCost = yesLeg.Price + noLeg.Price;
            var adjustedCost = rawCost + _feeBuffer + _slippageBuffer;
            var edge = 1m - adjustedCost;

            var quantityAvailable = Math.Min(yesLeg.Size, noLeg.Size);
            var sizing = _sizing.SizeByNotional(quantityAvailable, adjustedCost);
            var executableQuantity = sizing.ExecutableQuantity;

            var thresholdKey =
                $"{yesMarket.MarketId}|YES|{noMarket.MarketId}|NO";

            var strategy = pair.LowInfo.Direction == ThresholdDirection.GreaterThan
                ? "BUY_YES_LOWER_BUY_NO_HIGHER"
                : "BUY_YES_HIGHER_BUY_NO_LOWER";

            if (_sizingLogsEnabled && edge >= _minEdgePerShare && executableQuantity > 0m && sizing.WasClamped)
                Console.WriteLine($"[SIZING] Strategy={strategy} AvailableQty={sizing.QuantityAvailable:0.####} ExecutableQty={sizing.ExecutableQuantity:0.####} Notional={sizing.Notional:0.####} MaxNotional={sizing.MaxNotional:0.####} Edge={edge:0.####}");

            var orderLegs = new List<OrderLegCandidate>
            {
                new OrderLegCandidate(
                    Strategy: strategy,
                    GroupKey: pair.LowInfo.BaseKey,
                    Question: yesMarket.Question,
                    TokenId: yesMarket.YesTokenId,
                    Outcome: "YES",
                    Side: LiveOrderSide.BUY,
                    Price: yesLeg.Price,
                    Size: executableQuantity,
                    EdgePerShare: edge
                    ),

                new OrderLegCandidate(
                    Strategy: strategy,
                    GroupKey: pair.LowInfo.BaseKey,
                    Question: noMarket.Question,
                    TokenId: noMarket.NoTokenId,
                    Outcome: "NO",
                    Side: LiveOrderSide.BUY,
                    Price: noLeg.Price,
                    Size: executableQuantity,
                    EdgePerShare: edge
                    )
            };

            _monitor?.Record(new ArbMonitorRecord(
                TimestampUtc: DateTime.UtcNow,
                Engine: "ThresholdDominance",
                Strategy: strategy,
                Key: thresholdKey,
                EdgePerShare: edge,
                CostOrProceeds: adjustedCost,
                GuaranteedPayout: 1m,
                QuantityAvailable: executableQuantity,
                ExpectedProfit: executableQuantity * edge,
                IsExecutable: edge >= _minEdgePerShare && executableQuantity > 0,
                Leg1: $"BUY YES @ {yesLeg.Price} | {yesMarket.Question}",
                Leg2: $"BUY NO @ {noLeg.Price} | {noMarket.Question}",
                GroupKey: pair.LowInfo.BaseKey
            )
            {
                OrderLegs = orderLegs
            });

            var result = new ThresholdScanResult(
                Candidate: edge >= _minEdgePerShare,
                Executed: false,
                AdjustedCost: adjustedCost,
                Leg1: $"BUY YES: {yesMarket.Question} @ {yesLeg.Price}",
                Leg2: $"BUY NO : {noMarket.Question} @ {noLeg.Price}"
            );

            if (edge < _minEdgePerShare)
                return result;

            var quantity = executableQuantity;

            if (quantity <= 0)
                return result;

            var opportunity = new ArbOpportunity(
                Leg1: new ArbLeg(
                    MarketId: yesMarket.MarketId,
                    Question: yesMarket.Question,
                    Outcome: "YES",
                    Price: yesLeg.Price,
                    Size: yesLeg.Size
                ),
                Leg2: new ArbLeg(
                    MarketId: noMarket.MarketId,
                    Question: noMarket.Question,
                    Outcome: "NO",
                    Price: noLeg.Price,
                    Size: noLeg.Size
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
                Console.WriteLine("========== THRESHOLD DOMINANCE ARB ==========");
                Console.WriteLine($"BUY YES: {yesMarket.Question}");
                Console.WriteLine($"YES price: {yesLeg.Price} | Size: {yesLeg.Size}");
                Console.WriteLine($"BUY NO : {noMarket.Question}");
                Console.WriteLine($"NO price : {noLeg.Price} | Size: {noLeg.Size}");
                Console.WriteLine($"Raw cost/share: {rawCost}");
                Console.WriteLine($"Adjusted cost/share: {adjustedCost}");
                Console.WriteLine($"Edge/share: {edge}");
                Console.WriteLine($"Quantity available: {quantity}");
                Console.WriteLine("=============================================");
                Console.WriteLine();
            }

            return result with { Executed = executed };
        }
        catch
        {
            return ThresholdScanResult.Empty;
        }
        finally
        {
            semaphore.Release();
        }
    }

    private static ThresholdInfo? ExtractThreshold(string? question)
    {
        if (string.IsNullOrWhiteSpace(question))
            return null;

        var text = question.ToLowerInvariant();

        var greaterMatch = Regex.Match(
            text,
            @"(?<cmp>>|greater than|more than|above|over|at least)\s*\$?\s*(?<num>\d+(\.\d+)?)\s*(?<unit>k|m|b|bn|million|billion)?"
        );

        if (greaterMatch.Success)
        {
            return BuildThresholdInfo(
                question,
                greaterMatch,
                ThresholdDirection.GreaterThan
            );
        }

        var lessMatch = Regex.Match(
            text,
            @"(?<cmp><|less than|under|below|fewer than|at most)\s*\$?\s*(?<num>\d+(\.\d+)?)\s*(?<unit>k|m|b|bn|million|billion)?"
        );

        if (lessMatch.Success)
        {
            return BuildThresholdInfo(
                question,
                lessMatch,
                ThresholdDirection.LessThan
            );
        }

        return null;
    }

    private static ThresholdInfo? BuildThresholdInfo(
        string originalQuestion,
        Match match,
        ThresholdDirection direction)
    {
        if (!decimal.TryParse(match.Groups["num"].Value, out var value))
            return null;

        var unit = NormalizeUnit(match.Groups["unit"].Value);

        var normalizedValue = value * UnitMultiplier(unit);

        var baseKey = originalQuestion.ToLowerInvariant();

        baseKey = baseKey.Replace(match.Value, " THRESHOLD ");
        baseKey = Regex.Replace(baseKey, @"\s+", " ");
        baseKey = Regex.Replace(baseKey, @"[^\w\s]", "");
        baseKey = baseKey.Trim();

        return new ThresholdInfo(
            BaseKey: baseKey,
            Direction: direction,
            Value: normalizedValue,
            Unit: unit
        );
    }

    private static string NormalizeUnit(string unit)
    {
        unit = unit.ToLowerInvariant().Trim();

        return unit switch
        {
            "bn" => "b",
            "billion" => "b",
            "million" => "m",
            "" => "none",
            _ => unit
        };
    }

    private static decimal UnitMultiplier(string unit)
    {
        return unit switch
        {
            "k" => 1_000m,
            "m" => 1_000_000m,
            "b" => 1_000_000_000m,
            _ => 1m
        };
    }

    private enum ThresholdDirection
    {
        GreaterThan,
        LessThan
    }

    private record ThresholdInfo(
        string BaseKey,
        ThresholdDirection Direction,
        decimal Value,
        string Unit
    );

    private record ThresholdScanResult(
        bool Candidate,
        bool Executed,
        decimal? AdjustedCost,
        string? Leg1,
        string? Leg2)
    {
        public static ThresholdScanResult Empty =>
            new(false, false, null, null, null);
    }
}
