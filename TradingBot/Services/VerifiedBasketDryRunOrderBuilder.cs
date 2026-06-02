using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed class VerifiedBasketDryRunOrderBuilder
{
    private const decimal Tolerance = 0.000001m;

    public BasketOrderPlan Build(VerifiedMultiOutcomeOpportunity opp, VerifiedBasketPreTradeValidationResult preTrade, ExecutionOptions cfg)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var orders = new List<OrderIntent>();
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!cfg.EnableDryRunOrderBuilder) errors.Add("DryRunOrderBuilderDisabled");
        if (!preTrade.Approved) errors.Add(preTrade.Reason);
        if (!string.Equals(opp.VerificationStatus, "Verified", StringComparison.OrdinalIgnoreCase)) errors.Add("UnverifiedCandidateLegs");
        if (!string.Equals(opp.Strategy, "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", StringComparison.OrdinalIgnoreCase)) errors.Add("UnsupportedStrategy");

        foreach (var leg in opp.Legs)
        {
            if (string.IsNullOrWhiteSpace(leg.NoTokenId)) errors.Add("MissingNoTokenId");
            if (string.IsNullOrWhiteSpace(leg.MarketId)) errors.Add("MissingMarketId");
            if (leg.NoAsk <= 0m || leg.NoAsk > 1m) errors.Add("InvalidPrice");
            if (preTrade.Quantity <= 0m) errors.Add("InvalidQuantity");

            var id = $"{opp.Id}:{leg.MarketId}:{leg.NoTokenId}:BUY:NO";
            if (!seen.Add(id)) errors.Add("DuplicateOrderId");

            orders.Add(new OrderIntent(
                id,
                opp.Id,
                opp.GroupKey,
                opp.Strategy,
                leg.MarketId,
                leg.ConditionId,
                leg.Question,
                leg.NoTokenId,
                leg.Outcome,
                "BUY",
                "NO",
                leg.NoAsk,
                preTrade.Quantity,
                leg.NoAsk * preTrade.Quantity,
                "LIMIT",
                "GTC",
                false,
                true,
                now));
        }

        var total = orders.Sum(x => x.EstimatedCost);
        var duplicateOrderIds = orders.GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
        if (duplicateOrderIds) errors.Add("DuplicateOrderId");
        if (Math.Abs(total - preTrade.EstimatedCost) > Tolerance) errors.Add("CostMismatch");
        if (total > cfg.MaxNotionalPerBasket + Tolerance) errors.Add("MaxNotionalExceeded");
        if (orders.Any(x => !x.DryRunOnly)) errors.Add("DryRunFlagInvalid");
        if (orders.Any(x => !string.Equals(x.Side, "BUY", StringComparison.OrdinalIgnoreCase))) errors.Add("InvalidSide");
        if (orders.Any(x => !string.Equals(x.PositionSide, "NO", StringComparison.OrdinalIgnoreCase))) errors.Add("InvalidPositionSide");
        if (orders.Any(x => x.Quantity <= 0m)) errors.Add("InvalidQuantity");
        if (orders.Any(x => Math.Abs(x.Quantity - preTrade.Quantity) > Tolerance)) errors.Add("QuantityMismatch");
        if (orders.Any(x => x.Price <= 0m || x.Price > 1m)) errors.Add("InvalidPrice");
        if (orders.Any(x => string.IsNullOrWhiteSpace(x.TokenId))) errors.Add("MissingNoTokenId");
        if (orders.Any(x => string.IsNullOrWhiteSpace(x.MarketId))) errors.Add("MissingMarketId");
        if (Math.Abs(preTrade.ExpectedProfit - (preTrade.Quantity * preTrade.NetEdge)) > Tolerance) errors.Add("ExpectedProfitMismatch");

        var uniqueErrors = errors.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var status = uniqueErrors.Length == 0 ? BasketOrderPlanStatus.PaperOnly : BasketOrderPlanStatus.Rejected;
        return new BasketOrderPlan(
            Guid.NewGuid().ToString("N"),
            opp.Id,
            opp.GroupKey,
            opp.Title,
            opp.Strategy,
            opp.ActiveCostProfile,
            true,
            now,
            now.AddMinutes(10),
            status,
            opp.LegsCount,
            preTrade.Quantity,
            opp.GuaranteedPayout,
            opp.NoAskSum,
            total,
            preTrade.ExpectedProfit,
            preTrade.NetEdge,
            cfg.MaxNotionalPerBasket,
            orders,
            warnings,
            uniqueErrors
        );
    }
}
