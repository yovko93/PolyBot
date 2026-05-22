using System.ComponentModel.DataAnnotations;

namespace TradingBot.Options;

public class CrossExchangeOptions
{
    public const string SectionName = "CrossExchangeArbitrage";
    public bool Enabled { get; set; } = false;
    public bool PaperOnly { get; set; } = true;
    [Range(0.0, 1.0)] public decimal MinCrossExchangeEdge { get; set; } = 0.005m;
    [Range(0.0, double.MaxValue)] public decimal MinCrossExchangeExpectedProfit { get; set; } = 0.10m;
    [Range(1, double.MaxValue)] public decimal MaxNotionalPerOpportunity { get; set; } = 100m;
    [Range(1, int.MaxValue)] public int MaxOpenCrossExchangePositions { get; set; } = 5;
    [Range(0.0, 1.0)] public decimal SlippageBufferPerShare { get; set; } = 0.002m;
    [Range(100, int.MaxValue)] public int MaxOrderbookAgeMs { get; set; } = 10000;
    public bool UseOnlyVerifiedMarketPairs { get; set; } = true;
    public bool EnableCandidatePairLogging { get; set; } = true;
}

public class ExchangeFeesOptions
{
    public const string SectionName = "ExchangeFees";
    public FeeModel Polymarket { get; set; } = new();
    public FeeModel Kalshi { get; set; } = new();
}

public class FeeModel
{
    public bool Enabled { get; set; } = true;
    public decimal FeePerShare { get; set; }
    public decimal PercentageFee { get; set; }
    public decimal FixedFee { get; set; }
}
