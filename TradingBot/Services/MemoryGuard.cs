using System.Diagnostics;
using System.Text.Json;
using TradingBot.Api;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed class MemoryGuard
{
    private readonly object _gate = new();
    private DateTime _pausedUntilUtc = DateTime.MinValue;
    private DateTime _lastWarningLoggedAt = DateTime.MinValue;
    private DateTime _lastCriticalLoggedAt = DateTime.MinValue;

    public DateTime PausedUntilUtc { get { lock (_gate) return _pausedUntilUtc; } }

    public bool IsScannerPausedByMemory => PausedUntilUtc > DateTime.UtcNow;

    public bool ShouldSkipScannerCycle() => IsScannerPausedByMemory;

    public void Check(BotRuntimeState state, TradingBotOptions options, Action? clearNonEssentialCaches = null, string? contentRootPath = null)
    {
        var memory = options.RuntimeMemory;
        var processMb = GetProcessMemoryMb();
        var now = DateTime.UtcNow;
        if (processMb >= memory.CriticalProcessMemoryMb || processMb >= memory.MaxProcessMemoryMb)
        {
            var action = memory.PauseScannerOnCriticalMemory || memory.ClearNonEssentialCachesOnCriticalMemory
                ? "PauseScannerAndClearCaches"
                : "LogOnly";
            if (now - _lastCriticalLoggedAt > TimeSpan.FromSeconds(30))
            {
                _lastCriticalLoggedAt = now;
                Console.WriteLine($"[MEMORY_CRITICAL] ProcessMb={processMb:0.##} Action={action}");
            }

            if (memory.PauseScannerOnCriticalMemory)
            {
                lock (_gate) _pausedUntilUtc = now.AddSeconds(60);
                state.SetControls(state.Controls with { IsPaused = true, Reason = "MEMORY_CRITICAL", UpdatedAtUtc = now, Sequence = state.NextSeq() });
                state.SetStatus(state.Status with { ScannerActive = false, LastScanTime = now });
            }

            if (memory.ClearNonEssentialCachesOnCriticalMemory)
            {
                state.ClearNonEssentialRuntimeState();
                clearNonEssentialCaches?.Invoke();
            }

            if (memory.WriteMemorySnapshotOnCritical && !string.IsNullOrWhiteSpace(contentRootPath))
                WriteMemorySnapshot(contentRootPath, state);

            if (memory.ForceGcOnCriticalMemory)
            {
                GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
                GC.WaitForPendingFinalizers();
            }

            return;
        }

        if (processMb >= memory.WarningProcessMemoryMb && now - _lastWarningLoggedAt > TimeSpan.FromMinutes(1))
        {
            _lastWarningLoggedAt = now;
            Console.WriteLine($"[MEMORY_WARNING] ProcessMb={processMb:0.##}");
        }

        if (!IsScannerPausedByMemory && state.Controls.Reason == "MEMORY_CRITICAL")
            state.SetControls(state.Controls with { IsPaused = false, Reason = "RUNNING", UpdatedAtUtc = now, Sequence = state.NextSeq() });
    }

    private static double GetProcessMemoryMb() => Process.GetCurrentProcess().WorkingSet64 / 1024d / 1024d;

    private static void WriteMemorySnapshot(string contentRootPath, BotRuntimeState state)
    {
        try
        {
            var path = Path.Combine(contentRootPath, "exports/runtime-memory-snapshot-latest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var p = Process.GetCurrentProcess();
            var payload = new
            {
                timestamp = DateTime.UtcNow,
                memory = RuntimeHealthSnapshot.From(state),
                collectionCounts = state.GetRuntimeCollectionCounts(),
                topCacheSizes = new { orderbookCacheCount = state.OrderbookCacheCount, marketCacheCount = state.MarketCacheCount },
                scannerState = new { state.ScannerStats.Sequence, state.ScannerStats.LastScanDurationMs, state.Controls.IsPaused, state.Controls.Reason },
                lastLogsSummary = state.Logs().TakeLast(20).Select(l => new { l.Timestamp, l.Level, l.Source, Message = l.Message.Length > 240 ? l.Message[..240] : l.Message }),
                process = new { p.Threads.Count, p.HandleCount, p.PrivateMemorySize64, p.WorkingSet64 }
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MEMORY_SNAPSHOT_ERROR] Message={ex.Message.Replace(' ', '_')}");
        }
    }
}
