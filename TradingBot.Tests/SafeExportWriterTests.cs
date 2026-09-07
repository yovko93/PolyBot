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

    [Fact]
    public void DirectoryCollisionUsesNormalizedExportsFallback()
    {
        var root=Path.Combine(Path.GetTempPath(),$"polybot-safe-export-{Guid.NewGuid():N}");
        var legacy=Path.Combine(root,"exports","paper-phase1-positive-captures-latest.json");
        var primary=Path.Combine(root,"exports","latest","paper-phase1-positive-captures.json");
        SafeExportWriter.Configure(new JsonlExportOptions { JsonlMaxRetries=0,MinFreeDiskMb=0,CriticalFreeDiskMb=0,RetentionEnabled=false },root);
        Directory.CreateDirectory(primary); // Simulates a denied/non-file consolidated target.

        Assert.True(SafeExportWriter.WriteJson(legacy,"{}","paper-phase1-positive-captures-latest",critical:false));

        var stream=SafeExportWriter.StreamSnapshot("paper-phase1-positive-captures-latest");
        Assert.Equal("Fallback",stream.Health);
        Assert.True(stream.FallbackActive);
        Assert.Equal(Path.GetFullPath(primary),stream.PrimaryPath);
        Assert.Equal(Path.Combine(root,"exports","archive","fallback","paper-phase1-positive-captures.json"),stream.FallbackPath);
        Assert.True(File.Exists(stream.FallbackPath));
    }

    [Fact]
    public void ConsolidatedLayoutRoutesLatestHistoryAndDebugWithoutTopLevelArtifacts()
    {
        var root=Path.Combine(Path.GetTempPath(),$"polybot-safe-export-{Guid.NewGuid():N}");
        SafeExportWriter.Configure(new JsonlExportOptions { Root="exports",Layout="Consolidated",MinFreeDiskMb=0,CriticalFreeDiskMb=0,RetentionEnabled=false },root);

        Assert.True(SafeExportWriter.WriteJson(Path.Combine(root,"exports","diagnostics-dashboard-latest.json"),"{}","DiagnosticsDashboardLatest"));
        Assert.True(SafeExportWriter.AppendText(Path.Combine(root,"exports","dashboard-warnings-history.jsonl"),"{}\n","DashboardHistory"));
        Assert.True(SafeExportWriter.AppendText(Path.Combine(root,"exports","logs","verbose-events.jsonl"),"{}\n","verbose-events"));

        Assert.True(File.Exists(Path.Combine(root,"exports","latest","diagnostics-dashboard.json")));
        Assert.True(File.Exists(Path.Combine(root,"exports","history","dashboard-warnings.jsonl")));
        Assert.True(File.Exists(Path.Combine(root,"exports","debug","verbose-events","verbose-events.jsonl")));
        Assert.Empty(Directory.GetFiles(Path.Combine(root,"exports")));
        Assert.Equal(0,SafeExportWriter.Snapshot().TopLevelGeneratedFilesCount);
    }

    [Fact]
    public void ConsolidatedLayoutArchivesLegacyTopLevelArtifacts()
    {
        var root=Path.Combine(Path.GetTempPath(),$"polybot-safe-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root,"exports"));
        File.WriteAllText(Path.Combine(root,"exports","old-generated.json"),"{}");
        File.WriteAllText(Path.Combine(root,"exports","README.md"),"keep");

        SafeExportWriter.Configure(new JsonlExportOptions { Root="exports",Layout="Consolidated",WriteLegacyPointerFiles=false,MinFreeDiskMb=0,CriticalFreeDiskMb=0,RetentionEnabled=false },root);

        Assert.False(File.Exists(Path.Combine(root,"exports","old-generated.json")));
        Assert.True(File.Exists(Path.Combine(root,"exports","archive","legacy-top-level","old-generated.json")));
        Assert.True(File.Exists(Path.Combine(root,"exports","README.md")));
        Assert.Equal(0,SafeExportWriter.Snapshot().TopLevelGeneratedFilesCount);
    }
}
