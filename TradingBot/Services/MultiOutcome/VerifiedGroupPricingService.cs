using TradingBot.Models;

namespace TradingBot.Services.MultiOutcome;

public sealed class VerifiedGroupPricingService
{
    public static BinaryMarketTokens ResolveBinaryTokens(Market market)
    {
        if (market.clobTokenIds is null || market.clobTokenIds.Count < 2)
            return new BinaryMarketTokens(null, null, null, null, false, "MissingTokenIds");

        string? yes = null;
        string? no = null;
        string? yesName = null;
        string? noName = null;
        if (market.outcomes?.Count == market.clobTokenIds.Count)
        {
            for (var i = 0; i < market.outcomes.Count; i++)
            {
                var o = market.outcomes[i].Trim();
                if (o.Equals("yes", StringComparison.OrdinalIgnoreCase)) { yes = market.clobTokenIds[i]; yesName = o; }
                if (o.Equals("no", StringComparison.OrdinalIgnoreCase)) { no = market.clobTokenIds[i]; noName = o; }
            }
        }

        yes ??= market.clobTokenIds.FirstOrDefault();
        no ??= market.clobTokenIds.Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(no))
            return new BinaryMarketTokens(yes, null, yesName, noName, false, "MissingNoTokenId");
        return new BinaryMarketTokens(yes, no, yesName ?? "Yes", noName ?? "No", true, null);
    }

    public static ResolvedNoAsk ResolveNoAsk(Market market, BinaryOrderBookSnapshot? snapshot, DateTime nowUtc, int maxAgeMs)
    {
        var tokens = ResolveBinaryTokens(market);
        if (!tokens.IsValidBinaryMarket)
            return ResolvedNoAsk.Fail(market.id, market.conditionId, tokens.NoTokenId, "MissingNoTokenId");
        if (snapshot is null)
            return ResolvedNoAsk.Fail(market.id, market.conditionId, tokens.NoTokenId, "OrderbookFetchFailed");

        if (snapshot.NoAsk is not null)
            return new ResolvedNoAsk(market.id, market.conditionId, snapshot.NoAsk.Price, snapshot.NoAsk.Size, "DirectNoAsk", snapshot.YesBid?.Price, snapshot.YesBid?.Size, tokens.NoTokenId, nowUtc, false, null);

        if (snapshot.YesBid is not null)
        {
            var derived = 1m - snapshot.YesBid.Price;
            return new ResolvedNoAsk(market.id, market.conditionId, derived, snapshot.YesBid.Size, "DerivedFromYesBid", snapshot.YesBid.Price, snapshot.YesBid.Size, tokens.NoTokenId, nowUtc, false, null);
        }

        return ResolvedNoAsk.Fail(market.id, market.conditionId, tokens.NoTokenId, "MissingYesBidForDerivedNoAsk");
    }
}

public sealed record BinaryMarketTokens(string? YesTokenId, string? NoTokenId, string? YesOutcomeName, string? NoOutcomeName, bool IsValidBinaryMarket, string? FailureReason);
public sealed record ResolvedNoAsk(string MarketId, string? ConditionId, decimal? NoAsk, decimal? NoAskQuantity, string Source, decimal? YesBid, decimal? YesBidQuantity, string? NoTokenId, DateTime TimestampUtc, bool IsStale, string? FailureReason)
{
    public static ResolvedNoAsk Fail(string marketId, string? conditionId, string? noTokenId, string reason)
        => new(marketId, conditionId, null, null, "None", null, null, noTokenId, DateTime.UtcNow, false, reason);
}
