using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed class VerifiedBasketDryRunOrderBuilder
{
    public BasketOrderPlan Build(VerifiedMultiOutcomeOpportunity opp, VerifiedBasketPreTradeValidationResult preTrade, ExecutionOptions cfg)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var orders = new List<OrderIntent>();
        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var leg in opp.Legs)
        {
            if (string.IsNullOrWhiteSpace(leg.NoTokenId)) errors.Add("MissingNoTokenId");
            if (string.IsNullOrWhiteSpace(leg.MarketId)) errors.Add("MissingMarketId");
            if (leg.NoAsk <= 0m || leg.NoAsk > 1m) errors.Add("InvalidPrice");
            if (preTrade.Quantity <= 0m) errors.Add("InvalidQuantity");
            var id = $"{opp.Id}:{leg.MarketId}:{leg.NoTokenId}:BUY:NO";
            if (!seen.Add(id)) errors.Add("DuplicateOrderId");
            orders.Add(new OrderIntent(id, opp.Id, opp.GroupKey, opp.Strategy, leg.MarketId, leg.ConditionId, leg.Question, leg.NoTokenId, leg.Outcome, "BUY", "NO", leg.NoAsk, preTrade.Quantity, leg.NoAsk * preTrade.Quantity, "LIMIT", "GTC", false, true, now));
        }

        var total = orders.Sum(x => x.EstimatedCost);
        const decimal tol = 0.000001m;
        if (Math.Abs(total - preTrade.EstimatedCost) > tol) errors.Add("CostMismatch");
        if (total > cfg.MaxNotionalPerBasket + tol) errors.Add("MaxNotionalExceeded");
        if (orders.Any(x => !x.DryRunOnly)) errors.Add("DryRunFlagInvalid");
        if (orders.Any(x => x.Side != "BUY")) errors.Add("InvalidSide");
        if (orders.Any(x => x.PositionSide != "NO")) errors.Add("InvalidPositionSide");
        if (Math.Abs(preTrade.ExpectedProfit - (preTrade.Quantity * preTrade.NetEdge)) > tol) warnings.Add("ExpectedProfitRounded");

        var status = errors.Count == 0 ? BasketOrderPlanStatus.PaperOnly : BasketOrderPlanStatus.Rejected;
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
            errors
        );
    }
}
