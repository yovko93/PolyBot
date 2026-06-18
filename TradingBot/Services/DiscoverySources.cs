using TradingBot.Models;

namespace TradingBot.Services;

public enum DiscoverySourceFailureKind
{
    None,
    GammaOffsetPaginationRejected,
    OffsetCapReachedBeforeRequest,
    NoScannerSafeDiscoverySource,
    BelowMinimumActiveMarkets,
    InvalidTokenIds,
    InvalidMarketIds,
    PartialDiscovery
}

public sealed record DiscoverySourceHealth(
    string SourceName,
    bool Healthy,
    bool IsScannerSafe,
    int ActiveMarkets,
    DiscoverySourceFailureKind FailureKind,
    bool CanRetry,
    DateTime? RetryAfterUtc,
    bool IsPartial,
    IReadOnlyList<string> MissingRequirements);

public sealed record DiscoverySourceResult(
    string SourceName,
    IReadOnlyList<Market> Markets,
    DiscoverySourceHealth Health);

public interface IDiscoverySource
{
    string SourceName { get; }
    Task<DiscoverySourceResult> DiscoverAsync(CancellationToken cancellationToken);
}

public static class DiscoverySourceSelection
{
    public static readonly string[] Order =
    {
        "PersistedHealthySnapshot",
        "AlternativeFullMarketSource",
        "GammaOffset",
        "GammaPartitionedOffset",
        "Blocked"
    };

    public static bool CanFeedScanner(DiscoverySourceHealth health, int minimumActiveMarkets)
        => health.Healthy
           && health.IsScannerSafe
           && health.ActiveMarkets >= minimumActiveMarkets
           && !health.IsPartial
           && health.MissingRequirements.Count == 0;
}
