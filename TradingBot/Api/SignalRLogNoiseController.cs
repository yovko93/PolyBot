using System.Text.Json;
using TradingBot.Options;

namespace TradingBot.Api;

public static class SignalRLogNoiseController
{
    private static readonly object Gate = new();
    private static int _payloadTrimLoggedThisRun;
    private static long _payloadTrimSuppressedSinceSummary;
    private static DateTime _payloadTrimLastSummaryUtc = DateTime.MinValue;

    public static void HandlePayloadTrimmed(BotRuntimeState state, string eventName, int itemsBefore, int itemsAfter, TradingBotOptions options)
    {
        var c = options.SignalRLogNoiseControl;
        if (!c.Enabled || !c.SuppressPayloadTrimmed)
        {
            state.RecordSignalRPayloadTrimmed(eventName, itemsBefore, itemsAfter, logged: true);
            Console.WriteLine($"[SIGNALR_PAYLOAD_TRIMMED] Event={eventName} ItemsBefore={itemsBefore} ItemsAfter={itemsAfter}");
            TryExport(state, options);
            return;
        }

        lock (Gate)
        {
            var firstN = Math.Max(0, c.PayloadTrimmedFirstNPerRun);
            if (_payloadTrimLoggedThisRun < firstN)
            {
                _payloadTrimLoggedThisRun++;
                state.RecordSignalRPayloadTrimmed(eventName, itemsBefore, itemsAfter, logged: true);
                Console.WriteLine($"[SIGNALR_PAYLOAD_TRIMMED] Event={eventName} ItemsBefore={itemsBefore} ItemsAfter={itemsAfter}");
            }
            else
            {
                Interlocked.Increment(ref _payloadTrimSuppressedSinceSummary);
                state.RecordSignalRPayloadTrimmed(eventName, itemsBefore, itemsAfter, logged: false);
                var now = DateTime.UtcNow;
                var interval = Math.Max(1, c.PayloadTrimmedSummaryIntervalSeconds);
                if ((now - _payloadTrimLastSummaryUtc).TotalSeconds >= interval)
                {
                    _payloadTrimLastSummaryUtc = now;
                    var suppressed = Interlocked.Exchange(ref _payloadTrimSuppressedSinceSummary, 0);
                    Console.WriteLine($"[SIGNALR_PAYLOAD_TRIMMED_SUMMARY] Event={eventName} Suppressed={suppressed} LastItemsBefore={itemsBefore} LastItemsAfter={itemsAfter} IntervalSeconds={interval} ProcessRunId={ProcessRunContext.ProcessRunId}");
                }
            }
        }

        if (!state.SignalRLogNoiseControlConsistent)
            Console.WriteLine($"[SIGNALR_LOG_NOISE_CONTROL_WARNING] Reason=CounterMismatch Total={state.SignalRPayloadTrimmedTotal} Logged={state.SignalRPayloadTrimmedLogged} Suppressed={state.SignalRPayloadTrimmedSuppressed}");
        TryExport(state, options);
    }

    private static void TryExport(BotRuntimeState state, TradingBotOptions options)
    {
        if (!options.SignalRLogNoiseControl.ExportCounters) return;
        try
        {
            var exportPath = ResolveExportPath();
            Directory.CreateDirectory(Path.GetDirectoryName(exportPath)!);
            var payload = new { generatedAtUtc = DateTime.UtcNow, processRunId = ProcessRunContext.ProcessRunId, enabled = options.SignalRLogNoiseControl.Enabled, suppressPayloadTrimmed = options.SignalRLogNoiseControl.SuppressPayloadTrimmed, payloadTrimmedFirstNPerRun = options.SignalRLogNoiseControl.PayloadTrimmedFirstNPerRun, payloadTrimmedSummaryIntervalSeconds = options.SignalRLogNoiseControl.PayloadTrimmedSummaryIntervalSeconds, total = state.SignalRPayloadTrimmedTotal, logged = state.SignalRPayloadTrimmedLogged, suppressed = state.SignalRPayloadTrimmedSuppressed, lastEvent = state.SignalRPayloadTrimmedLastEvent, lastItemsBefore = state.SignalRPayloadTrimmedLastItemsBefore, lastItemsAfter = state.SignalRPayloadTrimmedLastItemsAfter, consistent = state.SignalRLogNoiseControlConsistent };
            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var tmp = exportPath + ".tmp";
            for (var i = 0; i < 3; i++)
            {
                try { File.WriteAllText(tmp, json); File.Move(tmp, exportPath, true); break; }
                catch (IOException) when (i < 2) { Thread.Sleep(50); }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[SIGNALR_LOG_NOISE_CONTROL_EXPORT_WARNING] Error={ex.Message}"); }
    }

    private static string ResolveExportPath()
    {
        var cwd = Directory.GetCurrentDirectory();
        var tradingBotDir = Path.GetFileName(cwd).Equals("TradingBot", StringComparison.OrdinalIgnoreCase) ? cwd : Path.Combine(cwd, "TradingBot");
        return Path.Combine(tradingBotDir, "exports", "signalr-log-noise-control-latest.json");
    }
}
