using Xunit;
using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using TradingBot.Options;

namespace TradingBot.Tests;

public class VerifiedStabilityAndAllowlistTests
{
    private static VerifiedBasketScreener.ScreenResult Row(string group, decimal net, bool near=false)
    {
        var profiles = new [] {
            new VerifiedBasketScreener.ProfileResult("Conservative","",0,0,0,net,net,net>0,false),
            new VerifiedBasketScreener.ProfileResult("PolymarketApprox","",0,0,0,net,net,net>0,true),
            new VerifiedBasketScreener.ProfileResult("RawOnly","",0,0,0,net,net,net>0,true)
        };
        return new(group,2,1,0.99m,0.01m,net,net>0?1:0,net,"","", "Conservative", profiles, new[]{ new VerifiedBasketScreener.QuantityScenarioResult(1,false,"",0,0,0,net,net,"leg",1)}, "", DateTime.UtcNow, 0, near, "", Array.Empty<string>(), net, VerifiedBasketScreener.ExecutionStatus.NotExecutable);
    }

    [Fact] public void One_positive_is_pending_not_stable(){ var t=new VerifiedOpportunityStabilityTracker(); var st=t.Track("g",Row("g",0.003m),200,3,0.001m,0.002m); Assert.Equal(VerifiedBasketState.EdgeExecutablePending, st);}    
    [Fact] public void Three_positive_is_stable(){ var t=new VerifiedOpportunityStabilityTracker(); t.Track("g",Row("g",0.003m),200,3,0.001m,0.002m); t.Track("g",Row("g",0.0031m),200,3,0.001m,0.002m); var st=t.Track("g",Row("g",0.0032m),200,3,0.001m,0.002m); Assert.Equal(VerifiedBasketState.EdgeStable, st);}    
    [Fact] public void Positive_then_negative_resets(){ var t=new VerifiedOpportunityStabilityTracker(); t.Track("g",Row("g",0.003m),200,3,0.001m,0.002m); var st=t.Track("g",Row("g",-0.001m),200,3,0.001m,0.002m); Assert.Equal(0,t.Consecutive("g")); Assert.Equal(VerifiedBasketState.NotExecutable, st);}    
    [Fact] public void History_is_bounded(){ var t=new VerifiedOpportunityStabilityTracker(); for(int i=0;i<10;i++) t.Track("g",Row("g",0.001m+i*0.0001m),5,3,0.001m,0.002m); Assert.Equal(5,t.Summaries().Single().Samples.Count);}    
}
