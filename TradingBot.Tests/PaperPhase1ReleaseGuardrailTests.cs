using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class PaperPhase1ReleaseGuardrailTests
{
    [Fact]
    public void FixturePosition_IsExcludedFromNormalRuntime()
    {
        var book = new PaperPositionBook(Path.Combine(Path.GetTempPath(), $"phase1-{Guid.NewGuid():N}.csv"));
        var position = book.AddContractFixturePosition(.012m, .01m, 4m);

        Assert.NotNull(position);
        Assert.Equal("RealScannerFixture", position!.SourceKind);
        Assert.True(position.IsFixture);
        Assert.False(position.IsSyntheticCanary);
        Assert.False(position.CountsTowardNormalRuntime);
    }

    [Fact]
    public void SyntheticCanary_IsExcludedFromNormalRuntime()
    {
        var book = new PaperPositionBook(Path.Combine(Path.GetTempPath(), $"phase1-{Guid.NewGuid():N}.csv"));
        var position = book.AddSyntheticCanaryPosition("canary", "market", "yes", "no", .49m, .49m,
            1m, .98m, 1m, .02m, .02m, .012m, "candidate", "run");

        Assert.NotNull(position);
        Assert.Equal("SyntheticCanary", position!.SourceKind);
        Assert.False(position.IsFixture);
        Assert.True(position.IsSyntheticCanary);
        Assert.False(position.CountsTowardNormalRuntime);
    }
}
