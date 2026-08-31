using System.Text.Json;
using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using TradingBot.Options;
using Xunit;

namespace TradingBot.Tests;

public class VerifiedMultiOutcomeDiscoveryDiagnosticsTests
{
    [Fact]
    public void Publishes_specific_outside_reduced_universe_blocker_and_shadow_contract()
    {
        var root=Path.Combine(Path.GetTempPath(),"verified-discovery-"+Guid.NewGuid().ToString("N"));
        try
        {
            VerifiedMultiOutcomeDiscoveryDiagnostics.Configure(new PaperPhase1Options());
            var group=new ResolvedVerifiedGroup("g1","Group 1",["m1","m2"],[],[],["m1","m2"],[],"Rejected","VerifiedGroupNotFoundInDiscoveredPool");
            VerifiedMultiOutcomeDiscoveryDiagnostics.Observe([group],[],10,5,"ReducedUniverseDiagnosticsOnly");
            VerifiedMultiOutcomeDiscoveryDiagnostics.ObserveCompletion(new(1,0,2,0,0,0,[new("g1","g1",[],["m1","m2"],true,false,2,0,0,0,"VerifiedGroupSiblingMarketLoadFailed")]));
            var snapshot=VerifiedMultiOutcomeDiscoveryDiagnostics.Publish(root,DateTime.UtcNow);
            Assert.Equal("VerifiedGroupOutsideReducedUniverse",snapshot.TopBlocker);
            using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"exports/phase1-verified-multioutcome-discovery-latest.json")));
            Assert.Equal(1,json.RootElement.GetProperty("CandidateGroupCount").GetInt32());
            var sample=json.RootElement.GetProperty("TopSkippedOrBlockedGroups")[0];
            Assert.Equal("VerifiedMultiOutcome",sample.GetProperty("Strategy").GetString());
            Assert.Equal("VerifiedGroupOutsideReducedUniverse",sample.GetProperty("FirstBlockingReason").GetString());
            Assert.True(json.RootElement.GetProperty("ShadowGroupCompletionConfigPresent").GetBoolean());
            Assert.True(json.RootElement.GetProperty("ShadowGroupCompletionEnabled").GetBoolean());
            using var completion=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"exports/phase1-verified-multioutcome-completion-latest.json")));
            Assert.Equal(1,completion.RootElement.GetProperty("Counters").GetProperty("Attempted5m").GetInt64());
            Assert.False(completion.RootElement.GetProperty("PaperOpenAllowed").GetBoolean());
        }
        finally { if(Directory.Exists(root))Directory.Delete(root,true); }
    }
}
