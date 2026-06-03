using Microsoft.Extensions.Options;
using TradingBot.Api;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
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
    public void VerifiedExecutionCoordinator_SoakSimulation_KeepsQueuesBounded()
    {
        var botOptions = Options.Create(new TradingBotOptions
        {
            RuntimeState = new RuntimeStateOptions
            {
                MaxExecutionAuditEvents = 500,
                MaxDryRunOrderPlans = 100,
                MaxFillSimulations = 100
            }
        });
        var coordinator = new VerifiedBasketExecutionCoordinator(Options.Create(new ExecutionOptions()), botOptions);

        for (var i = 0; i < 1000; i++)
        {
            coordinator.Audit(new ExecutionAuditEvent(DateTime.UtcNow, $"opp{i}", "g", "s", "stage", "status", "reason", 0, 0, 0, 0, ""));
            coordinator.RecordDryRunPlan(new BasketOrderPlan($"p{i}", $"opp{i}", "g", "t", "s", "Conservative", true, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1), BasketOrderPlanStatus.PaperOnly, 0, 0, 0, 0, 0, 0, 0, 0, Array.Empty<OrderIntent>(), Array.Empty<string>(), Array.Empty<string>()));
            coordinator.RecordFillSimulation(new FillSimulationResult($"f{i}", $"p{i}", "g", "s", DateTime.UtcNow, FillSimulationStatus.FullyFillable, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, "Conservative", false, true, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<LegFillSimulation>()));
        }

        Assert.True(coordinator.AuditCount <= botOptions.Value.RuntimeState.MaxExecutionAuditEvents);
        Assert.True(coordinator.DryRunPlanCount <= botOptions.Value.RuntimeState.MaxDryRunOrderPlans);
        Assert.True(coordinator.FillSimulationCount <= botOptions.Value.RuntimeState.MaxFillSimulations);
    }

    private static ScannerStatsDto MakeStats(long sequence) => new(0,0,0,0,0,0,0,0,0,0,0,0,DateTime.UtcNow,DateTime.UtcNow,null,0,0,0,0,0,0,0,DateTime.UtcNow,DateTime.UtcNow,0,0,0,0,0,0,0,0,0,0,0,0,null,0,0,0,0,0,0,0,null,sequence);
}
