using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public sealed class PaperPhase1PositiveReconciliationTests
{
    [Fact]
    public void Positive_edge_never_reports_below_min_edge_as_best_reason()
    {
        var reason = PaperPhase1PositiveReconciliationService.NormalizeBestReason("BelowMinEdge", 0.092m, 0.01m);

        Assert.Equal("CandidateSnapshotMismatch", reason);
    }

    [Fact]
    public void Actual_below_min_edge_reason_is_preserved()
    {
        var reason = PaperPhase1PositiveReconciliationService.NormalizeBestReason("BelowMinEdge", -0.003m, 0.01m);

        Assert.Equal("BelowMinEdge", reason);
    }
}
