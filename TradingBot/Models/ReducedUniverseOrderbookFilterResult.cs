using TradingBot.Models;

namespace TradingBot.Services;

public sealed record ReducedUniverseOrderbookFilterResult(
    int RawMarkets,
    int FilteredMarkets,
    int ExcludedInvalidTokens,
    int ExcludedQuarantinedMarkets,
    int ExcludedBadHistory,
    int EligibleMarkets,
    IReadOnlyList<Market> Markets);
