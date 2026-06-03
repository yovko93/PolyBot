using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.SignalR;
using TradingBot.Services;

namespace TradingBot.Api;

public class BotEventPublisher(BotRuntimeState state, IHubContext<BotHub> hub)
{
    public async Task PublishHeartbeatAsync()
    {
        var status = state.Status with { LastHeartbeat = DateTime.UtcNow, ConnectionStatus = "CONNECTED" };
        state.SetStatus(status);
        state.AddSignalREvent("heartbeat");
        await hub.Clients.All.SendAsync("heartbeat", new { timestamp = DateTime.UtcNow, sequence = state.NextSeq() });
        state.AddSignalREvent("botStatusUpdated");
        await hub.Clients.All.SendAsync("botStatusUpdated", status);
    }

    public void CaptureConsole(string message, string level = "info", string source = "bot")
    {
        var entry = new TerminalLogEntryDto(Guid.NewGuid().ToString("N"), DateTime.UtcNow, level, source, message, state.NextSeq());
        state.AddLog(entry);
        state.AddSignalREvent("terminalLogAdded");
        _ = hub.Clients.All.SendAsync("terminalLogAdded", entry);
    }
}

public class BotHostedService(ILogger<BotHostedService> logger, BotRuntimeState state, BotEventPublisher publisher): BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await publisher.PublishHeartbeatAsync();
                state.SetStatus(state.Status with { ScannerActive = true, LastScanTime = DateTime.UtcNow });
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Heartbeat failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
    }
}
