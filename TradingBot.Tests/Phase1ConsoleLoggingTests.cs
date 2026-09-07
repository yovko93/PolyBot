using TradingBot.Options;
using TradingBot.Services;
using Microsoft.Extensions.Configuration;

namespace TradingBot.Tests;

public sealed class Phase1ConsoleLoggingTests
{
    [Fact]
    public void EarlyRouterSuppressesProfileAndConfigLogsBeforeNormalStartup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"polybot-console-{Guid.NewGuid():N}");
        using var destination = new StringWriter();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var writer = Phase1ConsoleLogging.CreateEarlyWriter(destination,
            ["--profile", "ReducedDiagnosticsPaperPhase1", "--console-mode", "Summary5Min"], configuration, root);
        writer.WriteLine("[PROFILE_REGISTRY] KnownProfiles=x");
        writer.WriteLine("[PAPER_MODE] PaperOnly=true");
        Assert.Equal(string.Empty, destination.ToString());
        Assert.True(Phase1ConsoleLogging.RouterInitializedBeforeProfileLogging);
        Assert.True(Phase1ConsoleLogging.RouterInitializedBeforeConfigLogging);
        Assert.Contains("PROFILE_REGISTRY", Phase1ConsoleLogging.StartupVerboseEventNamesSuppressed);
        Assert.Contains("PAPER_MODE", File.ReadAllText(Path.Combine(root,"exports","debug","verbose-events","verbose-events.jsonl")));
        Phase1ConsoleLogging.MarkStartupComplete();
    }

    [Fact]
    public void SummaryModeSuppressesVerboseConsoleAndWritesJsonl()
    {
        var root = Path.Combine(Path.GetTempPath(), $"polybot-console-{Guid.NewGuid():N}");
        var options = new TradingBotOptions { Console = new ConsoleLoggingOptions { Mode = "Summary5Min", VerboseLogPath = "logs/verbose.jsonl" } };
        using var destination = new StringWriter();
        var writer = Phase1ConsoleLogging.CreateWriter(destination, options, root);

        writer.WriteLine("[RUNTIME_HEALTH] SigningAttempts=0 Detail=preserved");

        Assert.DoesNotContain("RUNTIME_HEALTH", destination.ToString());
        var jsonl = File.ReadAllText(Path.Combine(root,"exports","debug","verbose-events","verbose.jsonl"));
        Assert.Contains("\"event\":\"RUNTIME_HEALTH\"", jsonl);
        Assert.Contains("\"SigningAttempts\":\"0\"", jsonl);
    }

    [Theory]
    [InlineData("[RUNTIME_HEALTH] LastError=None")]
    [InlineData("[SOAK_STATUS] FormulaErrors=0")]
    [InlineData("[SCANNER_SUMMARY] LastError=None FatalErrors=0")]
    public void StrictAllowlistDoesNotTreatPayloadErrorFieldsAsErrors(string line)
    {
        var root = Path.Combine(Path.GetTempPath(), $"polybot-console-{Guid.NewGuid():N}");
        var options = new TradingBotOptions { Console = new ConsoleLoggingOptions { Mode = "Summary5Min", VerboseLogPath = "verbose.jsonl" } };
        using var destination = new StringWriter();
        var writer = Phase1ConsoleLogging.CreateWriter(destination, options, root);
        writer.WriteLine(line);
        Assert.Equal(string.Empty, destination.ToString());
        Assert.Contains(line[1..line.IndexOf(']')], File.ReadAllText(Path.Combine(root,"exports","debug","verbose-events","verbose.jsonl")));
    }

    [Fact]
    public void WaitingForEdgeBaselineAndNoopDoNotEmitAlertChange()
    {
        var root = Path.Combine(Path.GetTempPath(), $"polybot-console-{Guid.NewGuid():N}");
        var options = new TradingBotOptions { Console = new ConsoleLoggingOptions { Mode = "Summary5Min" } };
        using var destination = new StringWriter();
        Phase1ConsoleLogging.CreateWriter(destination, options, root);
        var before = Phase1ConsoleLogging.AlertNoopChangesSuppressed;
        Phase1ConsoleLogging.ObserveAlert(destination, 0, "WaitingForEdge", "BestRealWatchBelowMinEdge", "A", -.003m, false, .01m, "run");
        Phase1ConsoleLogging.ObserveAlert(destination, 0, "WaitingForEdge", "BestRealWatchBelowMinEdge", "B", -.002m, false, .01m, "run");
        Assert.Equal(string.Empty, destination.ToString());
        Assert.Equal(before + 1, Phase1ConsoleLogging.AlertNoopChangesSuppressed);
    }

    [Theory]
    [InlineData("Summary5Min", 300)]
    [InlineData("VerboseLegacy", 60)]
    [InlineData("Quiet", 15)]
    public void CliOverridesConsoleModeAndInterval(string mode, int seconds)
    {
        var options = new TradingBotOptions();
        RuntimeProfileService.ApplyCliOverrides(options, ["--console-mode", mode, "--console-summary-interval-seconds", seconds.ToString()]);
        Assert.Equal(mode, options.Console.Mode);
        Assert.Equal(seconds, options.Console.SummaryIntervalSeconds);
    }
}
