using TradingBot.Api;
using TradingBot.Options;
using Microsoft.Extensions.Options;
using TradingBot.Models;
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
    public void High_severity_data_quality_audit_hourly_cap_works()
    {
        var cap = new TradingBot.Services.SingleMarketDataQualityAuditHourlyCap();
        var start = DateTime.UtcNow;

        Assert.True(cap.TryReserve(30, start, out var firstLogDue, out var firstCount));
        Assert.False(firstLogDue);
        for (var i = 1; i < 30; i++) Assert.True(cap.TryReserve(30, start.AddMinutes(1), out _, out _));

        Assert.False(cap.TryReserve(30, start.AddMinutes(2), out var capLogDue, out var cappedCount));
        Assert.True(capLogDue);
        Assert.Equal(30, cappedCount);
        Assert.False(cap.TryReserve(30, start.AddMinutes(3), out var repeatedLogDue, out _));
        Assert.False(repeatedLogDue);
        Assert.True(cap.TryReserve(30, start.AddHours(1).AddSeconds(1), out var resetLogDue, out _));
        Assert.False(resetLogDue);
    }

    [Fact]
    public void Logs_remain_within_max_recent_logs_after_60_minute_simulated_soak()
    {
        var runtime = new RuntimeStateOptions { MaxRecentLogs = 500 };
        var state = new BotRuntimeState(runtime);

        for (var minute = 0; minute < 60; minute++)
        for (var i = 0; i < 50; i++)
            state.AddLog(new TerminalLogEntryDto($"soak-{minute}-{i}", DateTime.UtcNow.AddMinutes(minute), "info", "soak", "simulated", i));

        Assert.True(state.Logs().Length <= runtime.MaxRecentLogs);
    }

    [Fact]
    public void Execution_audit_remains_within_configured_cap_after_60_minute_simulated_soak()
    {
        var runtime = new RuntimeStateOptions { MaxExecutionAuditEvents = 30 };
        var audit = new VerifiedBasketExecutionCoordinator(Microsoft.Extensions.Options.Options.Create(new ExecutionOptions()), Microsoft.Extensions.Options.Options.Create(new TradingBotOptions { RuntimeState = runtime }));

        for (var minute = 0; minute < 60; minute++)
        for (var i = 0; i < 3; i++)
            audit.Audit(new ExecutionAuditEvent(DateTime.UtcNow.AddMinutes(minute), $"opp-{minute}-{i}", "group", "strategy", "SingleMarketDataQualityRejected", "Rejected", "SuspiciousYesNoAskSum", 0, 0, 0, 0, "simulated"));

        Assert.True(audit.ListAudit(500).Count <= runtime.MaxExecutionAuditEvents);
    }


    [Fact]
    public void RuntimeHealth_includes_quiet_suppression_counters()
    {
        var state = new BotRuntimeState();
        state.SetQuietLogGateStats(new QuietLogGateStats(123, 7, new Dictionary<string, long> { ["multi"] = 123 }, new Dictionary<string, long> { ["multi"] = 7 }, 4, 2));

        var health = RuntimeHealthSnapshot.From(state);
        var line = health.ToLogLine();

        Assert.Equal(123, health.QuietSuppressedTotal);
        Assert.Contains("QuietSuppressed=123", line);
        Assert.Contains("EmittedLogs=7", line);
        Assert.Contains("LogGateCache=4", line);
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
