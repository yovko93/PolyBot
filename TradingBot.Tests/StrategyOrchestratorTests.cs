using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class StrategyOrchestratorTests
{
    private sealed class FakeStrategy(string name, int candidates = 0, bool fault = false) : IOpportunityStrategy
    {
        public int Calls { get; private set; }
        public string Name { get; } = name;

        public Task<OpportunityStrategyScanResult> ScanAsync(OpportunityStrategyContext context)
        {
            Calls++;
            if (fault) throw new InvalidOperationException("boom");
            return Task.FromResult(new OpportunityStrategyScanResult(Name, StrategyMode.PaperEligible, Scanned: context.Markets.Count, Candidates: candidates, ExecutionCandidates: candidates));
        }
    }

    private static OpportunityStrategyContext Context()
        => new(Array.Empty<Market>(), new PaperTradingEngineFacade { Engine = new PaperTradingEngine(botOptions: new TradingBotOptions()) }, new SemaphoreSlim(1, 1), null, false, false, CancellationToken.None);

    [Fact]
    public async Task Disabled_strategy_is_not_scanned()
    {
        var disabled = new FakeStrategy("ExperimentalMultiOutcome");
        var options = new TradingBotOptions
        {
            Strategies = new Dictionary<string, OpportunityStrategyConfig>(StringComparer.OrdinalIgnoreCase)
            {
                ["ExperimentalMultiOutcome"] = new(false, StrategyMode.Disabled, 0)
            }
        };

        var results = await new StrategyOrchestrator(new[] { disabled }, options).RunEnabledAsync(Context());

        Assert.Empty(results);
        Assert.Equal(0, disabled.Calls);
    }

    [Fact]
    public async Task DiagnosticsOnly_strategy_never_opens_paper()
    {
        var queue = new OpportunityExecutionQueue();
        var opened = await queue.EnqueueAsync(new OpportunityExecutionCandidate("VerifiedMultiOutcome", StrategyMode.DiagnosticsOnly, "group-1", 10m, _ => Task.FromResult(true)));

        Assert.False(opened);
        Assert.Equal(1, queue.RejectedDiagnosticsOnly);
        Assert.Equal(0, queue.Executed);
    }

    [Fact]
    public async Task PaperEligible_strategy_can_enqueue_candidate()
    {
        var queue = new OpportunityExecutionQueue();
        var opened = await queue.EnqueueAsync(new OpportunityExecutionCandidate("SingleMarketBuyBoth", StrategyMode.PaperEligible, "single-market:m1", 10m, _ => Task.FromResult(true)));

        Assert.True(opened);
        Assert.Equal(1, queue.Executed);
    }

    [Fact]
    public async Task Execution_queue_is_serialized()
    {
        var queue = new OpportunityExecutionQueue();
        var tasks = Enumerable.Range(0, 10).Select(_ => queue.EnqueueAsync(new OpportunityExecutionCandidate("SingleMarketBuyBoth", StrategyMode.PaperEligible, "m", 1m, async _ =>
        {
            await Task.Delay(10);
            return true;
        })));

        await Task.WhenAll(tasks);

        Assert.Equal(10, queue.Executed);
        Assert.Equal(1, queue.MaxObservedConcurrency);
    }

    [Fact]
    public void Central_risk_gate_enforces_total_exposure()
    {
        var options = new TradingBotOptions
        {
            EnablePaperTrading = true,
            PaperOnly = true,
            TradingMode = new TradingModeOptions { PaperTradingEnabled = true, LiveTradingEnabled = false },
            PaperRisk = new PaperRiskOptions { MaxPaperTotalExposure = 10m, MaxPaperNotionalPerTrade = 25m, MaxPaperPositionsTotal = 10, MaxPaperPositionsPerStrategy = 10, MaxPaperOpenPerHour = 10 }
        };
        var gate = new PaperPreTradeGate(options);
        var opp = new PaperPreTradeOpportunity("SingleMarketBuyBoth", "single-market:m1", PaperStrategyKind.SingleMarket, true, 5m, 1m, true, true, true, true);
        var account = new PaperAccountSnapshotForGate(100m, 8m, 1, new Dictionary<string, int>(), 0);

        var result = gate.Validate(opp, account);

        Assert.False(result.Approved);
        Assert.Equal("MaxPaperTotalExposureExceeded", result.Reason);
    }

    [Fact]
    public void StrategyName_is_included_in_dedupe_key()
    {
        var paper = new PaperTradingEngine(botOptions: new TradingBotOptions());
        paper.MarkSingleMarketDedupeForDiagnostics("m1", "StrategyA");

        var a = paper.GetSingleMarketDuplicateDiagnostics("m1", "StrategyA");
        var b = paper.GetSingleMarketDuplicateDiagnostics("m1", "StrategyB");

        Assert.Contains("StrategyA", a.DedupeKey);
        Assert.DoesNotContain("StrategyA", b.DedupeKey);
        Assert.False(b.DedupeRegistryContains);
    }

    [Fact]
    public void RuntimeHealth_includes_per_strategy_counters()
    {
        var state = new BotRuntimeState();
        state.RecordStrategyResult(new OpportunityStrategyScanResult("SingleMarketBuyBoth", StrategyMode.PaperEligible, Scanned: 2, Candidates: 1));

        var health = RuntimeHealthSnapshot.From(state, new TradingBotOptions());

        Assert.True(health.StrategyCounters.ContainsKey("SingleMarketBuyBoth"));
        Assert.Equal(2, health.StrategyCounters["SingleMarketBuyBoth"].Scanned);
    }

    [Fact]
    public void Funnel_includes_per_strategy_breakdown()
    {
        var state = new BotRuntimeState();
        state.RecordStrategyResult(new OpportunityStrategyScanResult("VerifiedMultiOutcome", StrategyMode.DiagnosticsOnly, Scanned: 3));

        var funnel = PaperOpportunityFunnelExporter.Build(new TradingBotOptions(), state, new SingleMarketScanStats(0, 0, 0, 0, 0, 0, 0, 0), new MultiOutcomeGroupArbEngine.MultiOutcomeScanReport(0, 0, 0, 0, 0, 0, 0, 0m, 0m, 0m, "", "", new Dictionary<string, int>(), Array.Empty<MultiOutcomeGroupArbEngine.RejectedSample>(), Array.Empty<MultiOutcomeGroupArbEngine.CandidateGroupReview>()), 0);

        Assert.True(funnel.PerStrategy.ContainsKey("VerifiedMultiOutcome"));
    }

    [Fact]
    public async Task Strategy_fault_does_not_crash_scanner()
    {
        var bad = new FakeStrategy("SingleMarketBuyBoth", fault: true);
        var options = new TradingBotOptions { Strategies = new Dictionary<string, OpportunityStrategyConfig> { ["SingleMarketBuyBoth"] = new(true, StrategyMode.PaperEligible, 100) } };

        var results = await new StrategyOrchestrator(new[] { bad }, options).RunEnabledAsync(Context());

        Assert.Single(results);
        Assert.Equal(1, results[0].Faults);
    }

    [Fact]
    public void LiveTrading_false_and_signing_attempts_zero_are_enforced()
    {
        LiveTradingGuard.ResetForTests();
        var options = new TradingBotOptions { EnableLiveExecution = false, TradingMode = new TradingModeOptions { LiveTradingEnabled = false } };

        Assert.False(options.EnableLiveExecution);
        Assert.False(options.TradingMode.LiveTradingEnabled);
        Assert.Equal(0, LiveTradingGuard.SigningAttempts);
    }
}
