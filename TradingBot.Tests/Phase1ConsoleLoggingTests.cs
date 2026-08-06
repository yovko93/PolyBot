using TradingBot.Options;
using TradingBot.Services;

namespace TradingBot.Tests;

public sealed class Phase1ConsoleLoggingTests
{
    [Fact]
    public void SummaryModeSuppressesVerboseConsoleAndWritesJsonl()
    {
        var root = Path.Combine(Path.GetTempPath(), $"polybot-console-{Guid.NewGuid():N}");
        var options = new TradingBotOptions { Console = new ConsoleLoggingOptions { Mode = "Summary5Min", VerboseLogPath = "logs/verbose.jsonl" } };
        using var destination = new StringWriter();
        var writer = Phase1ConsoleLogging.CreateWriter(destination, options, root);

        writer.WriteLine("[RUNTIME_HEALTH] SigningAttempts=0 Detail=preserved");

        Assert.DoesNotContain("RUNTIME_HEALTH", destination.ToString());
        var jsonl = File.ReadAllText(Path.Combine(root, "logs/verbose.jsonl"));
        Assert.Contains("\"event\":\"RUNTIME_HEALTH\"", jsonl);
        Assert.Contains("\"SigningAttempts\":\"0\"", jsonl);
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
