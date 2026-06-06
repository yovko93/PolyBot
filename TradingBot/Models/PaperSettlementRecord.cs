namespace TradingBot.Models;

public sealed record PaperSettlementRecord(
    string SettlementId,
    string PositionId,
    DateTime RequestedAtUtc,
    DateTime? SettledAtUtc,
    string Mode,
    decimal Cost,
    decimal RealizedPayout,
    decimal RealizedPnl,
    string Status,
    string? RejectReason = null);

public sealed record PaperSettlementResult(
    bool Accepted,
    string Reason,
    PaperPosition? Position,
    PaperSettlementRecord? Settlement,
    bool DuplicateSuppressed = false);
