using System.Threading;
using TradingBot.Options;

namespace TradingBot.Services;

public enum LiveTradingAction
{
    OrderSigning,
    RealApiOrderSubmit,
    LiveOrderPlanExecution,
    LiveSettlement,
    LiveCancellation
}

public sealed class LiveTradingBlockedException(string component, LiveTradingAction action)
    : InvalidOperationException($"Live trading action blocked: {component}/{action}")
{
    public string Component { get; } = component;
    public LiveTradingAction Action { get; } = action;
}

public static class LiveTradingGuard
{
    private static long _blockedCount;
    private static long _signingAttempts;
    public static long BlockedCount => Interlocked.Read(ref _blockedCount);
    public static long SigningAttempts => Interlocked.Read(ref _signingAttempts);
    public static void ResetForTests()
    {
        Interlocked.Exchange(ref _blockedCount, 0);
        Interlocked.Exchange(ref _signingAttempts, 0);
    }

    public static void AssertNoLiveTrading(TradingBotOptions options, string component, LiveTradingAction action)
        => AssertNoLiveTrading(options.TradingMode.LiveTradingEnabled || options.EnableLiveExecution, component, action);

    public static void AssertNoLiveTrading(ExecutionOptions options, string component, LiveTradingAction action)
        => AssertNoLiveTrading(options.EnableLiveTrading && options.EnableLiveOrderSubmission && !options.PaperOnly, component, action);

    public static void AssertNoLiveTrading(bool liveTradingEnabled, string component, LiveTradingAction action)
    {
        if (liveTradingEnabled) return;
        if (action == LiveTradingAction.OrderSigning) Interlocked.Increment(ref _signingAttempts);
        Interlocked.Increment(ref _blockedCount);
        Console.WriteLine($"[LIVE_TRADING_BLOCKED] Reason=LiveTradingDisabled Component={component} Action={action}");
        throw new LiveTradingBlockedException(component, action);
    }

    public static void AssertOrderSigningAllowed(bool liveTradingEnabled, string component = "OrderSigner")
        => AssertNoLiveTrading(liveTradingEnabled, component, LiveTradingAction.OrderSigning);

    public static void AssertLiveSubmitAllowed(bool liveTradingEnabled, string component = "ExchangeOrderExecutor")
        => AssertNoLiveTrading(liveTradingEnabled, component, LiveTradingAction.RealApiOrderSubmit);

    public static void AssertLiveCancellationAllowed(bool liveTradingEnabled, string component = "ExchangeOrderExecutor")
        => AssertNoLiveTrading(liveTradingEnabled, component, LiveTradingAction.LiveCancellation);

    public static void AssertLiveSettlementAllowed(bool liveTradingEnabled, string component = "Settlement")
        => AssertNoLiveTrading(liveTradingEnabled, component, LiveTradingAction.LiveSettlement);
}
