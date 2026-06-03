using System.Text.Json.Nodes;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class AllowlistRepairSafetyTests
{
    private const string PeruGroupKey = "winner:2026 peruvian presidential election|kind:person";
    private const string PeruOscillatingMarketId = "947269";

    [Fact]
    public void LockedPeruGroup_IsNeverPatchable_AndReturnsManualReviewUnsafe()
    {
        var service = new AllowlistRepairService();
        var options = new AllowlistRepairOptions
        {
            UseLatestCandidateExportForRepair = false,
            LockedGroups =
            [
                new AllowlistRepairLockedGroupOptions
                {
                    GroupKey = PeruGroupKey,
                    Reason = "Manual review required due to repair diff oscillation on marketId 947269",
                    AllowPatchPreview = false
                }
            ]
        };
        service.BuildReport([], [], [], [], options);

        var preview = service.BuildPatchPreview(
            BuildReport("locked-snapshot", addMarket: true),
            [PeruConfig(includeOscillatingMarket: false)]);
        var patch = Assert.Single(preview.PatchPreview.Patches);

        Assert.Equal(0, preview.PatchPreview.Summary.PatchableHighConfidence + preview.PatchPreview.Summary.PatchableMediumConfidence);
        Assert.Equal(1, preview.PatchPreview.Summary.Locked);
        Assert.Equal("ReviewOnly", patch.PatchType);
        Assert.Equal(nameof(AllowlistRepairRecommendedAction.NeedsManualReview), patch.CurrentAction);
        Assert.Equal("Unsafe", patch.Confidence);
        Assert.Contains(patch.RiskNotes, x => x.Contains("ManualLock", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InverseDiff_DetectsOscillation_AndQuarantinesPeru()
    {
        var service = new AllowlistRepairService();
        var options = new AllowlistRepairOptions { UseLatestCandidateExportForRepair = false, LockedGroups = [] };
        service.BuildReport([], [], [], [], options);

        service.BuildPatchPreview(BuildReport("previous-remove", addMarket: false), [PeruConfig(includeOscillatingMarket: true)]);
        var current = service.BuildPatchPreview(BuildReport("current-add", addMarket: true), [PeruConfig(includeOscillatingMarket: false)]);
        var patch = Assert.Single(current.PatchPreview.Patches);

        Assert.Equal(0, current.PatchPreview.Summary.PatchableHighConfidence + current.PatchPreview.Summary.PatchableMediumConfidence);
        Assert.Equal(1, current.PatchPreview.Summary.Quarantined);
        Assert.Equal("ReviewOnly", patch.PatchType);
        Assert.Equal(nameof(AllowlistRepairRecommendedAction.NeedsManualReview), patch.CurrentAction);
        Assert.Equal("Unsafe", patch.Confidence);
        Assert.Contains(patch.RiskNotes, x => x.Contains("RepairDiffOscillation", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(PeruOscillatingMarketId, service.BuildRepairHistoryExport().Groups.Single().OscillatingMarketIds);
    }

    [Fact]
    public void RepairHistory_IsPersisted_AndLoadedAcrossServiceRestarts()
    {
        var temp = Directory.CreateTempSubdirectory("repair-history-test-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(temp, "exports"));
            var options = new AllowlistRepairOptions { UseLatestCandidateExportForRepair = false, LockedGroups = [] };
            var first = new AllowlistRepairService();
            first.BuildReport([], [], [], [], options, temp);
            first.BuildPatchPreview(BuildReport("previous-remove", addMarket: false), [PeruConfig(includeOscillatingMarket: true)]);
            first.ExportRepairHistory(Path.Combine(temp, "exports", "allowlist-repair-history-latest.json"));

            var second = new AllowlistRepairService();
            second.BuildReport([], [], [], [], options, temp);
            var current = second.BuildPatchPreview(BuildReport("current-add", addMarket: true), [PeruConfig(includeOscillatingMarket: false)]);

            Assert.Equal(1, current.PatchPreview.Summary.Quarantined);
            Assert.True(File.Exists(Path.Combine(temp, "exports", "allowlist-repair-history-latest.json")));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    [Fact]
    public void RefreshPatch_RequiresTwoStableSnapshotsBeforePatchable()
    {
        var service = new AllowlistRepairService();
        var options = new AllowlistRepairOptions { UseLatestCandidateExportForRepair = false, RequiredStableRepairSnapshots = 2, LockedGroups = [] };
        service.BuildReport([], [], [], [], options);

        var first = service.BuildPatchPreview(BuildReport("stable-1", addMarket: true), [PeruConfig(includeOscillatingMarket: false)]);
        var second = service.BuildPatchPreview(BuildReport("stable-2", addMarket: true), [PeruConfig(includeOscillatingMarket: false)]);

        Assert.Equal(0, first.PatchPreview.Summary.PatchableHighConfidence + first.PatchPreview.Summary.PatchableMediumConfidence);
        Assert.Equal(1, second.PatchPreview.Summary.PatchableHighConfidence + second.PatchPreview.Summary.PatchableMediumConfidence);
    }

    private static VerifiedMultiOutcomeGroupConfig PeruConfig(bool includeOscillatingMarket)
        => new(
            true,
            PeruGroupKey,
            "2026 Peruvian presidential election winner",
            includeOscillatingMarket ? ["base-market", PeruOscillatingMarketId] : ["base-market"],
            includeOscillatingMarket ? ["base-condition", "peru-condition"] : ["base-condition"],
            null,
            "Verified");

    private static AllowlistRepairReport BuildReport(string snapshotId, bool addMarket)
    {
        var suggested = SuggestedTemplate(addMarket);
        var match = new AllowlistRepairMatch(
            PeruGroupKey,
            1m,
            1m,
            1m,
            1m,
            1m,
            addMarket ? 2 : 1,
            0,
            addMarket ? [PeruOscillatingMarketId] : [],
            addMarket ? [] : [PeruOscillatingMarketId],
            0,
            "High");
        var group = new AllowlistRepairGroup(
            snapshotId,
            1,
            null,
            nameof(AllowlistRepairRecommendedAction.RefreshFromCandidateExport),
            DateTime.UtcNow,
            "Test",
            PeruGroupKey,
            "2026 Peruvian presidential election winner",
            true,
            "NeedsRepair",
            nameof(AllowlistRepairHealthCategory.NeedsRefresh),
            false,
            false,
            1,
            0,
            1,
            [],
            0,
            0,
            [],
            "TestMismatch",
            null,
            nameof(AllowlistRepairRecommendedAction.RefreshFromCandidateExport),
            "High",
            "Test repair",
            null,
            suggested,
            match,
            0,
            [],
            "Manual review required.",
            "Review only.");
        var summary = new AllowlistRepairSummary(1, 0, 0, 0, 1, 0, 0, 0, 1, true);
        var counts = new AllowlistRepairCategoryCounts(0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 1);
        return new AllowlistRepairReport(
            snapshotId,
            DateTime.UtcNow,
            summary,
            counts,
            true,
            new AllowlistRepairSnapshot(snapshotId, DateTime.UtcNow, snapshotId, string.Empty, 1, 1, "Test", [group]),
            1,
            0,
            0,
            1,
            1,
            0,
            0,
            0,
            0,
            [group],
            [],
            "Test");
    }

    private static JsonObject SuggestedTemplate(bool includeOscillatingMarket) => new()
    {
        ["enabled"] = true,
        ["groupKey"] = PeruGroupKey,
        ["title"] = "2026 Peruvian presidential election winner",
        ["verificationStatus"] = "Verified",
        ["groupType"] = "MutuallyExclusiveWinner",
        ["allowedStrategy"] = "BUY_ALL_NO_MUTUALLY_EXCLUSIVE",
        ["marketIds"] = ToJsonArray(includeOscillatingMarket ? ["base-market", PeruOscillatingMarketId] : ["base-market"]),
        ["conditionIds"] = ToJsonArray(includeOscillatingMarket ? ["base-condition", "peru-condition"] : ["base-condition"]),
        ["requiredOutcomeCount"] = includeOscillatingMarket ? 2 : 1,
        ["requireExactOutcomeCount"] = false,
        ["settlementNotes"] = "Manual review required.",
        ["verifiedBy"] = "manual-review-required",
        ["verifiedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd")
    };

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var arr = new JsonArray();
        foreach (var value in values) arr.Add(value);
        return arr;
    }
}
