using Microsoft.AspNetCore.SignalR;

namespace TradingBot.Api;

public interface IBotUiLogger
{
    void LogInfo(string source, string message);
    void LogWarn(string source, string message);
    void LogError(string source, string message);
    void LogSuccess(string source, string message);
}

public class BotUiLogger(BotRuntimeState state, IHubContext<BotHub> hub) : IBotUiLogger
{
    public void LogInfo(string source, string message) => Write("info", source, message);
    public void LogWarn(string source, string message) => Write("warn", source, message);
    public void LogError(string source, string message) => Write("error", source, message);
    public void LogSuccess(string source, string message) => Write("success", source, message);

    private void Write(string level, string source, string message)
    {
        var log = new TerminalLogEntryDto(Guid.NewGuid().ToString("N"), DateTime.UtcNow, level, source, message, state.NextSeq());
        state.AddLog(log);
        _ = hub.Clients.All.SendAsync("terminalLogAdded", log);
    }
}
