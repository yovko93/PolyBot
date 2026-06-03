using System.Reflection;
using System.Text.Json.Nodes;

namespace TradingBot.Services;

public enum DuplicateGroupKeyPolicy
{
    ThrowForConfig,
    KeepFirst,
    KeepLatest,
    KeepBestRepairCandidate,
    MergeRepairHistory,
    KeepHighestConfidence,
    KeepMostRecentSnapshot,
    KeepMostRestrictive
}

public static class GroupKeyDictionaryBuilder
{
    private static int _duplicateWarnings;
    public static int DuplicateWarnings => Volatile.Read(ref _duplicateWarnings);

    public static Dictionary<string, T> BuildUniqueByGroupKey<T>(IEnumerable<T> items, Func<T, string?> keySelector, string sourceName, DuplicateGroupKeyPolicy policy)
    {
        var result = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        var duplicateGroups = items
            .Select((item, index) => new KeyedItem<T>(item, NormalizeKey(keySelector(item)), index))
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in duplicateGroups)
        {
            var entries = group.ToArray();
            if (entries.Length == 1)
            {
                result[group.Key] = entries[0].Item;
                continue;
            }

            Interlocked.Increment(ref _duplicateWarnings);
            Console.WriteLine($"[DUPLICATE_GROUPKEY_DETECTED] Source={sourceName} GroupKey={group.Key} Count={entries.Length} Policy={policy}");
            if (policy == DuplicateGroupKeyPolicy.ThrowForConfig)
                Console.WriteLine($"[ALLOWLIST_CONFIG_ERROR] DuplicateGroupKeys=[{group.Key}]");

            var kept = Select(entries, policy);
            result[group.Key] = kept.Item;
            var dropped = entries.Length - 1;
            Console.WriteLine($"[DUPLICATE_GROUPKEY_RESOLVED] Source={sourceName} GroupKey={group.Key} Kept={Describe(kept.Item)} Dropped={dropped}");
        }

