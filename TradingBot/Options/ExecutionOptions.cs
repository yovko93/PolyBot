using System.ComponentModel.DataAnnotations;

namespace TradingBot.Options;

public enum ExecutionMode
{
    Disabled,
    Paper,
    DryRunLive,
    Live
}

public sealed class ExecutionOptions
{
    public const string SectionName = "Execution";
    public ExecutionMode Mode { get; set; } = ExecutionMode.Paper;
    public bool EnableLiveTrading { get; set; } = false;
    public bool EnableDryRunLive { get; set; } = true;
    public bool RequireManualConfirmationForLive { get; set; } = true;
    public bool KillSwitchEnabled { get; set; } = false;
    [Range(0.01, double.MaxValue)] public decimal MaxNotionalPerTrade { get; set; } = 100m;
    [Range(0.01, double.MaxValue)] public decimal MaxDailyNotional { get; set; } = 500m;
    [Range(1, int.MaxValue)] public int MaxOpenPositions { get; set; } = 5;
    [Range(0.01, double.MaxValue)] public decimal MaxExposurePerMarket { get; set; } = 100m;
    [Range(0.0, 1.0)] public decimal MaxSlippagePerLeg { get; set; } = 0.003m;
    [Range(1, int.MaxValue)] public int MaxOrderbookAgeMs { get; set; } = 3000;
    [Range(-1.0, 1.0)] public decimal MinEdgeAfterFeesAndSlippage { get; set; } = 0.001m;
    public bool AllowPartialFills { get; set; } = false;
    public bool CancelRemainingLegOnPartialFill { get; set; } = true;
}
