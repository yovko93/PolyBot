using System.ComponentModel.DataAnnotations;

namespace TradingBot.Options;

public class TradingBotOptions
{
    public const string SectionName = "TradingBot";

    [Range(1, int.MaxValue)] public int ScanIntervalMs { get; set; } = 3000;
    [Range(1, int.MaxValue)] public int MaxConcurrentRequests { get; set; } = 5;
    [Range(1, int.MaxValue)] public int MarketScanLimit { get; set; } = 200;
    public bool EnableLiveExecution { get; set; } = false;
    public bool EnablePaperTrading { get; set; } = true;
    public string ExecutionMode { get; set; } = "PAPER";
    [Range(0.0001, double.MaxValue)] public decimal MinEdgePerShare { get; set; } = 0.003m;
    [Range(0.0, double.MaxValue)] public decimal MinExpectedProfit { get; set; } = 0.25m;
    [Range(0.01, double.MaxValue)] public decimal MaxNotionalPerTrade { get; set; } = 100m;
    [Range(1, int.MaxValue)] public int MaxOpenPositions { get; set; } = 5;
    [Range(0.01, double.MaxValue)] public decimal MinNotionalPerTrade { get; set; } = 5m;
    [Range(0.01, double.MaxValue)] public decimal MaxLockedCapital { get; set; } = 300m;
    [Range(0.01, double.MaxValue)] public decimal MaxExposurePerGroup { get; set; } = 100m;
    [Range(0.0, double.MaxValue)] public decimal SingleMarketSlippage { get; set; } = 0.001m;
    [Range(0.0, double.MaxValue)] public decimal SingleMarketFees { get; set; } = 0.001m;
}
