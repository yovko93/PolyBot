using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services.MultiOutcome;

public sealed class VerifiedMultiOutcomeGroupResolver
{
    public IReadOnlyList<ResolvedVerifiedGroup> ResolveVerifiedGroups(
        IReadOnlyList<VerifiedMultiOutcomeGroupConfig> allowlist,
        IReadOnlyDictionary<string, Market> allDiscoveredMarkets,
        MultiOutcomeArbitrageOptions options,
        bool discoveryHealthy = true)
    {
        var byCondition = allDiscoveredMarkets.Values
            .Where(m => !string.IsNullOrWhiteSpace(m.conditionId))
            .ToDictionary(m => m.conditionId!, m => m, StringComparer.OrdinalIgnoreCase);
        var output = new List<ResolvedVerifiedGroup>();
        foreach (var item in allowlist.Where(x => x.Enabled).Take(Math.Max(1, options.MaxVerifiedGroupsPerCycle)))
        {
            if (item.MarketIds.Count == 0 && item.ConditionIds.Count == 0)
            {
                output.Add(new ResolvedVerifiedGroup(item.GroupKey, item.Title ?? item.GroupKey, item.MarketIds, item.ConditionIds, Array.Empty<Market>(), item.MarketIds, item.ConditionIds, "ConfiguredButIncomplete", "VerifiedGroupIncomplete"));
                continue;
            }

            var resolved = new Dictionary<string, Market>(StringComparer.OrdinalIgnoreCase);
            var missingMarkets = new List<string>();
            var missingConditions = new List<string>();
            foreach (var id in item.MarketIds)
            {
                if (allDiscoveredMarkets.TryGetValue(id, out var m)) resolved[id] = m;
                else missingMarkets.Add(id);
            }
            foreach (var cid in item.ConditionIds)
            {
                if (byCondition.TryGetValue(cid, out var m) && !resolved.ContainsKey(m.id)) resolved[m.id] = m;
                else if (!byCondition.ContainsKey(cid)) missingConditions.Add(cid);
            }

            var resolvedMarkets = resolved.Values.Where(m => MatchGroupKey(item.GroupKey, m.question)).ToList();
            var status = "VerifiedGroupResolved";
            var reason = "VerifiedGroupResolved";
            if (resolvedMarkets.Count == 0) { status = "Rejected"; reason = "VerifiedGroupNotFoundInDiscoveredPool"; }
            else if (missingMarkets.Count > 0) { status = "Rejected"; reason = discoveryHealthy ? "VerifiedGroupMarketMismatch" : "VerifiedGroupMissingBecauseDiscoveryIncomplete"; }
            else if (missingConditions.Count > 0) { status = "Rejected"; reason = "VerifiedGroupConditionMismatch"; }
            else if (item.RequiredOutcomeCount.HasValue && resolvedMarkets.Count != item.RequiredOutcomeCount.Value && options.RequireExactOutcomeCount) { status = "Rejected"; reason = "VerifiedGroupOutcomeCountMismatch"; }

            output.Add(new ResolvedVerifiedGroup(item.GroupKey, item.Title ?? item.GroupKey, item.MarketIds, item.ConditionIds, resolvedMarkets, missingMarkets, missingConditions, status, reason));
        }

        return output;
    }

    private static bool MatchGroupKey(string groupKey, string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        var q = question.ToLowerInvariant();
        if (groupKey.StartsWith("winner:", StringComparison.OrdinalIgnoreCase))
            return q.Contains("win");
        return true;
    }
}

public sealed record ResolvedVerifiedGroup(
    string GroupKey,
    string Title,
    IReadOnlyList<string> MarketIds,
    IReadOnlyList<string> ConditionIds,
    IReadOnlyList<Market> ResolvedMarkets,
    IReadOnlyList<string> MissingMarketIds,
    IReadOnlyList<string> MissingConditionIds,
    string ValidationStatus,
    string RejectionReason);
