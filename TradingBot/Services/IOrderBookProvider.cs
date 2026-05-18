using TradingBot.Models;

namespace TradingBot.Services;

public interface IOrderBookProvider
{
    Task<BinaryOrderBookSnapshot?> GetBinarySnapshotAsync(Market market, CancellationToken ct = default);
}