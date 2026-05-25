using System.Text.Json;
using System.Text.RegularExpressions;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record VerifiedMultiOutcomeGroupConfig(
    bool Enabled,
    string GroupKey,
    string? Title,
    IReadOnlyList<string> MarketIds,
    IReadOnlyList<string> ConditionIds);

public sealed record GroupValidationResult(
    bool IsValidForNoBasketArbitrage,
    string VerificationStatus,
    decimal Confidence,
    string RejectionReason,
    string DetectedMarketType,
    bool SameEvent,
    bool SameSettlementSource,
    bool PairwiseMutuallyExclusive,
    bool ContainsNestedThresholds,
    bool ContainsIndependentEvents,
    bool ContainsSpreadLines,
    bool ContainsTotalLines,
    bool ContainsDifferentTeamsOrMatches,
    IReadOnlyList<string> Warnings);

public class MutuallyExclusiveGroupValidator
{
    private readonly MultiOutcomeArbitrageOptions _options;
    private readonly Dictionary<string, VerifiedMultiOutcomeGroupConfig> _verifiedGroups;

    public MutuallyExclusiveGroupValidator(MultiOutcomeArbitrageOptions options)
    {
        _options = options;
        _verifiedGroups = LoadAllowlist().ToDictionary(x => x.GroupKey, StringComparer.OrdinalIgnoreCase);
    }

    public int LoadedAllowlistCount => _verifiedGroups.Count;
    public IReadOnlyCollection<string> AllowlistKeys => _verifiedGroups.Keys.ToList();

    public GroupValidationResult Validate(string groupKey, string groupKind, IReadOnlyList<BasketArbLeg> legs)
    {
        var warnings = new List<string>();
        var containsSpread = legs.Any(x => Regex.IsMatch(x.Question, @"\([+-]\d+(\.\d+)?\)|\bspread\b", RegexOptions.IgnoreCase));
        var containsTotal = legs.Any(x => Regex.IsMatch(x.Question, @"\bo/u\b|\bover\/under\b|\btotal\b", RegexOptions.IgnoreCase));
        var thresholds = legs.Select(x => ParseThreshold(x.Question)).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var containsNested = thresholds.Count > 1;

        var events = legs.Select(x => ExtractEvent(x.Question)).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToList();
        var sameEvent = events.Count <= 1;
        var independent = events.Count > 1;

        if (!_verifiedGroups.TryGetValue(groupKey, out var allowlisted))
            return Reject("UnverifiedGroup", containsSpread, containsTotal, containsNested, independent, !sameEvent, warnings);

        if (!IsAllowlistMatch(allowlisted, legs))
            return Reject("VerifiedGroupMarketMismatch", containsSpread, containsTotal, containsNested, independent, !sameEvent, warnings);

        if (groupKind.Equals("generic", StringComparison.OrdinalIgnoreCase) && !_options.AllowGenericGroupsForExecution)
            return Reject("GenericGroupNotExecutable", containsSpread, containsTotal, containsNested, independent, !sameEvent, warnings);

        return new GroupValidationResult(true, "Verified", 1m, "None", "MutuallyExclusiveWinner", true, true, true, false, false, false, false, false, warnings);
    }

    private static bool IsAllowlistMatch(VerifiedMultiOutcomeGroupConfig config, IReadOnlyList<BasketArbLeg> legs)
    {
        var marketIds = legs.Select(x => x.MarketId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var conditionIds = legs.Select(x => x.TokenId).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (config.MarketIds.Count > 0 && !config.MarketIds.All(marketIds.Contains)) return false;
        if (config.ConditionIds.Count > 0 && !config.ConditionIds.All(conditionIds.Contains)) return false;
        return true;
    }

    private static GroupValidationResult Reject(string reason, bool containsSpread, bool containsTotal, bool containsNested, bool independent, bool different, List<string> warnings)
        => new(false, "Candidate", 0m, reason, "Generic", !independent && !different, true, false, containsNested, independent, containsSpread, containsTotal, different, warnings);

    private static decimal? ParseThreshold(string q){ var m = Regex.Match(q, @"(\d+(\.\d+)?)"); return m.Success && decimal.TryParse(m.Groups[1].Value, out var d) ? d : null; }
    private static string ExtractEvent(string q){ var idx = q.IndexOf(':'); return idx > 0 ? q[..idx].Trim().ToLowerInvariant() : q.Trim().ToLowerInvariant(); }

    private static IReadOnlyList<VerifiedMultiOutcomeGroupConfig> LoadAllowlist()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config", "verified-multi-outcome-groups.json");
            if (!File.Exists(path)) return [];
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            var items = new List<VerifiedMultiOutcomeGroupConfig>();
            foreach (var x in doc.RootElement.EnumerateArray())
            {
                var enabled = x.TryGetProperty("enabled", out var en) && en.GetBoolean();
                if (!enabled) continue;
                var key = x.TryGetProperty("groupKey", out var gk) ? (gk.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(key)) continue;
                items.Add(new VerifiedMultiOutcomeGroupConfig(enabled, key, x.TryGetProperty("title", out var t) ? t.GetString() : null,
                    ReadArray(x, "marketIds"), ReadArray(x, "conditionIds")));
            }
            return items;
        }
        catch { return []; }
    }

    private static IReadOnlyList<string> ReadArray(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        return arr.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList();
    }
}
