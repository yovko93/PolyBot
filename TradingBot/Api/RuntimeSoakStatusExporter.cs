using System.Text.Json;
using TradingBot.Options;

namespace TradingBot.Api;

public static class RuntimeSoakStatusExporter
{
    public static string Export(BotRuntimeState state, TradingBotOptions options, string contentRootPath)
    {
        var health = RuntimeHealthSnapshot.From(state, options);
        var logs = state.Logs();
        var trend = RuntimeHealthTrendTracker.Current(options.RuntimeHealth);
        var payload = new
        {
            timestamp = DateTime.UtcNow,
            uptime = health.Uptime,
            processMemoryMb = health.ProcessMemoryMb,
            gcMemoryMb = health.GcTotalMemoryMb,
            minProcessMemoryMbWindow = trend.MinProcessMemoryMbWindow,
            maxProcessMemoryMbWindow = trend.MaxProcessMemoryMbWindow,
            memoryDeltaMbWindow = trend.MemoryDeltaMbWindow,
            memorySlopeMbPerMinute = trend.MemorySlopeMbPerMinute,
            isMemoryStable = trend.IsMemoryStable && state.MemoryCriticals == 0,
            logsCount = health.RecentLogsCount,
            executionAuditCount = health.ExecutionAuditCount,
            signalRBufferCount = health.SignalREventBufferCount,
            orderbookCacheCount = health.OrderbookCacheCount,
            marketCacheCount = health.MarketCacheCount,
            singleMarketOpportunities = health.SingleMarketOpportunitiesCount,
            singleMarketDataQualitySamples = health.SingleMarketDataQualitySamplesCount,
            singleMarketNearMisses = health.SingleMarketNearMissesCount,
            paperOpenedCount = state.SingleMarketExecutionsCount,
            paperClosedCount = health.PaperClosedPositions,
            paperExposure = health.PaperTotalExposure,
            paperRealizedPnl = health.PaperRealizedPnl,
            paperLocked = health.PaperLocked,
            quietSuppressed = health.QuietSuppressedTotal,
            emittedLogs = health.EmittedLogs,
            logGateCacheSize = health.LogGateCacheSize,
            quietSuppressedByCategory = health.QuietSuppressedByCategory,
            emittedByCategory = health.EmittedByCategory,
            logVolumeStable = RuntimeHealthTrendTracker.IsLogVolumeStable(health, options),
            batchBookRequests = health.BatchBookRequests,
            batchBookBadRequests = health.BatchBookBadRequests,
            batchBookTimeouts = health.BatchBookTimeouts,
            batchBookRetrySuccesses = health.BatchBookRetrySuccesses,
            batchBookInvalidTokens = health.BatchBookInvalidTokens,
            batchBookSuppressedErrors = health.BatchBookSuppressedErrors,
            invalidTokenQuarantine = state.OrderBookServiceStats.QuarantinedTokens,
            memoryWarnings = Math.Max(state.MemoryWarnings, logs.Count(x => x.Message.Contains("[MEMORY_WARNING]", StringComparison.OrdinalIgnoreCase))),
            memoryCriticals = Math.Max(state.MemoryCriticals, logs.Count(x => x.Message.Contains("[MEMORY_CRITICAL]", StringComparison.OrdinalIgnoreCase))),
            lastMemoryCriticalAt = state.LastMemoryCriticalAt,
            scannerPausedByMemoryGuard = state.ScannerPausedByMemoryGuard,
            liveTradingEnabled = options.EnableLiveExecution,
            paperOnly = options.PaperOnly,
            soakReady = options.Diagnostics.OperationalQuietMode
                && options.RuntimeHealth.Enabled
                && options.SignalR.MaxPayloadItems > 0
                && options.SignalR.MaxPayloadBytes > 0
                && options.PaperOnly
                && !options.EnableLiveExecution
        };
        var path = Path.Combine(contentRootPath, "exports/runtime-soak-status-latest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        return path;
    }
}
