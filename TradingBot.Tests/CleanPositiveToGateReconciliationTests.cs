using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public sealed class CleanPositiveToGateReconciliationTests
{
    [Fact]
    public void Candidate_contract_contains_every_requested_gate_stage()
    {
        var properties = typeof(CleanPositiveGateCandidate).GetProperties().Select(x => x.Name).ToHashSet();

        foreach (var stage in new[] { "LadderPositiveAfterSafety", "CleanPositive", "RealWatchCandidate",
                     "GateCandidate", "EdgeStable", "DepthSufficient", "FillPassed", "RiskPassed",
                     "PaperDiagnosticsLimitedEligible", "PaperEligible", "PaperOpened" })
            Assert.Contains(stage, properties);
    }

    [Fact]
    public void Empty_reconciliation_identifies_the_two_different_edge_sources()
    {
        var state = CleanPositiveGateReconciliation.Empty;

        Assert.Equal("FocusUniverseAfterSafetyEdge", state.P99AfterSafetySource);
        Assert.Equal("RealWatchGateAfterSafetyEdge", state.BestEdgeSource);
        Assert.Equal("NoPositiveCandidate", state.P99CandidateGateBlocker);
    }
}
