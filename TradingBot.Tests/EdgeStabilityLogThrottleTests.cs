using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class EdgeStabilityLogThrottleTests
{
    [Fact]
    public void Repeated_pending_detected_logs_are_throttled()
    {
        var throttle = new EdgeStabilityLogThrottle();
        var first = throttle.Evaluate("winner:2026 nba finals", VerifiedBasketState.EdgeExecutablePending, 1, 3, 0.0015m, TimeSpan.FromSeconds(1), "AwaitingConsecutiveScans");
        var second = throttle.Evaluate("winner:2026 nba finals", VerifiedBasketState.EdgeExecutablePending, 1, 3, 0.0015m, TimeSpan.FromSeconds(2), "AwaitingConsecutiveScans");

        Assert.True(first.LogPending);
        Assert.False(second.LogPending);
        Assert.False(second.LogStalled);
    }

    [Fact]
    public void Edge_executable_pending_exposes_consecutive_scans_and_reset_reason()
    {
        var throttle = new EdgeStabilityLogThrottle();
        var decision = throttle.Evaluate("winner:2026 nba finals", VerifiedBasketState.EdgeExecutablePending, 2, 3, 0.0015m, TimeSpan.FromSeconds(7), "NetEdgeVolatility");

        Assert.Equal(2, decision.ConsecutiveEdgeScans);
        Assert.Equal(3, decision.RequiredEdgeScans);
        Assert.Equal("NetEdgeVolatility", decision.LastResetReason);
        Assert.True(decision.StateAge >= TimeSpan.FromSeconds(7));
    }
}
