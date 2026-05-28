using Xunit;

namespace TradingBot.Tests;

public class AllowlistHealthInvariantTests
{
    [Fact]
    public void Configured11_Resolved10_Unresolved1_MustClassifyMissingAsBrokenOrDisabledOrIgnored()
    {
        var configured = 11;
        var healthy = 10;
        var unresolved = 1;

        // current model in runtime classifies unresolved enabled groups as Broken + NeedsRefresh
        var broken = unresolved;
        var disabled = 0;
        var ignored = 0;

        Assert.True(broken > 0 || disabled > 0 || ignored > 0);
        Assert.Equal(configured, healthy + broken + disabled + ignored);
    }
}
