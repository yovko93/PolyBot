using Microsoft.AspNetCore.SignalR;

namespace TradingBot.Api;

public interface IBotUiLogger
{
    void Info(string source, string message);
    void Warn(string source, string message);
    void Error(string source, string message);
}

public class BotUiLogger(BotRuntimeState state, IHubContext<BotHub> hub) : IBotUiLogger
{
    public void Info(string source, string message) => Write("info", source, message);
    public void Warn(string source, string message) => Write("warn", source, message);
    public void Error(string source, string message) => Write("error", source, message);

    private void Write(string level, string source, string message)
    {
        Console.WriteLine(message);
        var log = new TerminalLogEntryDto(Guid.NewGuid().ToString("N"), DateTime.UtcNow, level, source, message, state.NextSeq());
        state.AddLog(log);
        _ = hub.Clients.All.SendAsync("terminalLogAdded", log);
    }
}

public class UiConsoleTextWriter(IBotUiLogger logger, string source="console") : TextWriter
{
    public override Encoding Encoding => Encoding.UTF8;
    public override void WriteLine(string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            logger.Info(source, value);
    }
}
