namespace TradingBot.Models;

public class OrderBook
{
    public string market { get; set; } = string.Empty;
    public List<OrderBookEntry> data { get; set; } = new();
}

public class OrderBookEntry
{
    public string asset_id { get; set; } = string.Empty;

    public string best_bid { get; set; } = string.Empty;
    public string best_ask { get; set; } = string.Empty;
}
