using System.Text.RegularExpressions;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

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
    private readonly HashSet<string> _verifiedGroupKeys;

    public MutuallyExclusiveGroupValidator(MultiOutcomeArbitrageOptions options)
    {
        _options = options;
        _verifiedGroupKeys = LoadAllowlist();
    }

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

        var verified = _verifiedGroupKeys.Contains(groupKey);
        if (!verified)
            return Reject("UnverifiedGroup", containsSpread, containsTotal, containsNested, independent, !sameEvent, warnings);
        if (groupKind.Equals("generic", StringComparison.OrdinalIgnoreCase) && !_options.AllowGenericGroupsForExecution)
            return Reject("GenericGroupNotExecutable", containsSpread, containsTotal, containsNested, independent, !sameEvent, warnings);
        if (containsSpread)
            return Reject("SpreadLineGroupNotMutuallyExclusive", true, containsTotal, containsNested, independent, !sameEvent, warnings);
        if (containsTotal)
            return Reject("TotalLineGroupNotMutuallyExclusive", containsSpread, true, containsNested, independent, !sameEvent, warnings);
        if (containsNested)
            return Reject("NestedThresholdsDetected", containsSpread, containsTotal, true, independent, !sameEvent, warnings);
        if (independent)
            return Reject("IndependentEventsNotMutuallyExclusive", containsSpread, containsTotal, containsNested, true, true, warnings);

        return new GroupValidationResult(true, "Verified", 1m, "None", "MutuallyExclusiveWinner", true, true, true, false, false, false, false, false, warnings);
    }

    private static GroupValidationResult Reject(string reason, bool containsSpread, bool containsTotal, bool containsNested, bool independent, bool different, List<string> warnings)
        => new(false, "Candidate", 0m, reason, "Generic", !independent && !different, true, false, containsNested, independent, containsSpread, containsTotal, different, warnings);

    private static decimal? ParseThreshold(string q)
    {
        var m = Regex.Match(q, @"(\d+(\.\d+)?)");
        return m.Success && decimal.TryParse(m.Groups[1].Value, out var d) ? d : null;
    }

    private static string ExtractEvent(string q)
    {
        var idx = q.IndexOf(':');
        return idx > 0 ? q[..idx].Trim().ToLowerInvariant() : q.Trim().ToLowerInvariant();
    }

    private static HashSet<string> LoadAllowlist()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "config", "verified-multi-outcome-groups.json");
            if (!File.Exists(path)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var txt = File.ReadAllText(path);
            var doc = System.Text.Json.JsonDocument.Parse(txt);
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in doc.RootElement.EnumerateArray())
            {
                if (x.GetProperty("enabled").GetBoolean())
                    set.Add(x.GetProperty("groupKey").GetString() ?? string.Empty);
            }
            return set;
        }
        catch { return new HashSet<string>(StringComparer.OrdinalIgnoreCase); }
    }
}
