namespace TradingBot.Models.Normalized;

public enum MarketPairRiskLevel { Verified, Candidate, Disabled }

public record ExchangeOrderbook(
    string Exchange,
    string MarketId,
    string MarketTitle,
    decimal? BestYesBid,
    decimal? BestYesAsk,
    decimal? BestNoBid,
    decimal? BestNoAsk,
    decimal YesAskQuantity,
    decimal NoAskQuantity,
    DateTime TimestampUtc,
    string MarketStatus,
    string RawSourceReference
);

public record CrossExchangeMarketPair(
    bool Enabled,
    string CanonicalKey,
    string Description,
    string PolymarketMarketId,
    string? PolymarketConditionId,
    string? PolymarketQuestion,
    string KalshiTicker,
    string? KalshiMarketId,
    MarketPairRiskLevel RiskLevel,
    string? SettlementNotes
);

public record CrossExchangeArbitrageOpportunity(
    string PairKey,
    string Strategy,
    string Leg1Exchange,
    string Leg1Side,
    decimal Leg1Price,
    string Leg2Exchange,
    string Leg2Side,
    decimal Leg2Price,
    decimal GrossCost,
    decimal EstimatedFees,
    decimal SlippageBuffer,
    decimal NetCost,
    decimal GuaranteedPayout,
    decimal EdgePerShare,
    decimal ExecutableQty,
    decimal ExpectedProfit,
    bool IsExecutable,
    string SkipReason,
    DateTime TimestampUtc
);
