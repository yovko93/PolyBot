using System.ComponentModel.DataAnnotations;

namespace TradingBot.Services.Kalshi;

public class KalshiOptions
{
    public const string SectionName = "Kalshi";
    public bool Enabled { get; set; } = false;
    [Required] public string BaseUrl { get; set; } = "https://external-api.kalshi.com/trade-api/v2";
    public bool UseDemo { get; set; } = false;
    public string ApiKey { get; set; } = string.Empty;
    public string ApiSecret { get; set; } = string.Empty;
    [Range(100, int.MaxValue)] public int PollIntervalMs { get; set; } = 2000;
    [Range(1, int.MaxValue)] public int MaxMarkets { get; set; } = 200;
    [Range(1000, int.MaxValue)] public int RequestTimeoutMs { get; set; } = 10000;
}
