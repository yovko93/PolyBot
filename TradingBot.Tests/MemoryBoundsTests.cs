using TradingBot.Api;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class MemoryBoundsTests
{
    [Fact]
    public void Runtime_log_buffer_is_bounded()
    {
        var state = new BotRuntimeState();
        for (var i = 0; i < 1200; i++)
        {
            state.AddLog(new TerminalLogEntryDto($"id-{i}", DateTime.UtcNow, "info", "test", $"m-{i}", i));
        }

        Assert.Equal(1000, state.Logs().Length);
    }

    [Fact]
    public void Runtime_opportunities_buffer_is_bounded()
    {
        var state = new BotRuntimeState();
        for (var i = 0; i < 600; i++)
        {
            state.AddOpportunity(new OpportunityDto($"opp-{i}", DateTime.UtcNow, i + 1, "s", "g", "m", "BOTH", 0.1m, 0.1m, 1m, 1.1m, 1m, true, "EXECUTABLE", null, i));
        }

        Assert.Equal(500, state.Opportunities().Length);
    }

    [Fact]
    public void Execution_audit_log_respects_limit_and_max()
    {
        var audit = new ExecutionAuditLog();
        for (var i = 0; i < 1500; i++)
        {
            audit.Add("event", $"opp-{i}", "msg");
        }

        Assert.Equal(1000, audit.List(5000).Length);
        Assert.Equal(300, audit.List().Length);
    }
}
