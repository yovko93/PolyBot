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
    }
}

file sealed class FakeHandler(Func<HttpRequestMessage,string> func) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){ Content = new StringContent(func(request))});
}
