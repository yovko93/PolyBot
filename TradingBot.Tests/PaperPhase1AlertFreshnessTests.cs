using TradingBot.Options;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public sealed class PaperPhase1AlertFreshnessTests
{
    [Fact]
    public void Clean_positive_alert_ttl_defaults_to_ten_minutes()
    {
        Assert.Equal(600, new PaperPhase1Options().CleanPositiveAlertTtlSeconds);
    }

    [Fact]
    public void Readiness_contract_separates_alert_and_clean_positive_freshness()
    {
        var properties = typeof(PaperPhase1RealReadinessState).GetProperties().Select(x => x.Name).ToHashSet();

        foreach (var name in new[] { "AlertFresh", "AlertAgeSeconds", "AlertLastCleanPositiveUtc",
                     "AlertStaleSuppressedCount", "AlertDowngradeReason", "CleanPositiveAlertTtlSeconds",
                     "CleanPositiveTtlCount", "CleanPositiveLastSeenUtc", "CleanPositiveAgeSeconds" })
            Assert.Contains(name, properties);
    }
}
