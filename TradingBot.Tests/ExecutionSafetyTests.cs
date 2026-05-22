using Microsoft.Extensions.Options;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;

namespace TradingBot.Tests;

public class ExecutionSafetyTests
{
    private static (PreTradeValidator v, IRiskManager r) Build(ExecutionOptions? opt = null)
    {
        var o = Options.Create(opt ?? new ExecutionOptions());
        var r = new RiskManager(o);
        return (new PreTradeValidator(o, r, new DuplicateExecutionGuard()), r);
    }

    [Fact] public void Rejects_negative_edge(){ var (v,_) = Build(); var res=v.Validate(BaseOpp() with { EdgePerShare=-0.1m},[]); Assert.False(res.IsValid); }
    [Fact] public void Rejects_zero_edge(){ var (v,_) = Build(); var res=v.Validate(BaseOpp() with { EdgePerShare=0m},[]); Assert.False(res.IsValid); }
    [Fact] public void Rejects_stale_book(){ var (v,_) = Build(); var res=v.Validate(BaseOpp() with { OrderbookTimestampUtc=DateTime.UtcNow.AddMinutes(-5)},[]); Assert.Equal(RejectionReason.StaleOrderbook,res.RejectionReason); }
    [Fact] public void Rejects_over_notional(){ var (v,_) = Build(); var res=v.Validate(BaseOpp() with { Quantity=1000m },[]); Assert.Equal(RejectionReason.MaxNotionalPerTrade,res.RejectionReason); }
    [Fact] public void Rejects_kill_switch(){ var (v,_) = Build(new ExecutionOptions{KillSwitchEnabled=true}); var res=v.Validate(BaseOpp(),[]); Assert.Equal(RejectionReason.KillSwitchEnabled,res.RejectionReason); }
    [Fact] public void Approves_valid(){ var (v,_) = Build(); var res=v.Validate(BaseOpp(),[]); Assert.True(res.IsValid); }

    [Fact] public void Risk_tracks_daily_notional(){ var o=Options.Create(new ExecutionOptions()); var r=new RiskManager(o); var builder=new OrderPlanBuilder(o); var vres=new PreTradeValidationResult(true,PreTradeDecision.Approved,RejectionReason.None,[],0.01m,1,1,DateTime.UtcNow); var p=builder.Build(BaseOpp(),vres); r.RegisterPlannedExecution(p); Assert.True(r.GetRiskSnapshot().DailyNotional>0); }
    [Fact] public void Risk_blocks_exposure(){ var o=Options.Create(new ExecutionOptions{MaxExposurePerMarket=1m}); var r=new RiskManager(o); Assert.False(r.CanOpenPosition(BaseOpp() with {Quantity=10m}, out _)); }

    [Fact] public void Plan_deterministic_id(){ var id1=OrderPlanBuilder.DeterministicId("a"); var id2=OrderPlanBuilder.DeterministicId("a"); Assert.Equal(id1,id2); }
    [Fact] public void Dryrun_simulates(){ var ex=new DryRunLiveExecutor(new ExecutionAuditLog()); var o=Options.Create(new ExecutionOptions()); var builder=new OrderPlanBuilder(o); var p=builder.Build(BaseOpp(), new PreTradeValidationResult(true,PreTradeDecision.Approved,RejectionReason.None,[],0.01m,1,1,DateTime.UtcNow)); var res=ex.Execute(p); Assert.Equal(ExecutionResultStatus.Simulated,res.Status); }
    [Fact] public void Duplicate_protected(){ var (v,_) = Build(); var set=new HashSet<string>(); var o=BaseOpp(); var a=v.Validate(o,set); set.Add(o.OpportunityId); var b=v.Validate(o,set); Assert.True(a.IsValid); Assert.False(b.IsValid); }
    [Fact] public void Live_mode_disabled_by_default(){ Assert.False(new ExecutionOptions().EnableLiveTrading); }
    [Fact] public void Live_mode_rejected_when_not_enabled(){ var (v,_) = Build(new ExecutionOptions{Mode=ExecutionMode.Live,EnableLiveTrading=false}); var res=v.Validate(BaseOpp(),[]); Assert.Equal(RejectionReason.LiveTradingDisabled,res.RejectionReason); }

    private static ExecutionOpportunity BaseOpp() => new("opp-1","S","m1",0.02m,1m,1m,0.5m,DateTime.UtcNow, true, true,0.001m,0.001m);
}
