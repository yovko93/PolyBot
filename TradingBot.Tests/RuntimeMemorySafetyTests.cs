using Microsoft.Extensions.Options;
using TradingBot.Api;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class RuntimeMemorySafetyTests
{
    [Fact]
    public void RuntimeState_SoakSimulation_KeepsCollectionsBounded()
    {
        var options = new RuntimeStateOptions
        {
            MaxRecentLogs = 500,
            MaxScannerHistory = 500,
            MaxScannerStatsHistory = 500,
            MaxCandidateSnapshots = 2,
            MaxCandidateGroupsInMemory = 250,
            MaxUnresolvedDiagnostics = 100,
            MaxSignalREventBuffer = 100,
            MaxPaperPositions = 100
        };
        var state = new BotRuntimeState(options);

        for (var i = 0; i < 1000; i++)
        {
            state.AddLog(new TerminalLogEntryDto(i.ToString(), DateTime.UtcNow, "info", "test", "message", i));
            state.SetScannerStats(MakeStats(i));
            state.SetMultiOutcomeCandidates(Enumerable.Range(0, 500).Select(x => (object)new { x }));
            state.AddUnresolvedDiagnostics(Enumerable.Range(0, 3).Select(x => (object)new { cycle = i, x }));
            state.AddSignalREvent("test");
            state.ReplaceOpportunities(Enumerable.Range(0, 300).Select(x => new OpportunityDto($"o{x}", DateTime.UtcNow, x, "s", "g", "l", "BOTH", 0, 0, 0, 0, 0, false, "SKIPPED", null, x)));
        }

        Assert.True(state.Logs().Length <= options.MaxRecentLogs);
        Assert.True(state.ScannerStatsHistoryCount <= options.MaxScannerHistory);
        Assert.True(state.CandidateSnapshotCount <= options.MaxCandidateSnapshots);
        Assert.True(state.MultiOutcomeCandidates.Length <= options.MaxCandidateGroupsInMemory);
        Assert.True(state.UnresolvedDiagnosticsCount <= options.MaxUnresolvedDiagnostics);
        Assert.True(state.SignalREventBufferCount <= options.MaxSignalREventBuffer);
        Assert.True(state.Opportunities().Length <= options.MaxCandidateGroupsInMemory);
    }

    [Fact]
    public void RuntimeHealthSnapshot_ReturnsCollectionCounts_AndLogLine()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions { MaxRecentLogs = 500, MaxSignalREventBuffer = 100 });
        state.AddLog(new TerminalLogEntryDto("1", DateTime.UtcNow, "info", "test", "log", 1));
        state.AddSignalREvent("test");

        var health = RuntimeHealthSnapshot.From(state);

        Assert.True(health.ProcessMemoryMb > 0);
        Assert.Equal(1, health.RecentLogsCount);
        Assert.Equal(1, health.SignalREventBufferCount);
        Assert.Contains("[RUNTIME_HEALTH]", health.ToLogLine());
        Assert.Contains("ProcessMb=", health.ToLogLine());
    }

    [Fact]
    public void MemoryGuard_ClearsNonEssentialState_OnCriticalMemory()
    {
        var state = new BotRuntimeState(new RuntimeStateOptions { MaxRecentLogs = 500, MaxCandidateSnapshots = 2, MaxUnresolvedDiagnostics = 100, MaxSignalREventBuffer = 100 });
        state.AddLog(new TerminalLogEntryDto("1", DateTime.UtcNow, "info", "test", "log", 1));
        state.SetMultiOutcomeCandidates(Enumerable.Range(0, 10).Select(x => (object)new { x }));
        state.AddUnresolvedDiagnostics(Enumerable.Range(0, 10).Select(x => (object)new { x }));
        state.AddSignalREvent("test");
        var guard = new MemoryGuard();
        var options = new TradingBotOptions
        {
            RuntimeMemory = new RuntimeMemoryOptions
            {
                CriticalProcessMemoryMb = 0,
                MaxProcessMemoryMb = 0,
                PauseScannerOnCriticalMemory = true,
                ClearNonEssentialCachesOnCriticalMemory = true,
                ForceGcOnCriticalMemory = false,
                WriteMemorySnapshotOnCritical = false
            }
        };
        var cleared = false;

        guard.Check(state, options, () => cleared = true);

        Assert.True(cleared);
        Assert.True(state.Controls.IsPaused);
        Assert.Equal("MEMORY_CRITICAL", state.Controls.Reason);
        Assert.Equal(0, state.UnresolvedDiagnosticsCount);
        Assert.Equal(0, state.CandidateSnapshotCount);
        Assert.Equal(0, state.SignalREventBufferCount);
    }

    [Fact]
    public void SignalRPayloadGuard_TrimsLargePayloads()
    {
        var result = SignalRPayloadGuard.Trim(Enumerable.Range(0, 200).Select(x => new { id = x, text = new string('x', 1000) }), new SignalROptions { MaxPayloadItems = 100, MaxPayloadBytes = 16_384 });

        Assert.True(result.Trimmed);
        Assert.Equal(200, result.ItemsBefore);
        Assert.True(result.ItemsAfter <= 100);
        Assert.True(result.PayloadBytes <= 16_384);
    }

    [Fact]
    public void RejectedOnlyCandidateScans_AreSuppressedInOperationalQuietMode_WhenOnlyCountsChange()
    {
        var reasonsA = new Dictionary<string, int> { ["Rejected"] = 18 };
        var reasonsB = new Dictionary<string, int> { ["Rejected"] = 14 };
        var last = ScanLogSummaryService.RejectedOnlyCandidateScanFingerprint("Rejected", reasonsA, 15);
        var current = ScanLogSummaryService.RejectedOnlyCandidateScanFingerprint("Rejected", reasonsB, 15);

        Assert.True(ScanLogSummaryService.ShouldSuppressRejectedOnlyCandidateScan(true, false, true, current, last, periodic: false));
    }


    [Fact]
    public void RealConfigDuplicateGroupKey_DoesNotThrowAndKeepsOneGroup()
    {
        var temp = Directory.CreateTempSubdirectory("allowlist-duplicate-config-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, "config"));
            File.WriteAllText(Path.Combine(temp, "config", "verified-multi-outcome-groups.json"), """
            [
              { "enabled": true, "groupKey": "dup", "verificationStatus": "Verified", "allowedStrategy": "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", "marketIds": ["m1"], "conditionIds": ["c1"] },
              { "enabled": true, "groupKey": "dup", "verificationStatus": "Verified", "allowedStrategy": "BUY_ALL_NO_MUTUALLY_EXCLUSIVE", "marketIds": ["m2"], "conditionIds": ["c2"] }
            ]
            """);

            var validator = new MutuallyExclusiveGroupValidator(new MultiOutcomeArbitrageOptions(), temp);

            Assert.Equal(1, validator.LoadedAllowlistCount);
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static ScannerStatsDto MakeStats(long sequence) => new(0,0,0,0,0,0,0,0,0,0,0,0,DateTime.UtcNow,DateTime.UtcNow,null,0,0,0,0,0,0,0,DateTime.UtcNow,DateTime.UtcNow,0,0,0,0,0,0,0,0,0,0,0,0,null,0,0,0,0,0,0,0,null,sequence);
}
