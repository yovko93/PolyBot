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
    IReadOnlyList<string> ConditionIds,
    int? RequiredOutcomeCount,
    string VerificationStatus);

public sealed record AllowlistConfigValidationSummary(int Total, int UniqueGroupKeys, int DuplicateGroupKeys, int Enabled, int Disabled)
{
    public string ToLogLine() => $"[ALLOWLIST_CONFIG_VALIDATION] Total={Total} UniqueGroupKeys={UniqueGroupKeys} DuplicateGroupKeys={DuplicateGroupKeys} Enabled={Enabled} Disabled={Disabled}";
}

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
    private readonly string _contentRootPath;

    public MutuallyExclusiveGroupValidator(MultiOutcomeArbitrageOptions options, string? contentRootPath = null)
    {
        _options = options;
        _contentRootPath = string.IsNullOrWhiteSpace(contentRootPath) ? Directory.GetCurrentDirectory() : contentRootPath;
        _verifiedGroups = LoadAllowlist(_contentRootPath).ToDictionary(x => x.GroupKey, StringComparer.OrdinalIgnoreCase);
    }

    public int LoadedAllowlistCount => _verifiedGroups.Count;
    public IReadOnlyCollection<string> AllowlistKeys => _verifiedGroups.Keys.ToList();
    public IReadOnlyList<VerifiedMultiOutcomeGroupConfig> GetAllowlistedGroups() => _verifiedGroups.Values.ToList();

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
            return Reject("AutoCandidateUnverified", containsSpread, containsTotal, containsNested, independent, !sameEvent, warnings);

        if (allowlisted.MarketIds.Count == 0 && allowlisted.ConditionIds.Count == 0)
            return new GroupValidationResult(false, "ConfiguredButIncomplete", 0m, "VerifiedGroupIncomplete", "MutuallyExclusiveWinner", true, true, false, containsNested, independent, containsSpread, containsTotal, !sameEvent, warnings);

        if (!IsAllowlistMatch(allowlisted, legs))
            return Reject("AutoCandidatePartialOverlap", containsSpread, containsTotal, containsNested, independent, !sameEvent, warnings);

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
        if (config.RequiredOutcomeCount.HasValue && marketIds.Count != config.RequiredOutcomeCount.Value) return false;
        return true;
    }

    private static GroupValidationResult Reject(string reason, bool containsSpread, bool containsTotal, bool containsNested, bool independent, bool different, List<string> warnings)
        => new(false, "Candidate", 0m, reason, "Generic", !independent && !different, true, false, containsNested, independent, containsSpread, containsTotal, different, warnings);

    private static decimal? ParseThreshold(string q){ var m = Regex.Match(q, @"(\d+(\.\d+)?)"); return m.Success && decimal.TryParse(m.Groups[1].Value, out var d) ? d : null; }
    private static string ExtractEvent(string q){ var idx = q.IndexOf(':'); return idx > 0 ? q[..idx].Trim().ToLowerInvariant() : q.Trim().ToLowerInvariant(); }

    private static IReadOnlyList<VerifiedMultiOutcomeGroupConfig> LoadAllowlist(string contentRootPath)
    {
        try
        {
            var path = Path.Combine(contentRootPath, "config", "verified-multi-outcome-groups.json");
            if (!File.Exists(path)) return [];
            var doc = JsonDocument.Parse(File.ReadAllText(path));
            var validation = ValidateAllowlistConfig(doc.RootElement);
            Console.WriteLine(validation.ToLogLine());
            LogKnownRemainingRepairs(doc.RootElement);
            var items = new List<VerifiedMultiOutcomeGroupConfig>();
            foreach (var x in doc.RootElement.EnumerateArray())
            {
                var enabled = x.TryGetProperty("enabled", out var en) && en.GetBoolean();
                if (!enabled) continue;
                var key = x.TryGetProperty("groupKey", out var gk) ? (gk.GetString() ?? string.Empty) : string.Empty;
                if (string.IsNullOrWhiteSpace(key)) continue;
                var verificationStatus = x.TryGetProperty("verificationStatus", out var vs) ? (vs.GetString() ?? string.Empty) : string.Empty;
                var strategy = x.TryGetProperty("allowedStrategy", out var st) ? (st.GetString() ?? string.Empty) : string.Empty;
                if (!verificationStatus.Equals("Verified", StringComparison.OrdinalIgnoreCase) || !strategy.Equals("BUY_ALL_NO_MUTUALLY_EXCLUSIVE", StringComparison.OrdinalIgnoreCase))
                    continue;
                var marketIds = ReadArray(x, "marketIds");
                var conditionIds = ReadArray(x, "conditionIds");
                if (marketIds.Count == 0 && conditionIds.Count == 0)
                    Console.WriteLine($"[ALLOWLIST_WARNING] Group={key} is marked Verified but has no marketIds/conditionIds. It will not be executable.");
                items.Add(new VerifiedMultiOutcomeGroupConfig(enabled, key, x.TryGetProperty("title", out var t) ? t.GetString() : null,
                    marketIds, conditionIds, x.TryGetProperty("requiredOutcomeCount", out var ro) && ro.ValueKind == JsonValueKind.Number ? ro.GetInt32() : null, marketIds.Count == 0 && conditionIds.Count == 0 ? "ConfiguredButIncomplete" : "Verified"));
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
