using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class ExecutionReadinessTrackerTests
{
    [Fact]
    public void EdgeStable_Is_Not_ExecutionStable_And_Does_Not_Imply_PreTradeReadiness()
    {
        var tracker = new VerifiedOpportunityStabilityTracker();
        var sample = tracker.TrackExecutionReadiness(Opp(0.91m), Options(), hasOpenDuplicate: false);
        Assert.False(sample.Ready);
        Assert.Equal("PlannedQtyBelowMinimum", sample.NotReadyReason);
        Assert.NotEqual(VerifiedBasketState.ExecutionStable, tracker.State(sample.GroupKey));
    }

    [Fact]
    public void Ready_Scans_Require_Three_Consecutive_Samples()
    {
        var tracker = new VerifiedOpportunityStabilityTracker();
        var options = Options();
        var s1 = tracker.TrackExecutionReadiness(Opp(30.14m), options, false);
        var s2 = tracker.TrackExecutionReadiness(Opp(30.14m), options, false);
        var s3 = tracker.TrackExecutionReadiness(Opp(30.14m), options, false);
        Assert.Equal(VerifiedBasketState.ExecutionReadinessPending, s1.State);
        Assert.Equal(1, s1.ConsecutiveReadyScans);
        Assert.Equal(VerifiedBasketState.ExecutionReadinessPending, s2.State);
        Assert.Equal(2, s2.ConsecutiveReadyScans);
        Assert.Equal(VerifiedBasketState.ExecutionStable, s3.State);
        Assert.Equal(3, s3.ConsecutiveReadyScans);
    }

    [Fact]
    public void Readiness_Resets_When_Liquidity_Drops()
    {
        var tracker = new VerifiedOpportunityStabilityTracker();
        var options = Options();
        tracker.TrackExecutionReadiness(Opp(30.14m), options, false);
        tracker.TrackExecutionReadiness(Opp(30.14m), options, false);
        var dropped = tracker.TrackExecutionReadiness(Opp(0.91m), options, false);
        Assert.True(dropped.Reset);
        Assert.Equal(2, dropped.PreviousReadyScans);
        Assert.Equal("PlannedQtyBelowMinimum", dropped.NotReadyReason);
        Assert.Equal(0, tracker.ConsecutiveExecutionReady(dropped.GroupKey));
    }

    [Fact]
    public void Export_Contains_Latest_Readiness_Sample()
    {
        var tracker = new VerifiedOpportunityStabilityTracker();
        var options = Options();
        tracker.TrackExecutionReadiness(Opp(30.14m), options, false);
        var path = Path.Combine(Path.GetTempPath(), $"execution-readiness-{Guid.NewGuid():N}.json");
        tracker.ExportExecutionReadiness(path, options.RequiredConsecutiveExecutionReadyScans);
        var json = File.ReadAllText(path);
        Assert.Contains("execution-ready-test", json);
        Assert.Contains("plannedQty", json);
        Assert.Contains("requiredConsecutiveReadyScans", json);
    }

    private static ExecutionOptions Options() => new()
    {
        MaxNotionalPerBasket = 250m,
        MinStableNetEdgePerBasket = 0.001m,
        RequiredConsecutiveExecutionReadyScans = 3,
        MinPlannedBasketQty = 5m,
        MinPlannedNotional = 25m,
        MinPlannedExpectedProfit = 0.10m,
        MaxPlannedQtyVolatilityRatio = 0.50m,
        MaxPlannedCostVolatilityRatio = 0.50m,
        MaxNetEdgeVolatility = 0.002m
    };

    private static VerifiedMultiOutcomeOpportunity Opp(decimal liquidity)
    {
        var legs = new[]
        {
            new VerifiedMultiOutcomeOpportunityLeg("m1", "c1", "q1", "NO", "no-1", 0.65m, liquidity, "DirectNoAsk", 0m, 0m),
            new VerifiedMultiOutcomeOpportunityLeg("m2", "c2", "q2", "NO", "no-2", 0.67m, liquidity, "DirectNoAsk", 0m, 0m),
            new VerifiedMultiOutcomeOpportunityLeg("m3", "c3", "q3", "NO", "no-3", 0.666m, liquidity, "DirectNoAsk", 0m, 0m),
        };
        var qty = Math.Min(250m / 1.986m, liquidity);
        return new VerifiedMultiOutcomeOpportunity("opp", "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", "execution-ready-test", "Execution Ready Test", "Verified", 3, 2m, 1.986m, 0.014m, 0.0085m, "Conservative", qty, qty * 0.0085m, 250m, qty * 1.986m, "PaperExecutable", legs);
    }
}
