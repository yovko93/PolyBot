namespace TradingBot.Models;

public enum PaperPositionStatus
{
    Open,
    Closed,
    Cancelled
}

public record PaperPositionLeg(
    string MarketId,
    string Question,
    string Outcome,
    decimal Price,
    decimal Quantity,
    decimal Notional
);

public class PaperPosition
{
    public string PositionId { get; init; } = "";
    public DateTime OpenedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? ClosedAtUtc { get; set; }

    public decimal Cost { get; set; }
    public bool IsClosed { get; set; }

    public string Engine { get; init; } = "";
    public string Strategy { get; init; } = "";
    public string GroupKey { get; init; } = "";

    public decimal Quantity { get; init; }
    public decimal TotalCost { get; init; }
    public decimal CostPerBasket { get; init; }
    public decimal GuaranteedPayout { get; init; }
    public decimal EdgePerShare { get; init; }
    public decimal ExpectedProfit { get; init; }
    public decimal ExpectedProfitAtOpen => ExpectedProfit;
    public decimal GrossEdgeAtOpen { get; init; }
    public decimal GrossEdgePerBasket => GrossEdgeAtOpen;
    public decimal NetEdgeAtOpen { get; init; }
    public decimal ActiveProfileNetEdgePerBasket => NetEdgeAtOpen;
    public decimal FillAdjustedNetEdgePerBasket => NetEdgeAtOpen;
    public decimal LockedCapital { get; set; }
    public string ActiveProfile { get; init; } = "";
    public string Source { get; set; } = "";
    public string SourceKind { get; set; } = "";
    public bool IsSyntheticCanary { get; init; }
    public string SourceCandidateId { get; set; } = "";
    public string ProcessRunId { get; set; } = "";
    public bool OpenedFromSimulatedFills { get; init; }
    public string? FillSimulationId { get; init; }

    public decimal CurrentNoAskSum { get; set; }
    public decimal? CurrentExitValue { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public string MtmStatus { get; set; } = "Incomplete";
    public int MissingExitPrices { get; set; }

    public decimal? RealizedPayout { get; set; }
    public decimal? RealizedProfit { get; set; }

    public PaperPositionStatus Status { get; set; } = PaperPositionStatus.Open;

    public List<PaperPositionLeg> Legs { get; init; } = new();
}
