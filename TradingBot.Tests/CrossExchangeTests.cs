using TradingBot.Models.Kalshi;
using TradingBot.Models.Normalized;
using TradingBot.Options;
using TradingBot.Services.CrossExchange;
using TradingBot.Services.Kalshi;

public class CrossExchangeTests
{
    [Fact]
    public void KalshiNormalizer_Derives_Asks()
    {
        var ob = new KalshiOrderbook([new KalshiLevel(61, 10)], [new KalshiLevel(37, 7)]);
        var n = KalshiOrderbookNormalizer.Normalize("M","T",ob,"src");
        Assert.Equal(0.61m, n.BestYesBid);
        Assert.Equal(0.63m, n.BestYesAsk);
        Assert.Equal(7m, n.YesAskQuantity);
    }

    [Fact]
    public void Engine_Executable_When_NetCost_Below_One()
    {
        var opt = new CrossExchangeOptions{Enabled=true, MinCrossExchangeEdge=0.001m, MinCrossExchangeExpectedProfit=0.001m};
        var fees = new ExchangeFeesOptions();
        var e = new CrossExchangeArbitrageEngine(opt, fees);
        var pair = new CrossExchangeMarketPair(true,"k","d","pm",null,null,"kt",null,MarketPairRiskLevel.Verified,null);
        var p = new ExchangeOrderbook("POLY","pm","",0.6m,0.45m,0.5m,0.4m,100,100,DateTime.UtcNow,"active","");
        var k = new ExchangeOrderbook("KAL","k","",0.6m,0.44m,0.55m,0.4m,100,100,DateTime.UtcNow,"active","");
        Assert.Contains(e.Evaluate(pair,p,k,0), x => x.IsExecutable);
    }
}
