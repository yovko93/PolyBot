using Newtonsoft.Json;
using System.Net.WebSockets;
using System.Text;
using TradingBot.Engines;
using TradingBot.Models;

namespace TradingBot.Services;

public class PolymarketWebSocket
{
    //private readonly ClientWebSocket _ws = new();
    private readonly PriceService _priceService;
    private readonly StrategyEngine _strategy;
    private readonly PaperTradingEngine _paper;

    public PolymarketWebSocket(
        PriceService priceService,
        StrategyEngine strategy,
        PaperTradingEngine paper)
    {
        _priceService = priceService;
        _strategy = strategy;
        _paper = paper;
    }

    private ClientWebSocket CreateWebSocket()
    {
        var ws = new ClientWebSocket();

        ws.Options.SetRequestHeader("User-Agent", "Mozilla/5.0");
        ws.Options.SetRequestHeader("Origin", "https://polymarket.com");

        return ws;
    }

    public async Task RunAsync()
    {
        while (true)
        {
            try
            {
                await StartAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WS error: {ex.Message}");
            }

            Console.WriteLine("Reconnecting in 2s...");
            await Task.Delay(2000);
        }
    }

    public async Task StartAsync()
    {
        using var ws = CreateWebSocket();

        var uri = new Uri("wss://ws-subscriptions-clob.polymarket.com/ws/market");

        await ws.ConnectAsync(uri, CancellationToken.None);

        Console.WriteLine("Connected to WS");

        await Subscribe(ws);
        await ReceiveLoop(ws);
    }

    private async Task Subscribe(ClientWebSocket ws)
    {
        var message = @"{
        ""type"": ""subscribe"",
        ""channel"": ""price_changes"",
        ""markets"": [""0xf1855d47c0f70732e3d51cf9b690139ea24d31fed71552c8d195b1c60a1aef82""]
    }";

        var bytes = Encoding.UTF8.GetBytes(message);

        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private async Task ReceiveLoop(ClientWebSocket ws)
    {
        var buffer = new byte[8192];

        while (ws.State == WebSocketState.Open)
        {
            var result = await ws.ReceiveAsync(buffer, CancellationToken.None);

            var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);

            var data = JsonConvert.DeserializeObject<PriceUpdate>(msg);

            if (data?.price_changes?.Count >= 2)
            {
                var yes = data.price_changes[0];
                var no = data.price_changes[1];

                decimal yesPrice = decimal.Parse(yes.price);
                decimal noPrice = decimal.Parse(no.price);

                decimal yesBid = decimal.Parse(yes.best_bid);
                decimal yesAsk = decimal.Parse(yes.best_ask);

                decimal noBid = decimal.Parse(no.best_bid);
                decimal noAsk = decimal.Parse(no.best_ask);

                Console.WriteLine($"YES: {yesPrice} | NO: {noPrice}");

                var sum = yesAsk + noAsk;
                Console.WriteLine($"SUM: {sum}");

                _priceService.UpdatePrice(data.market, yesPrice);

                var signal = _strategy.Evaluate(
                    data.market,
                    yesPrice,
                    yesBid,
                    _paper.Positions
                );

                _paper.Execute(signal, data.market);
            }
        }
    }
}