        return result;
    }

    private static KeyedItem<T> Select<T>(IReadOnlyList<KeyedItem<T>> entries, DuplicateGroupKeyPolicy policy) => policy switch
    {
        DuplicateGroupKeyPolicy.KeepFirst or DuplicateGroupKeyPolicy.ThrowForConfig => entries.OrderBy(x => x.Index).First(),
        DuplicateGroupKeyPolicy.KeepLatest or DuplicateGroupKeyPolicy.MergeRepairHistory => entries.OrderBy(x => x.Index).Last(),
        DuplicateGroupKeyPolicy.KeepHighestConfidence => entries.OrderByDescending(x => ConfidenceScore(x.Item)).ThenByDescending(x => x.Index).First(),
        DuplicateGroupKeyPolicy.KeepBestRepairCandidate => entries.OrderByDescending(x => RepairCandidateScore(x.Item)).ThenByDescending(x => ConfidenceScore(x.Item)).ThenByDescending(x => MostRecentTicks(x.Item)).ThenByDescending(x => x.Index).First(),
        DuplicateGroupKeyPolicy.KeepMostRecentSnapshot => entries.OrderByDescending(x => MostRecentTicks(x.Item)).ThenByDescending(x => x.Index).First(),
        DuplicateGroupKeyPolicy.KeepMostRestrictive => entries.OrderByDescending(x => RestrictiveScore(x.Item)).ThenByDescending(x => ConfidenceScore(x.Item)).ThenByDescending(x => x.Index).First(),
        _ => entries.OrderBy(x => x.Index).First()
    };

    private static string NormalizeKey(string? key) => (key ?? string.Empty).Trim();

    private static int ConfidenceScore<T>(T item)
    {
        var confidence = ReadString(item, "Confidence") ?? ReadString(item, "RepairConfidence");
        return confidence?.ToLowerInvariant() switch
        {
            "unsafe" => 0,
            "low" => 1,
            "medium" => 2,
            "high" => 3,
            _ => 0
        };
    }

    private static decimal RepairCandidateScore<T>(T item)
    {
        var repairMatch = ReadPropertyValue(item, "RepairMatch");
        var score = ReadDecimal(repairMatch, "Score");
        var pricedLegs = ReadDecimal(repairMatch, "PricedLegs");
        var resolved = ReadDecimal(item, "ResolvedMarketCount");
        var candidateMarkets = ReadJsonArrayCount(item, "markets");
        return (score * 1_000_000m) + (pricedLegs * 1_000m) + (resolved * 10m) + candidateMarkets;
    }

    private static int RestrictiveScore<T>(T item)
    {
        var notes = string.Join("|", ReadStringEnumerable(item, "RiskNotes"));
        var patchType = ReadString(item, "PatchType") ?? string.Empty;
        var action = ReadString(item, "CurrentAction") ?? ReadString(item, "RecommendedAction") ?? string.Empty;
        var confidence = ReadString(item, "Confidence") ?? ReadString(item, "RepairConfidence") ?? string.Empty;
        var score = 0;
        if (notes.Contains("RepairDiffOscillation", StringComparison.OrdinalIgnoreCase)) score += 1000;
        if (notes.Contains("ManualLock", StringComparison.OrdinalIgnoreCase)) score += 900;
        if (patchType.Equals("ReviewOnly", StringComparison.OrdinalIgnoreCase)) score += 200;
        if (action.Equals("NeedsManualReview", StringComparison.OrdinalIgnoreCase)) score += 100;
        if (confidence.Equals("Unsafe", StringComparison.OrdinalIgnoreCase)) score += 50;
        if (patchType.Equals("None", StringComparison.OrdinalIgnoreCase)) score -= 10;
        return score;
    }

    private static long MostRecentTicks<T>(T item)
    {
        foreach (var name in new[] { "Timestamp", "LastUpdatedAt", "CreatedAt", "ActionChangedAt" })
        {
            var value = ReadPropertyValue(item, name);
            if (value is DateTime dt) return dt.Ticks;
        }
        return 0;
    }

    private static string Describe<T>(T item)
        => $"{ReadString(item, "PatchType") ?? ReadString(item, "CurrentAction") ?? ReadString(item, "RecommendedAction") ?? ReadString(item, "GroupKey") ?? typeof(T).Name}:{ReadString(item, "Confidence") ?? ReadString(item, "RepairConfidence") ?? "n/a"}";

    private static object? ReadPropertyValue<T>(T item, string name)
    {
        if (item is null) return null;
        if (item is JsonObject json)
        {
            foreach (var kv in json)
                if (kv.Key.Equals(name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return null;
        }
        return item.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase)?.GetValue(item);
    }

    private static string? ReadString<T>(T item, string name)
    {
        var value = ReadPropertyValue(item, name);
        if (value is string s) return s;
        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var jsonString)) return jsonString;
        return null;
    }

    private static decimal ReadDecimal(object? item, string name)
    {
        var value = ReadPropertyValue(item, name);
        if (value is decimal d) return d;
        if (value is int i) return i;
        if (value is long l) return l;
        if (value is double db) return (decimal)db;
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<decimal>(out var jsonDecimal)) return jsonDecimal;
            if (jsonValue.TryGetValue<int>(out var jsonInt)) return jsonInt;
        }
        return 0m;
    }

    private static int ReadJsonArrayCount<T>(T item, string name)
        => ReadPropertyValue(item, name) is JsonArray arr ? arr.Count : 0;

    private static IEnumerable<string> ReadStringEnumerable<T>(T item, string name)
    {
        var value = ReadPropertyValue(item, name);
        if (value is IEnumerable<string> strings) return strings;
        if (value is JsonArray arr) return arr.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>();
        return Array.Empty<string>();
    }

    private sealed record KeyedItem<T>(T Item, string Key, int Index);
}
