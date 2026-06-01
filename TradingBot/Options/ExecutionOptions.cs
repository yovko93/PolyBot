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
    [Range(0.01, double.MaxValue)] public decimal MaxNotionalPerBasket { get; set; } = 250m;
    [Range(1, int.MaxValue)] public int MaxOpenPositions { get; set; } = 5;
    [Range(1, int.MaxValue)] public int MaxOpenBasketPositions { get; set; } = 5;
    [Range(0.01, double.MaxValue)] public decimal MaxExposurePerMarket { get; set; } = 100m;
    [Range(0.01, double.MaxValue)] public decimal MaxExposurePerGroup { get; set; } = 250m;
    [Range(0.0, 1.0)] public decimal MaxSlippagePerLeg { get; set; } = 0.003m;
    [Range(1, int.MaxValue)] public int MaxOrderbookAgeMs { get; set; } = 3000;
    [Range(-1.0, 1.0)] public decimal MinEdgeAfterFeesAndSlippage { get; set; } = 0.001m;
    public bool RequireStableExecutableSignals { get; set; } = true;
    [Range(1, 20)] public int RequiredConsecutiveExecutableScans { get; set; } = 3;
    [Range(-1.0, 1.0)] public decimal MinStableNetEdgePerBasket { get; set; } = 0.001m;
    [Range(0.0, 1.0)] public decimal MaxNetEdgeVolatility { get; set; } = 0.002m;
    public bool RequireStableExecutionReadiness { get; set; } = true;
    [Range(1, 20)] public int RequiredConsecutiveExecutionReadyScans { get; set; } = 3;
    [Range(0.0001, double.MaxValue)] public decimal MinPlannedBasketQty { get; set; } = 5m;
    [Range(0.0001, double.MaxValue)] public decimal MinPlannedNotional { get; set; } = 25m;
    [Range(0.0000001, double.MaxValue)] public decimal MinPlannedExpectedProfit { get; set; } = 0.10m;
    [Range(0.0, 10.0)] public decimal MaxPlannedQtyVolatilityRatio { get; set; } = 0.50m;
    [Range(0.0, 10.0)] public decimal MaxPlannedCostVolatilityRatio { get; set; } = 0.50m;
    [Range(1, 600)] public int StabilityWindowSeconds { get; set; } = 30;
    public bool AllowPartialFills { get; set; } = false;
    public bool CancelRemainingLegOnPartialFill { get; set; } = true;
    public bool PaperOnly { get; set; } = true;
    public bool EnableLiveOrderSubmission { get; set; } = false;
    public bool EnableDryRunOrderBuilder { get; set; } = true;
    public bool AllowPaperExecutionOnPartialDiscovery { get; set; } = false;
    public bool AllowDryRunOrderPlanOnPartialDiscovery { get; set; } = true;
    public bool RequireHealthyDiscoveryForPaperOpen { get; set; } = true;
    public bool PreventDuplicateGroupPositions { get; set; } = true;
    [Range(1, 1440)] public int DuplicateCooldownMinutes { get; set; } = 60;
}
