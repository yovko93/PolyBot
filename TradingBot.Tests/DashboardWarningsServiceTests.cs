using System.Text.Json;
using TradingBot.Services;

namespace TradingBot.Tests;

public sealed class DashboardWarningsServiceTests
{
    [Fact]
    public void ExpectedReducedUniverseGlobalWarningIsNonBlockingWhenLocalPaperIsReady()
    {
        var result = DashboardWarningsService.Classify(
            new Dictionary<string, long> { ["ReadinessInvariant:UnhealthyDiscoveryRequiresUnstableRuntime"] = 81 },
            ["ReadinessInvariant:UnhealthyDiscoveryRequiresUnstableRuntime"],
            localPaperPhase1Readiness: true, orderbookStable: true, fixtureIsolationOk: true);

        Assert.Equal(81, result.Total);
        Assert.Equal(0, result.Blocking);
        Assert.Equal(81, result.NonBlocking);
        Assert.False(result.AffectsPaperPhase1);
        Assert.False(result.AffectsTradingSafety);
        Assert.True(result.Consistent);
        Assert.Equal("ReadyWaitingForEdgeWithNonBlockingWarnings", result.Status("ReadyWaitingForEdge"));
    }

    [Theory]
    [InlineData("PaperCounterImbalanceDetected")]
    [InlineData("OrderbookLifecycleMismatch")]
    [InlineData("SigningAttemptsGreaterThanZero")]
    [InlineData("FixtureStateWithoutExplicitFlag")]
    [InlineData("DashboardExportFailure:IOException")]
    public void SafetyAndPaperWarningsBlock(string reason)
    {
        var result = DashboardWarningsService.Classify(new Dictionary<string, long> { [reason] = 2 },
            [reason], true, true, true);

        Assert.Equal(2, result.Blocking);
        Assert.Equal("WarningBlocked", result.Status("ReadyWaitingForEdge"));
        Assert.StartsWith("InvestigateAndFix:", result.RecommendedAction);
    }

    [Fact]
    public void InvalidPositiveArtifactExportFailureIsVisibleButNonBlocking()
    {
        const string reason = "ExportWriteError:InvalidPositiveArtifacts";
        var result = DashboardWarningsService.Classify(new Dictionary<string, long> { [reason] = 1 },
            [reason], true, true, true);

        Assert.Equal(1, result.NonBlocking);
        Assert.Equal(0, result.Blocking);
        Assert.False(result.AffectsPaperPhase1);
        Assert.False(result.AffectsTradingSafety);
        Assert.Equal(reason, result.TopReason);
    }

    [Fact]
    public void ExportWritesLatestAndJsonlHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"polybot-dashboard-warnings-{Guid.NewGuid():N}");
        SafeExportWriter.Configure(new TradingBot.Options.JsonlExportOptions { MinFreeDiskMb=0,CriticalFreeDiskMb=0,RetentionEnabled=false },root);
        var result = DashboardWarningsService.Classify(new Dictionary<string, long> { ["StaleUISample"] = 3 },
            ["StaleUISample"], true, true, true);

        Assert.True(DashboardWarningsService.Export(root, result, DateTime.UnixEpoch));
        var latest = Path.Combine(root, "exports", "latest", "dashboard-warnings.json");
        var history = Path.Combine(root, "exports", "history", "dashboard-warnings.jsonl");
        Assert.True(File.Exists(latest));
        Assert.Single(File.ReadLines(history));
        using var json = JsonDocument.Parse(File.ReadAllText(latest));
        Assert.Equal(3, json.RootElement.GetProperty("Total").GetInt64());
        Assert.Equal(0, json.RootElement.GetProperty("Blocking").GetInt64());
        Assert.Equal("NoActionRequired;monitor_diagnostics", json.RootElement.GetProperty("RecommendedAction").GetString());
    }
}
