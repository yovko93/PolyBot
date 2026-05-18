using TradingBot.Models;

namespace TradingBot.Services;

public class ExecutionDecisionService
{
    private readonly ExecutionPolicy _policy;

    public ExecutionDecisionService(ExecutionPolicy policy)
    {
        _policy = policy;
    }

    public ExecutionDecision EvaluateBasket(
        BasketArbOpportunity opportunity,
        decimal currentBalance,
        decimal currentLockedCapital)
    {
        if (!_policy.AllowBasketArbs)
        {
            return Reject("Basket arbs disabled", opportunity.EdgePerShare);
        }

        if (opportunity.Legs.Count > _policy.MaxLegsPerBasket)
        {
            return Reject(
                $"Too many legs. Legs={opportunity.Legs.Count}, Max={_policy.MaxLegsPerBasket}",
                opportunity.EdgePerShare
            );
        }

        if (opportunity.CostPerShare <= 0)
        {
            return Reject("Invalid cost per share", opportunity.EdgePerShare);
        }

        if (opportunity.EdgePerShare < _policy.MinEdgePerShare)
        {
            return Reject(
                $"Edge too small. Edge={opportunity.EdgePerShare:0.####}, Min={_policy.MinEdgePerShare:0.####}",
                opportunity.EdgePerShare
            );
        }

        var remainingLockCapacity = _policy.MaxLockedCapital - currentLockedCapital;

        if (remainingLockCapacity <= 0)
        {
            return Reject(
                $"Max locked capital reached. Locked={currentLockedCapital:0.##}, Max={_policy.MaxLockedCapital:0.##}",
                opportunity.EdgePerShare
            );
        }

        var maxQuantityByBalance = currentBalance / opportunity.CostPerShare;
        var maxQuantityByRisk = _policy.MaxNotionalPerTrade / opportunity.CostPerShare;
        var maxQuantityByLockCapacity = remainingLockCapacity / opportunity.CostPerShare;

        var executableQuantity = Math.Min(
            opportunity.Quantity,
            Math.Min(
                maxQuantityByBalance,
                Math.Min(maxQuantityByRisk, maxQuantityByLockCapacity)
            )
        );

        if (executableQuantity <= 0)
        {
            return Reject("Executable quantity is zero", opportunity.EdgePerShare);
        }

        var totalCost = executableQuantity * opportunity.CostPerShare;

        if (totalCost < _policy.MinNotionalPerTrade)
        {
            return new ExecutionDecision(
                CanExecute: false,
                Reason: $"Notional too small. Cost={totalCost:0.####}, Min={_policy.MinNotionalPerTrade:0.####}",
                ExecutableQuantity: executableQuantity,
                TotalCost: totalCost,
                ExpectedProfit: executableQuantity * opportunity.EdgePerShare,
                EdgePerShare: opportunity.EdgePerShare
            );
        }

        var expectedProfit = executableQuantity * opportunity.EdgePerShare;

        if (expectedProfit < _policy.MinExpectedProfit)
        {
            return new ExecutionDecision(
                CanExecute: false,
                Reason: $"Expected profit too small. ExpectedProfit={expectedProfit:0.####}, Min={_policy.MinExpectedProfit:0.####}",
                ExecutableQuantity: executableQuantity,
                TotalCost: totalCost,
                ExpectedProfit: expectedProfit,
                EdgePerShare: opportunity.EdgePerShare
            );
        }

        return new ExecutionDecision(
            CanExecute: true,
            Reason: "OK",
            ExecutableQuantity: executableQuantity,
            TotalCost: totalCost,
            ExpectedProfit: expectedProfit,
            EdgePerShare: opportunity.EdgePerShare
        );
    }

    private static ExecutionDecision Reject(string reason, decimal edge)
    {
        return new ExecutionDecision(
            CanExecute: false,
            Reason: reason,
            ExecutableQuantity: 0m,
            TotalCost: 0m,
            ExpectedProfit: 0m,
            EdgePerShare: edge
        );
    }
}