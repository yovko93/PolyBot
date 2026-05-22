using System.ComponentModel.DataAnnotations;

namespace TradingBot.Options;

public class TradingBotOptions
{
    public const string SectionName = "TradingBot";

    [Range(1, int.MaxValue)] public int ScanIntervalMs { get; set; } = 3000;
    [Range(1, int.MaxValue)] public int MaxConcurrentRequests { get; set; } = 5;
    [Range(1, int.MaxValue)] public int MarketScanLimit { get; set; } = 200;
    [Range(1, int.MaxValue)] public int AbsoluteMaxMarkets { get; set; } = 2000;
    [Range(0.0, double.MaxValue)] public decimal LogMinEdgeToLog { get; set; } = 0.001m;
    public bool ShowNegativeEdgeOpportunities { get; set; } = false;
    public bool ShowZeroEdgeOpportunities { get; set; } = false;
    public bool LogScanSummary { get; set; } = true;
    public bool LogPrefetchDetails { get; set; } = false;
    public bool LogOnlyExecutableOpportunities { get; set; } = false;
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
    [Required] public string ListenUrl { get; set; } = "http://localhost:5000";
    [Range(1000, int.MaxValue)] public int HeartbeatIntervalMs { get; set; } = 3000;
    [Range(1, int.MaxValue)] public int ExternalApiTimeoutSeconds { get; set; } = 10;
}
