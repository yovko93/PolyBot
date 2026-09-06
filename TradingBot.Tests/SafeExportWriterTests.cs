using TradingBot.Options;
using TradingBot.Services;

namespace TradingBot.Tests;

public sealed class SafeExportWriterTests
{
    [Theory]
    [InlineData("paper-phase1-canary-latest.json", "PaperPhase1SyntheticCanary", false)]
    [InlineData("paper-phase1-invalid-positive-artifacts.jsonl", "InvalidPositiveArtifacts", false)]
    [InlineData("diagnostics-dashboard-latest.json", "DashboardLatest", true)]
    public void InjectedDiskFullIsNonFatalAndUpdatesHealth(string file, string stream, bool critical)
    {
        var root = Path.Combine(Path.GetTempPath(), $"polybot-safe-export-{Guid.NewGuid():N}");
        SafeExportWriter.Configure(new JsonlExportOptions
        {
            InjectDiskFullForTesting = true,
            JsonlMaxRetries = 0,
            JsonlDisableAfterConsecutiveFailures = 2,
            MinFreeDiskMb = 0,
            CriticalFreeDiskMb = 0,
            RetentionEnabled = false
        }, root);

        var exception = Record.Exception(() => SafeExportWriter.WriteText(Path.Combine(root, "exports", file), "{}", stream, critical));

        Assert.Null(exception);
        var health = SafeExportWriter.Snapshot();
        Assert.True(health.WriteFailuresTotal >= 2); // primary and fallback
        Assert.Equal("IOException", health.LastExceptionType);
        Assert.Contains("space on the disk", health.LastExceptionMessageShort);
        Assert.NotEqual("Ok", health.Health);
    }

    [Fact]
    public void RepeatedFailureDisablesOnlyThatStream()
    {
        var root = Path.Combine(Path.GetTempPath(), $"polybot-safe-export-{Guid.NewGuid():N}");
        SafeExportWriter.Configure(new JsonlExportOptions { InjectDiskFullForTesting = true, JsonlMaxRetries = 0, JsonlDisableAfterConsecutiveFailures = 2, MinFreeDiskMb = 0, CriticalFreeDiskMb = 0, RetentionEnabled = false }, root);
        var path = Path.Combine(root, "exports", "canary.json");

        Assert.False(SafeExportWriter.WriteText(path, "{}", "Canary", false));
        Assert.False(SafeExportWriter.WriteText(path, "{}", "Canary", false));
        Assert.Equal("Disabled", SafeExportWriter.Snapshot().Health);
    }
}
