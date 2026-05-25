using System.Net;
using Microsoft.Extensions.Configuration;
using TradingBot.Options;
using TradingBot.Services;
using TradingBot.Models;
using Xunit;

public class ScannerDiagnosticsTests
{
    [Fact]
    public void Scanner_Options_Bind_MarketScanLimit_1000()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string,string?> { ["TradingBot:MarketScanLimit"] = "1000" })
            .Build();
        var options = new TradingBotOptions();
        config.GetSection(TradingBotOptions.SectionName).Bind(options);
        Assert.Equal(1000, options.MarketScanLimit);
    }

    [Fact]
    public async Task Pagination_Loads_Three_Pages_Total_600()
    {
        using var http = new HttpClient(new FakeHandler((req) =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query);
            var offset = int.Parse(q["offset"]!);
            var limit = int.Parse(q["limit"]!);
            if (offset >= 600) return "[]";
            var arr = Enumerable.Range(0, limit).Select(i => new Market { id=$"m{offset+i}", conditionId=$"c{offset+i}", active=true, accepting_orders=true, closed=false, archived=false, clobTokenIds = new(){"y","n"}, outcomes = new(){"Yes","No"} }).ToList();
            return Newtonsoft.Json.JsonConvert.SerializeObject(arr);
        }));
        var svc = new MarketDataService(http);
        var options = new TradingBotOptions { DiscoveryPageSize = 200, MaxMarketsToDiscover = 600 };
        var res = await svc.GetMarketsAsync(options);
        Assert.Equal(600, res.Markets.Count);
        Assert.Equal(3, res.Summary.PagesFetched);
        Assert.Equal(600, res.Summary.RawLoadedTotal);
    }

    [Fact]
    public async Task Offset_Pagination_Uses_Incrementing_Offsets()
    {
        var offsets = new List<int>();
        using var http = new HttpClient(new FakeHandler((req) =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query);
            var offset = int.Parse(q["offset"]!);
            offsets.Add(offset);
            var limit = int.Parse(q["limit"]!);
            if (offset >= 300) return "[]";
            return Newtonsoft.Json.JsonConvert.SerializeObject(
                Enumerable.Range(0, limit).Select(i => new Market { id=$"m{offset+i}", conditionId=$"c{offset+i}", active=true, closed=false, archived=false, clobTokenIds = new(){"y","n"}, outcomes = new(){"Yes","No"} }));
        }));
        var svc = new MarketDataService(http);
        await svc.GetMarketsAsync(new TradingBotOptions { DiscoveryPageSize = 100, AbsoluteMaxMarketsSafetyCap = 400 });
        Assert.Equal(new[] { 0, 100, 200, 300 }, offsets);
    }

    [Fact]
    public void Active_Filter_Classifies_Skip_Reasons()
    {
        Assert.True(MarketDataService.IsTradablePolymarketMarket(new Market { active = true, closed = false, archived = false, clobTokenIds = new(){"y","n"}, outcomes = new(){"Yes","No"} }).IsTradable);
        Assert.True(MarketDataService.IsTradablePolymarketMarket(new Market { active = null, closed = false, archived = false, clobTokenIds = new(){"y","n"}, outcomes = new(){"Yes","No"} }).IsTradable);
        Assert.Equal("Closed", MarketDataService.IsTradablePolymarketMarket(new Market { closed = true, clobTokenIds = new(){"y","n"}, outcomes = new(){"Yes","No"} }).SkipReason);
        Assert.Equal("Archived", MarketDataService.IsTradablePolymarketMarket(new Market { archived = true, clobTokenIds = new(){"y","n"}, outcomes = new(){"Yes","No"} }).SkipReason);
        Assert.Equal("MissingTokenIds", MarketDataService.IsTradablePolymarketMarket(new Market { active = true, closed = false, archived = false, clobTokenIds = new(), outcomes = new(){"Yes","No"} }).SkipReason);
    }

    [Fact]
    public async Task Dedup_Uses_ConditionId_And_Tracks_Duplicates()
    {
        using var http = new HttpClient(new FakeHandler((_) => Newtonsoft.Json.JsonConvert.SerializeObject(new[]
        {
            new Market { id="m1", conditionId="same", active=true, closed=false, archived=false, clobTokenIds = new(){"a","b"}, outcomes = new(){"Yes","No"} },
            new Market { id="m2", conditionId="same", active=true, closed=false, archived=false, clobTokenIds = new(){"a","b"}, outcomes = new(){"Yes","No"} }
        })));
        var svc = new MarketDataService(http);
        var result = await svc.GetMarketsAsync(new TradingBotOptions { DiscoveryPageSize = 2, AbsoluteMaxMarketsSafetyCap = 2 });
        Assert.Single(result.Markets);
        Assert.Equal(1, result.Summary.DuplicatesRemoved);
    }

    [Fact]
    public async Task Zero_Active_Markets_Sets_Discovery_Unhealthy()
    {
        using var http = new HttpClient(new FakeHandler((_) => Newtonsoft.Json.JsonConvert.SerializeObject(new[]
        {
            new Market { id="m1", active=false, closed=true, archived=true, clobTokenIds = new(){"a","b"}, outcomes = new(){"Yes","No"} }
        })));
        var svc = new MarketDataService(http);
        var result = await svc.GetMarketsAsync(new TradingBotOptions { DiscoveryPageSize = 1, AbsoluteMaxMarketsSafetyCap = 1 });
        Assert.False(result.Summary.DiscoveryHealthy);
        Assert.NotNull(result.Summary.LastDiscoveryWarning);
    }
    [Fact]
    public async Task Pagination_Uses_Effective_Page_Size_When_Endpoint_Caps()
    {
        var offsets = new List<int>();
        using var http = new HttpClient(new FakeHandler((req) =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(req.RequestUri!.Query);
            var offset = int.Parse(q["offset"]!);
            offsets.Add(offset);
            if (offset >= 300) return "[]";
            return Newtonsoft.Json.JsonConvert.SerializeObject(
                Enumerable.Range(0, 100).Select(i => new Market { id=$"m{offset+i}", conditionId=$"c{offset+i}", active=true, closed=false, archived=false, clobTokenIds = new(){"y","n"}, outcomes = new(){"Yes","No"} }));
        }));
        var svc = new MarketDataService(http);
        await svc.GetMarketsAsync(new TradingBotOptions { DiscoveryPageSize = 200, AbsoluteMaxMarketsSafetyCap = 400 });
        Assert.Equal(new[] { 0, 100, 200, 300 }, offsets);
    }

}

file sealed class FakeHandler(Func<HttpRequestMessage,string> func) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){ Content = new StringContent(func(request))});
}
