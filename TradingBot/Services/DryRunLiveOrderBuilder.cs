using System.Globalization;
using TradingBot.Models;

namespace TradingBot.Services;

public class DryRunLiveOrderBuilder
{
    public LiveOrderDryRunPlan BuildPlan(ExecutionCandidate candidate, ExecutionDecision decision)
    {
        if (candidate is null)
            throw new ArgumentNullException(nameof(candidate));

        if (decision is null)
            throw new ArgumentNullException(nameof(decision));

        if (!decision.CanExecute)
            throw new InvalidOperationException(
                $"Execution decision is rejected. Reason={decision.Reason}");

        if (decision.ExecutableQuantity <= 0)
            throw new InvalidOperationException("Executable quantity must be positive.");

        var orders = candidate.Legs
            .Select((leg, idx) => ToOrder(candidate, leg, decision.ExecutableQuantity, idx))
            .ToList();

        var notes = string.Join(
            " | ",
            $"DRY-RUN live order plan only (no API calls)",
            $"CandidateType={candidate.Type}",
            $"Legs={orders.Count}",
            $"DecisionReason={decision.Reason}");

        return new LiveOrderDryRunPlan(
            CandidateKey: candidate.Key,
            Strategy: candidate.Strategy,
            Quantity: decision.ExecutableQuantity,
            TotalNotional: decision.TotalCost,
            ExpectedProfit: decision.ExpectedProfit,
            EdgePerShare: decision.EdgePerShare,
            Orders: orders,
            Notes: notes);
    }

    private static LiveOrderIntent ToOrder(
        ExecutionCandidate candidate,
        ExecutionCandidateLeg leg,
        decimal executableQuantity,
        int index)
    {
        var side = ResolveSide(candidate.Type);

        return new LiveOrderIntent(
            MarketId: leg.MarketId,
            Question: leg.Question,
            Outcome: leg.Outcome,
            Side: side,
            LimitPrice: leg.SnapshotPrice,
            Quantity: executableQuantity,
            ClientOrderId: BuildClientOrderId(candidate.Key, index),
            Strategy: candidate.Strategy,
            CandidateKey: candidate.Key);
    }

    private static LiveOrderSide ResolveSide(ExecutionCandidateType type)
    {
        return type == ExecutionCandidateType.CompleteSetSell
            ? LiveOrderSide.Sell
            : LiveOrderSide.Buy;
    }

    private static string BuildClientOrderId(string key, int legIndex)
    {
        var sanitized = key.Replace("|", "-").Replace(" ", "-");
        return $"dryrun-{sanitized}-{legIndex + 1}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
    }

    public static string FormatForConsole(LiveOrderDryRunPlan plan)
    {
        var lines = new List<string>
        {
            "[DRY-RUN LIVE ORDER PLAN]",
            $"Key={plan.CandidateKey}",
            $"Strategy={plan.Strategy}",
            $"Qty={plan.Quantity.ToString("0.####", CultureInfo.InvariantCulture)}",
            $"Notional={plan.TotalNotional.ToString("0.####", CultureInfo.InvariantCulture)}",
            $"ExpectedProfit={plan.ExpectedProfit.ToString("0.####", CultureInfo.InvariantCulture)}",
            $"EdgePerShare={plan.EdgePerShare.ToString("0.####", CultureInfo.InvariantCulture)}"
        };

        for (var i = 0; i < plan.Orders.Count; i++)
        {
            var order = plan.Orders[i];
            lines.Add(
                $"Leg#{i + 1}: {order.Side} {order.Outcome} | Market={order.MarketId} | " +
                $"Px={order.LimitPrice.ToString("0.####", CultureInfo.InvariantCulture)} | " +
                $"Qty={order.Quantity.ToString("0.####", CultureInfo.InvariantCulture)} | " +
                $"ClientOrderId={order.ClientOrderId}");
        }

        lines.Add($"Notes={plan.Notes}");
        return string.Join(Environment.NewLine, lines);
    }
}
