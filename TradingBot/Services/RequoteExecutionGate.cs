using TradingBot.Models;

namespace TradingBot.Services;

public class RequoteExecutionGate
{
    private readonly OrderBookDepthSimulator _depth;

    public RequoteExecutionGate(OrderBookDepthSimulator depth)
    {
        _depth = depth;
    }

    public async Task<bool> ValidateBasketNoAsync(
        BasketArbOpportunity opportunity,
        List<Market> sourceMarkets,
        decimal minEdgePerShare,
        decimal minExpectedProfit,
        decimal feeBufferPerLeg,
        decimal slippageBufferPerLeg,
        CancellationToken ct = default)
    {
        if (sourceMarkets.Count < 2)
            return false;

        var targetQuantity = opportunity.Quantity;

        if (targetQuantity <= 0)
            return false;

        var fills = new List<OrderBookFillSimulationResult>();

        foreach (var market in sourceMarkets)
        {
            var fill = await _depth.SimulateBuyAsync(
                market,
                outcome: "NO",
                targetQuantity: targetQuantity,
                ct
            );

            if (fill == null)
                return false;

            if (!fill.IsComplete)
            {
                Console.WriteLine(
                    $"[DEPTH SKIP] Not enough NO depth. " +
                    $"Market={market.question}, " +
                    $"Requested={targetQuantity:0.####}, " +
                    $"Filled={fill.FilledQuantity:0.####}"
                );

                return false;
            }

            fills.Add(fill);
        }

        var n = fills.Count;

        var rawCost = fills.Sum(x => x.AveragePrice);
        var buffer = n * (feeBufferPerLeg + slippageBufferPerLeg);
        var adjustedCost = rawCost + buffer;

        var guaranteedPayout = n - 1m;
        var edge = guaranteedPayout - adjustedCost;
        var expectedProfit = targetQuantity * edge;

        if (edge < minEdgePerShare)
        {
            Console.WriteLine(
                $"[DEPTH SKIP] Basket edge disappeared. " +
                $"Edge={edge:0.####}, Min={minEdgePerShare:0.####}"
            );

            return false;
        }

        if (expectedProfit < minExpectedProfit)
        {
            Console.WriteLine(
                $"[DEPTH SKIP] Basket expected profit too small. " +
                $"ExpectedProfit={expectedProfit:0.####}, Min={minExpectedProfit:0.####}"
            );

            return false;
        }

        Console.WriteLine(
            $"[DEPTH OK] Basket NO requote passed. " +
            $"Qty={targetQuantity:0.####}, " +
            $"AdjustedCost={adjustedCost:0.####}, " +
            $"Guaranteed={guaranteedPayout:0.####}, " +
            $"Edge={edge:0.####}, " +
            $"ExpectedProfit={expectedProfit:0.####}"
        );

        return true;
    }

    public async Task<bool> ValidateSingleMarketBuyBothAsync(
        Market market,
        decimal targetQuantity,
        decimal minEdgePerShare,
        decimal minExpectedProfit,
        decimal feeBuffer,
        decimal slippageBuffer,
        CancellationToken ct = default)
    {
        var yesFill = await _depth.SimulateBuyAsync(market, "YES", targetQuantity, ct);
        var noFill = await _depth.SimulateBuyAsync(market, "NO", targetQuantity, ct);

        if (yesFill == null || noFill == null)
            return false;

        if (!yesFill.IsComplete || !noFill.IsComplete)
            return false;

        var cost = yesFill.AveragePrice + noFill.AveragePrice + feeBuffer + slippageBuffer;
        var edge = 1m - cost;
        var expectedProfit = targetQuantity * edge;

        return edge >= minEdgePerShare && expectedProfit >= minExpectedProfit;
    }

    public async Task<bool> ValidateCompleteSetSellAsync(
        Market market,
        decimal targetQuantity,
        decimal minEdgePerShare,
        decimal minExpectedProfit,
        decimal feeBuffer,
        decimal slippageBuffer,
        CancellationToken ct = default)
    {
        var yesFill = await _depth.SimulateSellAsync(market, "YES", targetQuantity, ct);
        var noFill = await _depth.SimulateSellAsync(market, "NO", targetQuantity, ct);

        if (yesFill == null || noFill == null)
            return false;

        if (!yesFill.IsComplete || !noFill.IsComplete)
            return false;

        var proceeds = yesFill.AveragePrice + noFill.AveragePrice - feeBuffer - slippageBuffer;
        var edge = proceeds - 1m;
        var expectedProfit = targetQuantity * edge;

        return edge >= minEdgePerShare && expectedProfit >= minExpectedProfit;
    }
}