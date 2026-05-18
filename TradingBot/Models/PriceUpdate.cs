namespace TradingBot.Models;

public class PriceUpdate
{
    public string event_type { get; set; }
    public string market { get; set; }
    public List<PriceChange> price_changes { get; set; }
}

public class PriceChange
{
    public string asset_id { get; set; }

    public string price { get; set; }
    public string best_bid { get; set; }
    public string best_ask { get; set; }

    public string side { get; set; }
    public string size { get; set; }
}