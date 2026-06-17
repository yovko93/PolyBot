namespace TradingBot.Models;

public sealed record MarketOrderbookQuarantine(
    string MarketId,
    string YesTokenId,
    string NoTokenId,
    string Reason,
    DateTime FirstDetectedAtUtc,
    DateTime LastSeenAtUtc,
    int TtlMinutes,
    string Source,
    DateTime ExpiresAtUtc);

public sealed record OrderbookEligibilityState(
    string MarketId,
    bool Eligible,
    string IneligibleReason,
    string LastOrderbookFailure,
    int ConsecutiveOrderbookFailures,
    DateTime? QuarantinedUntilUtc);
