using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public interface IExchangeOrderExecutor
{
    Task SubmitAsync(OrderIntent intent, ExecutionOptions options, CancellationToken ct = default);
}

public sealed class DisabledExchangeOrderExecutor : IExchangeOrderExecutor
{
    public Task SubmitAsync(OrderIntent intent, ExecutionOptions options, CancellationToken ct = default)
    {
        LiveTradingGuard.AssertNoLiveTrading(options, nameof(DisabledExchangeOrderExecutor), LiveTradingAction.RealApiOrderSubmit);

        if (options.PaperOnly)
        {
            Console.WriteLine("[LIVE_EXECUTION_BLOCKED] Reason=PaperOnly");
            throw new InvalidOperationException("LiveExecutionDisabled");
        }

        if (!options.EnableLiveOrderSubmission)
        {
            Console.WriteLine("[LIVE_EXECUTION_BLOCKED] Reason=LiveOrderSubmissionDisabled");
            throw new InvalidOperationException("LiveExecutionDisabled");
        }

        throw new InvalidOperationException("NoLiveExecutorConfigured");
    }
}
