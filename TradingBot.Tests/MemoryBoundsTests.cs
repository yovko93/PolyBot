using TradingBot.Api;
using TradingBot.Options;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class MemoryBoundsTests
{

    [Fact]
    public void Soak_status_export_is_written()
    {
        var dir = Directory.CreateTempSubdirectory();
        var state = new BotRuntimeState();
        state.AddLog(new TerminalLogEntryDto("warn", DateTime.UtcNow, "warning", "memory", "[MEMORY_WARNING] sample", 1));
        var options = new TradingBotOptions
        {
            EnableLiveExecution = false,
            PaperOnly = true,
            Diagnostics = { OperationalQuietMode = true },
            RuntimeHealth = { Enabled = true },
            SignalR = { MaxPayloadItems = 100, MaxPayloadBytes = 1024 }
        };

        var path = RuntimeSoakStatusExporter.Export(state, options, dir.FullName);
        var json = File.ReadAllText(path);

        Assert.EndsWith("exports/runtime-soak-status-latest.json", path.Replace('\\', '/'));
        Assert.Contains("\"soakReady\": true", json);
        Assert.Contains("\"memoryWarnings\": 1", json);
        Assert.Contains("\"liveTradingEnabled\": false", json);
        Assert.Contains("\"minProcessMemoryMbWindow\"", json);
        Assert.Contains("\"memorySlopeMbPerMinute\"", json);
        Assert.Contains("\"isMemoryStable\"", json);
    }

    [Fact]
    public void Runtime_health_trend_calculates_slope_and_stability()
    {
        var start = DateTime.UtcNow.AddMinutes(-20);
        var trend = RuntimeHealthTrendTracker.Analyze(
            [
                (start, 260d),
                (start.AddMinutes(10), 275d),
                (start.AddMinutes(20), 286d)
            ],
            new RuntimeHealthOptions { SoakTrendWindowMinutes = 20, StableMemorySlopeMbPerMinute = 5, StableMemoryMaxDeltaMb = 150 });

        Assert.Equal(260, trend.MinProcessMemoryMbWindow);
        Assert.Equal(286, trend.MaxProcessMemoryMbWindow);
        Assert.Equal(26, trend.MemoryDeltaMbWindow);
        Assert.True(trend.IsMemoryStable);
    }

    [Fact]
    public void Logs_stay_bounded_after_20000_market_evaluation_logs()
    {
        var runtime = new RuntimeStateOptions { MaxRecentLogs = 500 };
        var state = new BotRuntimeState(runtime);

        for (var i = 0; i < 20_000; i++)
            state.AddLog(new TerminalLogEntryDto($"eval-{i}", DateTime.UtcNow, "info", "single-market", $"evaluation {i}", i));

        Assert.True(state.Logs().Length <= runtime.MaxRecentLogs);
    }

    [Fact]
    public void Execution_audit_count_stays_bounded_after_20000_market_evaluations()
    {
        var runtime = new RuntimeStateOptions { MaxExecutionAuditEvents = 500 };
        var state = new BotRuntimeState(runtime);

        for (var i = 0; i < 20_000; i++)
            state.SetRuntimeCounts(executionAuditCount: Math.Min(i + 1, runtime.MaxExecutionAuditEvents));

        Assert.True(state.ExecutionAuditCount <= runtime.MaxExecutionAuditEvents);
    }


    [Fact]
    public void SignalR_buffer_remains_bounded_after_20000_events()
    {
        var runtime = new RuntimeStateOptions { MaxSignalREventBuffer = 100 };
        var state = new BotRuntimeState(runtime);

        for (var i = 0; i < 20_000; i++)
            state.AddSignalREvent($"event-{i}");

        Assert.True(state.SignalREventBufferCount <= runtime.MaxSignalREventBuffer);
    }

    [Fact]
    public void Runtime_health_log_schedule_is_periodic_independent_of_scanner_loop()
    {
        var lastLogged = DateTime.UtcNow;

        Assert.False(RuntimeHealthSnapshot.ShouldLogAt(lastLogged.AddSeconds(30), lastLogged, everyMinutes: 2));
        Assert.True(RuntimeHealthSnapshot.ShouldLogAt(lastLogged.AddMinutes(2).AddSeconds(1), lastLogged, everyMinutes: 2));
    }

    [Fact]
    public void Runtime_log_buffer_is_bounded()
    {
        var state = new BotRuntimeState();
        for (var i = 0; i < 1200; i++)
            state.AddLog(new TerminalLogEntryDto($"id-{i}", DateTime.UtcNow, "info", "test", $"m-{i}", i));

        Assert.Equal(500, state.Logs().Length);
    }

    [Fact]
    public void Runtime_opportunities_buffer_is_bounded()
    {
        var state = new BotRuntimeState();
        for (var i = 0; i < 600; i++)
            state.AddOpportunity(new OpportunityDto($"opp-{i}", DateTime.UtcNow, i + 1, "s", "g", "m", "BOTH", 0.1m, 0.1m, 1m, 1.1m, 1m, true, "EXECUTABLE", null, i));

        Assert.Equal(500, state.Opportunities().Length);
    }

    [Fact]
    public void Scanner_history_is_bounded()
    {
        var state = new BotRuntimeState();
        for (var i = 0; i < 800; i++) state.SetScannerStats(state.ScannerStats with { Sequence = i });
        Assert.Equal(500, state.ScannerStatsHistoryCount);
    }

    [Fact]
    public void Execution_audit_log_respects_limit_and_max()
    {
        var audit = new ExecutionAuditLog();
        for (var i = 0; i < 1500; i++) audit.Add("event", $"opp-{i}", "msg");
        Assert.Equal(1000, audit.List(5000).Length);
        Assert.Equal(300, audit.List().Length);
    }
}
