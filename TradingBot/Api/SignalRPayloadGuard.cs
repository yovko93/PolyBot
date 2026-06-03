using System.Text.Json;
using TradingBot.Options;

namespace TradingBot.Api;

public sealed record SignalRPayloadTrimResult<T>(T[] Items, int ItemsBefore, int ItemsAfter, bool Trimmed, int PayloadBytes);

public static class SignalRPayloadGuard
{
    public static SignalRPayloadTrimResult<T> Trim<T>(IEnumerable<T> items, SignalROptions options)
    {
        var source = items as T[] ?? items.ToArray();
        var before = source.Length;
        var itemLimit = Math.Max(1, options.MaxPayloadItems);
        var payload = source.TakeLast(Math.Min(itemLimit, before)).ToArray();
        var maxBytes = Math.Max(1024, options.MaxPayloadBytes);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload).Length;
        while (payload.Length > 1 && bytes > maxBytes)
        {
            payload = payload.TakeLast(Math.Max(1, payload.Length / 2)).ToArray();
            bytes = JsonSerializer.SerializeToUtf8Bytes(payload).Length;
        }

        return new SignalRPayloadTrimResult<T>(payload, before, payload.Length, payload.Length != before, bytes);
    }
}
