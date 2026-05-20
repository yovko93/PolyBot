using System.Text.Json;
using TradingBot.Models;

namespace TradingBot.Services;

public sealed class DryRunLiveOrderBuilder
{
    private readonly decimal _minEdgePerShare;
    private readonly decimal _maxPlanCost;
    private readonly decimal _minSize;
    private readonly decimal _tickSize;
    private readonly LiveOrderType _orderType;
    private readonly string _logDir;
    private readonly ExecutionPolicy _policy;

    private const string ZeroBytes32 =
        "0x0000000000000000000000000000000000000000000000000000000000000000";

    public DryRunLiveOrderBuilder(
        decimal minEdgePerShare = 0.002m,
        decimal maxPlanCost = 100m,
        decimal minSize = 1m,
        decimal tickSize = 0.01m,
        LiveOrderType orderType = LiveOrderType.FOK,
        string logDir = "logs",
        ExecutionPolicy? policy = null)
    {
        _minEdgePerShare = minEdgePerShare;
        _maxPlanCost = maxPlanCost;
        _minSize = minSize;
        _tickSize = tickSize;
        _orderType = orderType;
        _logDir = logDir;
        _policy = policy ?? new ExecutionPolicy();
    }

    public DryRunLiveOrderPlan? BuildPlan(
        string planId,
        IReadOnlyCollection<OrderLegCandidate> legs,
        out List<string> errors)
    {
        errors = new List<string>();

        if (legs.Count == 0)
        {
            errors.Add("No order legs supplied.");
            return null;
        }

        var first = legs.First();

        if (first.EdgePerShare < _minEdgePerShare)
        {
            errors.Add($"Edge too small: {first.EdgePerShare:0.####}. Min required: {_minEdgePerShare:0.####}");
            return null;
        }

        var warnings = new List<string>
        {
            "DRY RUN ONLY: order is not signed and not posted.",
            "Multi-leg arbitrage is not atomic. Live execution needs legging-risk control.",
            "Validate that TokenId is the real CLOB token id before enabling live mode."
        };

        var orders = new List<DryRunLiveOrder>();
        decimal estimatedOrderCost = 0m;

        foreach (var leg in legs)
        {
            ValidateLeg(leg, errors);

            if (errors.Count > 0)
                return null;

            var cost = leg.Side == LiveOrderSide.BUY
                ? leg.Price * leg.Size
                : 0m;

            estimatedOrderCost += cost;

            var makerAmount = CalculateMakerAmount(leg);
            var takerAmount = CalculateTakerAmount(leg);

            var timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
            var salt = Random.Shared.Next(1, int.MaxValue).ToString();

            var orderPreview = new
            {
                maker = "DRY_RUN_NO_MAKER",
                signer = "DRY_RUN_NO_SIGNER",
                tokenId = leg.TokenId,
                makerAmount,
                takerAmount,
                side = leg.Side.ToString(),
                sideCode = (int)leg.Side,
                expiration = "0",
                timestamp = timestampMs,
                metadata = ZeroBytes32,
                builder = ZeroBytes32,
                signature = "DRY_RUN_NOT_SIGNED",
                salt,
                signatureType = 0
            };

            orders.Add(new DryRunLiveOrder
            {
                Question = leg.Question,
                Outcome = leg.Outcome,
                TokenId = leg.TokenId,
                Side = leg.Side.ToString(),
                SideCode = (int)leg.Side,
                Price = leg.Price,
                Size = leg.Size,
                MakerAmount = makerAmount,
                TakerAmount = takerAmount,
                OrderType = _orderType,
                PostPreview = new DryRunPostOrderPreview
                {
                    Order = orderPreview,
                    Owner = "DRY_RUN_NO_OWNER",
                    OrderType = _orderType.ToString(),
                    DeferExec = false
                }
            });
        }

        var nonOrderCapitalRequired = EstimateNonOrderCapitalRequired(legs);
        var totalCapitalRequired = estimatedOrderCost + nonOrderCapitalRequired;

        var effectiveCap = Math.Min(_maxPlanCost, _policy.MaxNotionalPerTrade);
        if (totalCapitalRequired > effectiveCap)
        {
            errors.Add($"Plan capital too high: {totalCapitalRequired:0.####}. Max allowed: {effectiveCap:0.####}");
            return null;
        }

        return new DryRunLiveOrderPlan
        {
            PlanId = planId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Strategy = first.Strategy,
            GroupKey = first.GroupKey,

            TotalEstimatedCost = estimatedOrderCost,
            EstimatedOrderCost = estimatedOrderCost,
            NonOrderCapitalRequired = nonOrderCapitalRequired,
            TotalCapitalRequired = totalCapitalRequired,

            EdgePerShare = first.EdgePerShare,
            Orders = orders,
            Warnings = warnings
        };
    }

    public void SavePlan(DryRunLiveOrderPlan plan)
    {
        Directory.CreateDirectory(_logDir);

        var file = Path.Combine(
            _logDir,
            $"dryrun-live-orders-{DateTime.UtcNow:yyyyMMdd}.jsonl");

        var json = JsonSerializer.Serialize(plan, new JsonSerializerOptions
        {
            WriteIndented = false
        });

        File.AppendAllText(file, json + Environment.NewLine);
    }

    #region Helpers
    private void ValidateLeg(OrderLegCandidate leg, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(leg.TokenId))
            errors.Add("Missing tokenId.");

        if (leg.Price <= 0m || leg.Price >= 1m)
            errors.Add($"Invalid price: {leg.Price}. Price must be between 0 and 1.");

        if (leg.Size < _minSize)
            errors.Add($"Invalid size: {leg.Size}. Min size: {_minSize}.");

        if (!IsOnTick(leg.Price))
            errors.Add($"Price {leg.Price} is not aligned to tick size {_tickSize}.");

        if (string.IsNullOrWhiteSpace(leg.Question))
            errors.Add("Missing question.");

        if (string.IsNullOrWhiteSpace(leg.Outcome))
            errors.Add("Missing outcome.");
    }

    private bool IsOnTick(decimal price)
    {
        var units = price / _tickSize;
        return units == Math.Round(units, 0);
    }

    private static string CalculateMakerAmount(OrderLegCandidate leg)
    {
        return leg.Side switch
        {
            LiveOrderSide.BUY => ToFixed6(leg.Price * leg.Size),
            LiveOrderSide.SELL => ToFixed6(leg.Size),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static string CalculateTakerAmount(OrderLegCandidate leg)
    {
        return leg.Side switch
        {
            LiveOrderSide.BUY => ToFixed6(leg.Size),
            LiveOrderSide.SELL => ToFixed6(leg.Price * leg.Size),
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private static string ToFixed6(decimal value)
    {
        var scaled = decimal.Round(value * 1_000_000m, 0, MidpointRounding.AwayFromZero);
        return scaled.ToString("0");
    }

    private static decimal EstimateNonOrderCapitalRequired(IReadOnlyCollection<OrderLegCandidate> legs)
    {
        var first = legs.FirstOrDefault();

        if (first == null)
            return 0m;

        if (first.Strategy == "MINT_AND_SELL_YES_NO")
        {
            // Mint complete set costs 1 USDC per complete set.
            return legs.Max(x => x.Size);
        }

        return 0m;
    }
    #endregion
}
