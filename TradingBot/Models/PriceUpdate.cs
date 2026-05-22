namespace TradingBot.Models;

public class PriceUpdate
{
    public string event_type { get; set; } = string.Empty;
    public string market { get; set; } = string.Empty;
    public List<PriceChange> price_changes { get; set; } = new();
}

public class PriceChange
{
    public string asset_id { get; set; } = string.Empty;

    public string price { get; set; } = string.Empty;
    public string best_bid { get; set; } = string.Empty;
    public string best_ask { get; set; } = string.Empty;

    public string side { get; set; } = string.Empty;
    public string size { get; set; } = string.Empty;
}
