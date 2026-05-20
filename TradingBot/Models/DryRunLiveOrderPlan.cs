namespace TradingBot.Models;

public sealed class DryRunLiveOrderPlan
{
    public string PlanId { get; init; } = "";
    public DateTimeOffset CreatedAtUtc { get; init; }
    public string Strategy { get; init; } = "";
    public string GroupKey { get; init; } = "";
    public decimal TotalEstimatedCost { get; init; }
    public decimal EdgePerShare { get; init; }
    public string Status { get; init; } = "DRY_RUN_ONLY";
    public List<DryRunLiveOrder> Orders { get; init; } = new();
    public List<string> Warnings { get; init; } = new();
}

public sealed class DryRunLiveOrder
{
    public string Question { get; init; } = "";
    public string Outcome { get; init; } = "";
    public string TokenId { get; init; } = "";

    public string Side { get; init; } = "";
    public int SideCode { get; init; }

    public decimal Price { get; init; }
    public decimal Size { get; init; }

    public string MakerAmount { get; init; } = "";
    public string TakerAmount { get; init; } = "";

    public LiveOrderType OrderType { get; init; }

    public DryRunPostOrderPreview PostPreview { get; init; } = new();
}

public sealed class DryRunPostOrderPreview
{
    public object Order { get; init; } = new();
    public string Owner { get; init; } = "DRY_RUN_NO_OWNER";
    public string OrderType { get; init; } = "FOK";
    public bool DeferExec { get; init; } = false;
}