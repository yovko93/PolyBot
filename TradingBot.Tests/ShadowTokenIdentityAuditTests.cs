using System.Text.Json;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public sealed class ShadowTokenIdentityAuditTests
{
    [Fact]
    public void Filters_closed_markets_and_exports_clob_identity_without_affecting_execution()
    {
        var root=Path.Combine(Path.GetTempPath(),"shadow-token-audit-"+Guid.NewGuid().ToString("N"));
        try
        {
            SafeExportWriter.Configure(new JsonlExportOptions { MinFreeDiskMb=0,CriticalFreeDiskMb=0,RetentionEnabled=false },root);
            var closed=new Market { id="closed",question="Closed",conditionId="c",active=false,closed=true,acceptingOrders=false,enableOrderBook=true,outcomes=["Yes","No"],clobTokenIds=["1","2"] };
            var active=new Market { id="active",question="Active",conditionId="c2",active=true,closed=false,acceptingOrders=true,enableOrderBook=true,outcomes=["Yes","No"],clobTokenIds=["10","20"] };
            ShadowTokenIdentityAudit.ObserveFilter([closed,active]);
            ShadowTokenIdentityAudit.Observe("g",active,false,"Ok",false);

            var snapshot=ShadowTokenIdentityAudit.Publish(root,DateTime.UtcNow);

            Assert.Equal("ClobTokenId",snapshot.RequestIdentifierType);
            Assert.Equal(0,snapshot.RequestIdentifierTypeMismatch5m);
            Assert.Equal(1,snapshot.GroupsFilteredClosed5m);
            Assert.Equal(1,snapshot.GroupsEligibleForOrderbookPrefetch5m);
            Assert.Equal(2,snapshot.ActuallyMissingOrderbook5m);
            using var audit=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"exports/latest/shadow-token-identity-audit.json")));
            Assert.Equal(2,audit.RootElement.GetProperty("RequestedTokenCount").GetInt32());
            using var samples=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"exports/debug/shadow-token-identity-audit/top-missing-tokens.json")));
            Assert.Equal("clobTokenIds",samples.RootElement[0].GetProperty("TokenIdSourceField").GetString());
        }
        finally { if(Directory.Exists(root)) Directory.Delete(root,true); }
    }
}
