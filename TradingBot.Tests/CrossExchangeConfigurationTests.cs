using Microsoft.Extensions.Configuration;
using TradingBot.Options;
using TradingBot.Services.CrossExchange;
using TradingBot.Services.Kalshi;

public class CrossExchangeConfigurationTests
{
    [Fact]
    public void CrossExchange_Defaults_Are_Conservative_And_Paper_First()
    {
        var options = new CrossExchangeOptions();
        Assert.False(options.Enabled);
        Assert.True(options.PaperOnly);
        Assert.True(options.UseOnlyVerifiedMarketPairs);
        Assert.True(options.MaxOrderbookAgeMs > 0);
    }

    [Fact]
    public void TradingBot_Default_LiveExecution_Is_Disabled()
    {
        var options = new TradingBotOptions();
        Assert.False(options.EnableLiveExecution);
        Assert.True(options.EnablePaperTrading);
    }

    [Fact]
    public void Appsettings_Binds_Kalshi_And_CrossExchange_Sections()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "TradingBot"))
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var kalshi = new KalshiOptions();
        var cross = new CrossExchangeOptions();

        config.GetSection(KalshiOptions.SectionName).Bind(kalshi);
        config.GetSection(CrossExchangeOptions.SectionName).Bind(cross);

        Assert.Equal("https://external-api.kalshi.com/trade-api/v2", kalshi.BaseUrl);
        Assert.False(cross.Enabled);
        Assert.True(cross.PaperOnly);
    }

    [Fact]
    public void MarketPairLoader_Skips_Invalid_Entries()
    {
        var temp = Path.GetTempFileName();
        File.WriteAllText(temp, """
[
  { "enabled": true, "canonicalKey": "ok", "polymarketMarketId": "pm", "kalshiTicker": "kal", "description": "d", "riskLevel": "Verified" },
  { "enabled": true, "canonicalKey": "bad", "polymarketMarketId": "", "kalshiTicker": "kal", "description": "d", "riskLevel": "Verified" }
]
""");

        var loader = new MarketPairConfigLoader(temp);
        var pairs = loader.Load();

        Assert.Single(pairs);
        Assert.Equal("ok", pairs[0].CanonicalKey);
    }
}
