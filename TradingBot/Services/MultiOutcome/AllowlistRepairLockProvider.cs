using Microsoft.Extensions.Options;
using TradingBot.Options;

namespace TradingBot.Services.MultiOutcome;

public sealed class AllowlistRepairLockProvider
{
    public const string PeruOscillationGroupKey = "winner:2026 peruvian presidential election|kind:person";
    public const string PeruOscillationReason = "Manual review required due to repair diff oscillation on marketId 947269";

    public int TotalConfigured { get; }
    public int UniqueGroupKeys => LockedGroups.Count;
    public int DuplicateGroupKeys { get; }
    public IReadOnlyDictionary<string, AllowlistRepairLockedGroupOptions> LockedGroups { get; }

    public AllowlistRepairLockProvider(IOptions<TradingBotOptions> options)
    {
        var normalized = NormalizeLockedGroups(options.Value.AllowlistRepair.LockedGroups);
        TotalConfigured = normalized.TotalConfigured;
        DuplicateGroupKeys = normalized.DuplicateGroupKeys;
        LockedGroups = normalized.LockedGroups;
        LogValidation();
    }

    public bool TryGetLock(string? groupKey, out AllowlistRepairLockedGroupOptions lockedGroup)
    {
        lockedGroup = default!;
        return !string.IsNullOrWhiteSpace(groupKey) && LockedGroups.TryGetValue(groupKey.Trim(), out lockedGroup!);
    }

    public bool IsHardLocked(string? groupKey)
        => TryGetLock(groupKey, out var lockedGroup) && !lockedGroup.AllowPatchPreview;

    public static LockNormalizationResult NormalizeLockedGroups(IEnumerable<AllowlistRepairLockedGroupOptions>? configuredLocks)
    {
        var rawLocks = NormalizeInput(configuredLocks).ToList();
        if (rawLocks.Count == 0)
            rawLocks.Add(DefaultPeruLock());

        var groups = rawLocks.GroupBy(x => x.GroupKey, StringComparer.OrdinalIgnoreCase).ToArray();
        var duplicateGroupKeys = groups.Sum(g => Math.Max(0, g.Count() - 1));
        var lockedGroups = groups.ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);
        return new LockNormalizationResult(rawLocks.Count, duplicateGroupKeys, lockedGroups);
    }

    public static AllowlistRepairLockedGroupOptions DefaultPeruLock() => new()
    {
        GroupKey = PeruOscillationGroupKey,
        Reason = PeruOscillationReason,
        AllowPatchPreview = false
    };

    private static IEnumerable<AllowlistRepairLockedGroupOptions> NormalizeInput(IEnumerable<AllowlistRepairLockedGroupOptions>? locks)
    {
        foreach (var locked in locks ?? [])
        {
            if (string.IsNullOrWhiteSpace(locked.GroupKey)) continue;
            yield return new AllowlistRepairLockedGroupOptions
            {
                GroupKey = locked.GroupKey.Trim(),
                Reason = string.IsNullOrWhiteSpace(locked.Reason) ? "ManualLock" : locked.Reason.Trim(),
                AllowPatchPreview = locked.AllowPatchPreview
            };
        }
    }

    private void LogValidation()
    {
        var policy = DuplicateGroupKeys > 0 ? " Policy=KeepLatest" : string.Empty;
        Console.WriteLine($"[ALLOWLIST_REPAIR_LOCKS_VALIDATION] Total={TotalConfigured} UniqueGroupKeys={UniqueGroupKeys} DuplicateGroupKeys={DuplicateGroupKeys}{policy}");
        foreach (var lockedGroup in LockedGroups.Values.Where(x => !x.AllowPatchPreview).OrderBy(x => x.GroupKey, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"[ALLOWLIST_REPAIR_LOCKED] Group={lockedGroup.GroupKey} Reason={lockedGroup.Reason}");
    }

    public sealed record LockNormalizationResult(int TotalConfigured, int DuplicateGroupKeys, IReadOnlyDictionary<string, AllowlistRepairLockedGroupOptions> LockedGroups);
}
