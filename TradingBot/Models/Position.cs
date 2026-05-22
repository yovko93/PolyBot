namespace TradingBot.Models;

public class Position
{
    public string MarketId { get; set; } = string.Empty;
    public decimal EntryPrice { get; set; }
    public decimal Size { get; set; }

    public bool IsOpen { get; set; } = true;
}
