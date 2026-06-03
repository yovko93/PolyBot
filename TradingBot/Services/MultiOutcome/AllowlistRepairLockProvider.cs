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
        : this(options.Value.AllowlistRepair.LockedGroups, logValidation: true)
    {
    }

    public AllowlistRepairLockProvider(IEnumerable<AllowlistRepairLockedGroupOptions>? configuredLocks, bool logValidation = true)
    {
        var rawLocks = NormalizeInput(configuredLocks).ToList();
        if (rawLocks.Count == 0)
            rawLocks.Add(DefaultPeruLock());

        TotalConfigured = rawLocks.Count;
        var groups = rawLocks.GroupBy(x => x.GroupKey, StringComparer.OrdinalIgnoreCase).ToArray();
        DuplicateGroupKeys = groups.Sum(g => Math.Max(0, g.Count() - 1));
        LockedGroups = groups.ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        if (logValidation)
            LogValidation();
    }

    public bool TryGetLock(string? groupKey, out AllowlistRepairLockedGroupOptions lockedGroup)
    {
        lockedGroup = default!;
        return !string.IsNullOrWhiteSpace(groupKey) && LockedGroups.TryGetValue(groupKey.Trim(), out lockedGroup!);
    }

    public bool IsHardLocked(string? groupKey)
        => TryGetLock(groupKey, out var lockedGroup) && !lockedGroup.AllowPatchPreview;

    internal static AllowlistRepairLockedGroupOptions DefaultPeruLock() => new()
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
}
