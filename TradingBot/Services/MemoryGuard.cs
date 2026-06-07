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
    private DateTime _lastGcForCriticalAt = DateTime.MinValue;

    public DateTime PausedUntilUtc { get { lock (_gate) return _pausedUntilUtc; } }

    public bool IsScannerPausedByMemory => PausedUntilUtc > DateTime.UtcNow;

    public bool ShouldSkipScannerCycle() => IsScannerPausedByMemory;

    public void Check(BotRuntimeState state, TradingBotOptions options, Action? clearNonEssentialCaches = null, string? contentRootPath = null, double? processMemoryMbOverride = null)
    {
        var memory = options.RuntimeMemory;
        var processMb = processMemoryMbOverride ?? GetProcessMemoryMb();
        var now = DateTime.UtcNow;
        var criticalThreshold = Math.Min(memory.CriticalProcessMemoryMb, memory.MaxProcessMemoryMb <= 0 ? memory.CriticalProcessMemoryMb : memory.MaxProcessMemoryMb);

        if (IsScannerPausedByMemory)
        {
            if (processMb <= memory.ResumeBelowProcessMemoryMb)
            {
                lock (_gate) _pausedUntilUtc = DateTime.MinValue;
                state.SetScannerPausedByMemoryGuard(false);
                if (state.Controls.Reason == "MEMORY_CRITICAL")
                    state.SetControls(state.Controls with { IsPaused = false, Reason = "RUNNING", UpdatedAtUtc = now, Sequence = state.NextSeq() });
            }
            else if (now >= PausedUntilUtc)
            {
                lock (_gate) _pausedUntilUtc = now.AddSeconds(Math.Max(1, memory.CriticalPauseSeconds));
                Console.WriteLine($"[MEMORY_CRITICAL_PAUSED] ProcessMb={processMb:0.##} Reason=MemoryDidNotRecover");
            }
        }

        if (processMb >= criticalThreshold)
        {
            var action = memory.PauseScannerOnCriticalMemory || memory.ClearNonEssentialCachesOnCriticalMemory || memory.ClearCachesOnMemoryCritical
                ? "PauseScannerAndClearCaches"
                : "LogOnly";
            if (now - _lastCriticalLoggedAt > TimeSpan.FromSeconds(30))
            {
                _lastCriticalLoggedAt = now;
                state.RecordMemoryCritical(now, memory.PauseScannerOnCriticalMemory);
                Console.WriteLine($"[MEMORY_CRITICAL] ProcessMb={processMb:0.##} Action={action}");
                Console.WriteLine(BuildDiagnosticLine(state, processMb));
            }

            if (memory.PauseScannerOnCriticalMemory)
            {
                lock (_gate) _pausedUntilUtc = now.AddSeconds(Math.Max(1, memory.CriticalPauseSeconds));
                state.SetScannerPausedByMemoryGuard(true);
                state.SetControls(state.Controls with { IsPaused = true, Reason = "MEMORY_CRITICAL", UpdatedAtUtc = now, Sequence = state.NextSeq() });
                state.SetStatus(state.Status with { ScannerActive = false, LastScanTime = now });
            }

            if (memory.ClearNonEssentialCachesOnCriticalMemory || memory.ClearCachesOnMemoryCritical)
            {
                state.ClearNonEssentialRuntimeState();
                clearNonEssentialCaches?.Invoke();
            }

            if (memory.WriteMemorySnapshotOnCritical && !string.IsNullOrWhiteSpace(contentRootPath))
                WriteMemorySnapshot(contentRootPath, state);

            if (memory.ForceGcOnCriticalMemory && now - _lastGcForCriticalAt > TimeSpan.FromMinutes(1))
            {
                _lastGcForCriticalAt = now;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                var afterMb = GetProcessMemoryMb();
                if (afterMb > memory.ResumeBelowProcessMemoryMb)
                    Console.WriteLine($"[MEMORY_CRITICAL_PAUSED] ProcessMb={afterMb:0.##} Reason=MemoryDidNotRecover");
            }

            return;
        }

        if (processMb >= memory.WarningProcessMemoryMb && now - _lastWarningLoggedAt > TimeSpan.FromMinutes(1))
        {
            _lastWarningLoggedAt = now;
            state.RecordMemoryWarning();
            Console.WriteLine($"[MEMORY_WARNING] ProcessMb={processMb:0.##}");
            Console.WriteLine(BuildDiagnosticLine(state, processMb));
        }

        if (!IsScannerPausedByMemory && state.Controls.Reason == "MEMORY_CRITICAL" && processMb <= memory.ResumeBelowProcessMemoryMb)
        {
            state.SetScannerPausedByMemoryGuard(false);
            state.SetControls(state.Controls with { IsPaused = false, Reason = "RUNNING", UpdatedAtUtc = now, Sequence = state.NextSeq() });
        }
    }

    private static double GetProcessMemoryMb() => Process.GetCurrentProcess().WorkingSet64 / 1024d / 1024d;

    private static string BuildDiagnosticLine(BotRuntimeState state, double processMb)
    {
        var p = Process.GetCurrentProcess();
        var gcInfo = GC.GetGCMemoryInfo();
        var lohMb = gcInfo.GenerationInfo.Length > 3 ? gcInfo.GenerationInfo[3].SizeAfterBytes / 1024d / 1024d : 0;
        var fragmentedMb = gcInfo.FragmentedBytes / 1024d / 1024d;
        return $"[MEMORY_DIAG] ProcessMb={processMb:0.##} GcMb={GC.GetTotalMemory(false) / 1024d / 1024d:0.##} WorkingSetMb={p.WorkingSet64 / 1024d / 1024d:0.##} Gen0={GC.CollectionCount(0)} Gen1={GC.CollectionCount(1)} Gen2={GC.CollectionCount(2)} LohMb={lohMb:0.##} FragmentedMb={fragmentedMb:0.##} OrderbookCache={state.OrderbookCacheCount} MarketCache={state.MarketCacheCount} InvalidTokenCache={state.OrderBookServiceStats.QuarantinedTokens} BatchBookErrorHistory={state.OrderBookServiceStats.BatchBookErrorSampleCount} QuietLogGateCache={state.QuietLogGateStats.LogGateCacheSize} CandidateSnapshots={state.CandidateSnapshotCount} RepairHistory={state.RepairHistoryCount} ScannerHistory={state.ScannerStatsHistoryCount} RecentLogs={state.Logs().Length} SignalRBuffer={state.SignalREventBufferCount} SingleMarketOpportunities={state.SingleMarketOpportunitiesCount} HttpErrorSamples={state.OrderBookServiceStats.BatchBookErrorSampleCount} ExportSnapshots={state.ExportQueueCount}";
    }

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
                topCacheSizes = new { orderbookCacheCount = state.OrderbookCacheCount, marketCacheCount = state.MarketCacheCount, invalidTokenCacheCount = state.OrderBookServiceStats.QuarantinedTokens },
                scannerState = new { state.ScannerStats.Sequence, state.ScannerStats.LastScanDurationMs, state.Controls.IsPaused, state.Controls.Reason, state.ScannerPausedByMemoryGuard },
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
