using System.Text.Json;
using System.Text.Json.Nodes;
using TradingBot.Models;
using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class AllowlistRepairServiceTests
{

    [Fact]
    public void Shared_classifier_counts_match_health_and_repair_report()
    {
        var report = new AllowlistRepairService().BuildReport(Configured(), ResolvedWithColombianPricingProblem(), Pricing(), [NbaCandidate()]);
        var healthHealthy = report.Summary.Healthy + report.Summary.MonitoringOnly;
        var healthBroken = report.Summary.Broken;

        Assert.Equal(report.Healthy, healthHealthy);
        Assert.Equal(report.Broken, healthBroken);
        Assert.Equal(report.Summary.NeedsPricingPrune, report.NeedsPricingPrune);
        Assert.Equal(report.Summary.Broken, report.CategoryCounts.BrokenTotal);
        Assert.True(report.InvariantResult);
    }

    [Fact]
    public void Classification_invariant_passes_for_11_configured_groups()
    {
        var configured = Configured().Concat(Enumerable.Range(1, 7).Select(i => new VerifiedMultiOutcomeGroupConfig(true, $"winner:healthy-{i}|kind:generic", $"Healthy {i}", [$"h{i}"], [], 1, "Verified"))).ToArray();
        var resolved = ResolvedWithColombianPricingProblem().Concat(Enumerable.Range(1, 7).Select(i => new ResolvedVerifiedGroup($"winner:healthy-{i}|kind:generic", $"Healthy {i}", [$"h{i}"], [], [Market($"h{i}")], [], [], "VerifiedGroupResolved", "VerifiedGroupResolved"))).ToArray();

        var report = new AllowlistRepairService().BuildReport(configured, resolved, Pricing(), [NbaCandidate()]);

        Assert.Equal(11, report.ConfiguredGroups);
        Assert.True(report.Summary.InvariantOk);
        Assert.Equal(11, report.Summary.Healthy + report.Summary.MonitoringOnly + report.Summary.NeedsPricingPrune + report.Summary.NeedsRefresh + report.Summary.BrokenConfig + report.Summary.Disabled + report.Summary.Ignored);
    }

    [Fact]
    public void Repair_action_does_not_change_within_same_snapshot()
    {
        var svc = new AllowlistRepairService();
        var first = svc.BuildReport(Configured(), ResolvedMismatches(), [], [NbaCandidate()]);
        var second = svc.BuildReport(Configured(), ResolvedMismatches(), [], []);

        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal("RefreshFromCandidateExport", first.Groups.Single(x => x.GroupKey.Contains("nba finals", StringComparison.OrdinalIgnoreCase)).RecommendedAction);
        Assert.Equal("RefreshFromCandidateExport", second.Groups.Single(x => x.GroupKey.Contains("nba finals", StringComparison.OrdinalIgnoreCase)).RecommendedAction);
    }

    [Fact]
    public void Peru_stable_refresh_from_candidate_export_with_cached_match()
    {
        var svc = new AllowlistRepairService();
        var first = svc.BuildReport(Configured(), ResolvedMismatches(), [], [PeruCandidate()]);
        var second = svc.BuildReport(Configured(), ResolvedMismatches(), [], []);

        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal("RefreshFromCandidateExport", second.Groups.Single(x => x.GroupKey.Contains("peruvian", StringComparison.OrdinalIgnoreCase)).RecommendedAction);
    }

    [Fact]
    public void Repair_action_changes_only_when_snapshot_id_changes_and_downgrade_cycles_are_met()
    {
        var svc = new AllowlistRepairService();
        var dir = Directory.CreateTempSubdirectory();
        var opts = new TradingBot.Options.AllowlistRepairOptions { MatchFailureDowngradeCycles = 2, PreferStableCachedMatches = true, UseLatestCandidateExportForRepair = true };
        WriteCandidateExport(dir.FullName, [NbaCandidate()]);
        var first = svc.BuildReport(Configured(), ResolvedMismatches(), [], [], opts, dir.FullName);
        WriteCandidateExport(dir.FullName, []);
        var oneMiss = svc.BuildReport(Configured(), ResolvedMismatches(), [], [], opts, dir.FullName);
        WriteCandidateExport(dir.FullName, [], "\n ");
        var twoMisses = svc.BuildReport(Configured(), ResolvedMismatches(), [], [], opts, dir.FullName);

        Assert.NotEqual(first.SnapshotId, oneMiss.SnapshotId);
        Assert.Equal("RefreshFromCandidateExport", oneMiss.Groups.Single(x => x.GroupKey.Contains("nba finals", StringComparison.OrdinalIgnoreCase)).RecommendedAction);
        Assert.Equal("NeedsManualReview", twoMisses.Groups.Single(x => x.GroupKey.Contains("nba finals", StringComparison.OrdinalIgnoreCase)).RecommendedAction);
        Assert.Equal(2, twoMisses.Groups.Single(x => x.GroupKey.Contains("nba finals", StringComparison.OrdinalIgnoreCase)).ActionVersion);
    }


    [Fact]
    public void Womens_us_open_does_not_flip_flop_within_same_snapshot()
    {
        var svc = new AllowlistRepairService();
        var first = svc.BuildReport(Configured(), ResolvedMismatches(), [], [WomensCandidate()]);
        var second = svc.BuildReport(Configured(), ResolvedMismatches(), [], []);
        var womensFirst = first.Groups.Single(x => x.GroupKey.Contains("women s us open", StringComparison.OrdinalIgnoreCase));
        var womensSecond = second.Groups.Single(x => x.GroupKey.Contains("women s us open", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal(womensFirst.RecommendedAction, womensSecond.RecommendedAction);
        Assert.Equal("RefreshFromCandidateExport", womensSecond.RecommendedAction);
    }

    [Fact]
    public void Nba_stable_refresh_from_candidate_export_with_cached_match()
    {
        var svc = new AllowlistRepairService();
        var first = svc.BuildReport(Configured(), ResolvedMismatches(), [], [NbaCandidate()]);
        var second = svc.BuildReport(Configured(), ResolvedMismatches(), [], []);

        Assert.Equal(first.SnapshotId, second.SnapshotId);
        Assert.Equal("RefreshFromCandidateExport", second.Groups.Single(x => x.GroupKey.Contains("nba finals", StringComparison.OrdinalIgnoreCase)).RecommendedAction);
    }

    [Fact]
    public void Repair_report_export_is_overwritten()
    {
        var svc = new AllowlistRepairService();
        var dir = Directory.CreateTempSubdirectory();
        var reportPath = Path.Combine(dir.FullName, "verified-allowlist-repair-report-latest.json");
        var suggestedPath = Path.Combine(dir.FullName, "verified-multi-outcome-groups-repair-suggested.json");
        File.WriteAllText(reportPath, "OLD");

        svc.Export(reportPath, suggestedPath, Configured(), ResolvedWithColombianPricingProblem(), Pricing(), []);

        Assert.NotEqual("OLD", File.ReadAllText(reportPath));
        Assert.Contains("repairSuggestions", File.ReadAllText(reportPath));
        Assert.Contains("copyInstructions", File.ReadAllText(reportPath));
        Assert.Contains("snapshotId", File.ReadAllText(reportPath));
    }

    [Fact]
    public void Repair_report_export_is_created_when_broken_exists()
    {
        var svc = new AllowlistRepairService();
        var dir = Directory.CreateTempSubdirectory();
        var reportPath = Path.Combine(dir.FullName, "verified-allowlist-repair-report-latest.json");
        var suggestedPath = Path.Combine(dir.FullName, "verified-multi-outcome-groups-repair-suggested.json");

        svc.Export(reportPath, suggestedPath, Configured(), ResolvedWithColombianPricingProblem(), Pricing(), []);

        Assert.True(File.Exists(reportPath));
        Assert.True(File.Exists(suggestedPath));
        Assert.Contains("snapshotId", File.ReadAllText(suggestedPath));
    }

    [Fact]
    public void Suggested_config_export_does_not_overwrite_real_config()
    {
        var svc = new AllowlistRepairService();
        var dir = Directory.CreateTempSubdirectory();
        var configPath = Path.Combine(dir.FullName, "verified-multi-outcome-groups.json");
        File.WriteAllText(configPath, "REAL_CONFIG");

        svc.Export(Path.Combine(dir.FullName, "report.json"), Path.Combine(dir.FullName, "suggested.json"), Configured(), ResolvedWithColombianPricingProblem(), Pricing(), []);

        Assert.Equal("REAL_CONFIG", File.ReadAllText(configPath));
    }

    [Fact]
    public void Colombian_missing_no_ask_gets_prune_template_excluding_missing_market()
    {
        var report = new AllowlistRepairService().BuildReport(Configured(), ResolvedWithColombianPricingProblem(), Pricing(), []);
        var col = report.Groups.Single(x => x.GroupKey.Contains("colombian", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("NeedsPricingPrune", col.HealthCategory);
        Assert.Equal("PruneMissingNoAskLegs", col.RecommendedAction);
        Assert.Equal(1, col.MissingNoAsk);
        var marketIds = col.SuggestedPrunedTemplate!["marketIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        Assert.DoesNotContain("569373", marketIds);
        Assert.Equal(2, marketIds.Length);
        Assert.Equal(2, col.SuggestedPrunedTemplate!["requiredOutcomeCount"]!.GetValue<int>());
        Assert.False(col.SuggestedPrunedTemplate!["requireExactOutcomeCount"]!.GetValue<bool>());
        Assert.Contains("Missing NO ask leg excluded", col.SuggestedPrunedTemplate!["settlementNotes"]!.GetValue<string>());
    }

    [Fact]
    public void Nba_finals_mismatch_attempts_refresh_from_candidate_export()
    {
        var report = new AllowlistRepairService().BuildReport(Configured(), ResolvedMismatches(), [], [NbaCandidate()]);
        var nba = report.Groups.Single(x => x.GroupKey.Contains("nba finals", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("NeedsRefresh", nba.HealthCategory);
        Assert.Equal("RefreshFromCandidateExport", nba.RecommendedAction);
        Assert.NotNull(nba.SuggestedRefreshedTemplate);
    }

    [Fact]
    public void Womens_us_open_mismatch_does_not_create_pruned_template_when_resolved_less_than_two()
    {
        var report = new AllowlistRepairService().BuildReport(Configured(), ResolvedMismatches(), [], []);
        var womens = report.Groups.Single(x => x.GroupKey.Contains("women s us open", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("BrokenConfig", womens.HealthCategory);
        Assert.Null(womens.SuggestedPrunedTemplate);
        Assert.Equal("DisableMissingMarkets", womens.RecommendedAction);
    }

    [Fact]
    public void Peru_mismatch_becomes_manual_review_without_safe_candidate()
    {
        var report = new AllowlistRepairService().BuildReport(Configured(), ResolvedMismatches(), [], []);
        var peru = report.Groups.Single(x => x.GroupKey.Contains("peruvian", StringComparison.OrdinalIgnoreCase));

        Assert.True(peru.HealthCategory is "NeedsRefresh" or "BrokenConfig");
        Assert.True(peru.RecommendedAction is "NeedsManualReview" or "DisableMissingMarkets");
    }

    [Fact]
    public void Suggested_config_contains_all_current_configured_groups()
    {
        var svc = new AllowlistRepairService();
        var report = svc.BuildReport(Configured(), ResolvedWithColombianPricingProblem(), Pricing(), []);
        var suggested = svc.BuildSuggestedConfig(report);

        Assert.Equal(report.SnapshotId, suggested.SnapshotId);
        Assert.Equal(Configured().Count, suggested.Groups.Count);
        Assert.Contains(suggested.Groups, x => x.GroupKey.Contains("colombian", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Api_style_bounded_repair_report_limits_groups()
    {
        var report = new AllowlistRepairService().BuildReport(Configured(), ResolvedWithColombianPricingProblem(), Pricing(), [NbaCandidate()]);

        var bounded = report.Groups.Take(2).ToArray();

        Assert.Equal(2, bounded.Length);
        Assert.True(report.Groups.Count > bounded.Length);
    }


    [Fact]
    public void Patch_preview_export_is_created_and_does_not_overwrite_real_config()
    {
        var svc = new AllowlistRepairService();
        var dir = Directory.CreateTempSubdirectory();
        var configDir = Directory.CreateDirectory(Path.Combine(dir.FullName, "config"));
        var realConfig = new JsonArray(Configured().Select(ConfigNode).ToArray()).ToJsonString();
        var configPath = Path.Combine(configDir.FullName, "verified-multi-outcome-groups.json");
        File.WriteAllText(configPath, realConfig);
        var report = svc.BuildReport(Configured(), ResolvedWithColombianPricingProblem(), Pricing(), [NbaCandidate(), PeruCandidate()]);
        var patchPath = Path.Combine(dir.FullName, "exports/verified-allowlist-repair-patch-preview-latest.json");
        var previewPath = Path.Combine(dir.FullName, "exports/verified-multi-outcome-groups-patched-preview.json");
        var metadataPath = Path.Combine(dir.FullName, "exports/verified-multi-outcome-groups-patched-preview.with-metadata.json");

        var export = svc.ExportPatchPreview(patchPath, previewPath, metadataPath, report, Configured(), dir.FullName);

        Assert.True(File.Exists(patchPath));
        Assert.True(File.Exists(previewPath));
        Assert.True(File.Exists(metadataPath));
        Assert.Equal(realConfig, File.ReadAllText(configPath));
        Assert.False(export.PatchPreview.WillOverwriteRealConfig);
        Assert.Equal("ManualPreviewOnly", export.PatchPreview.Mode);
    }

    [Fact]
    public void Patch_preview_marks_high_confidence_repairs_patchable_and_low_confidence_review_only()
    {
        var svc = new AllowlistRepairService();
        var report = svc.BuildReport(Configured(), ResolvedWithColombianPricingProblem(), Pricing(), [NbaCandidate(), PeruCandidate()]);
        var export = svc.BuildPatchPreview(report, Configured());
        var patches = export.PatchPreview.Patches;

        var col = patches.Single(x => x.GroupKey.Contains("colombian presidential", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("PruneGroup", col.PatchType);
        Assert.Equal("High", col.Confidence);
        Assert.DoesNotContain("569373", col.ProposedGroup!["marketIds"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal(2, col.ProposedGroup!["requiredOutcomeCount"]!.GetValue<int>());
        Assert.False(col.ProposedGroup!["requireExactOutcomeCount"]!.GetValue<bool>());
        Assert.True(col.ProposedGroup!["enabled"]!.GetValue<bool>());
        Assert.Contains("Missing NO ask leg excluded", col.ProposedGroup!["settlementNotes"]!.GetValue<string>());

        var nba = patches.Single(x => x.GroupKey.Contains("nba finals", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ReplaceGroup", nba.PatchType);
        Assert.Contains("nba-old", nba.Diff!["removedMarketIds"]!.AsArray().Select(x => x!.GetValue<string>()));

        var peru = patches.Single(x => x.GroupKey.Contains("peruvian", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ReplaceGroup", peru.PatchType);
        Assert.Equal("RefreshFromCandidateExport", peru.ProposedGroup!["repairAction"]!.GetValue<string>());

        var womens = patches.Single(x => x.GroupKey.Contains("women s us open", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("ReviewOnly", womens.PatchType);
        Assert.Null(womens.ProposedGroup);
        Assert.Contains(womens.RiskNotes, x => x.Contains("Low confidence", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void First_round_colombian_low_confidence_is_review_only()
    {
        var configured = Configured().Concat([new VerifiedMultiOutcomeGroupConfig(true, "winner:1st round of 2026 colombian presidential election|kind:person", "1st round Colombia", ["round-old"], [], 1, "Verified")]).ToArray();
        var resolved = ResolvedWithColombianPricingProblem().Concat([new ResolvedVerifiedGroup("winner:1st round of 2026 colombian presidential election|kind:person", "1st round Colombia", ["round-old"], [], [], ["round-old"], [], "Rejected", "VerifiedGroupMarketMismatch")]).ToArray();
        var report = new AllowlistRepairService().BuildReport(configured, resolved, Pricing(), [NbaCandidate(), PeruCandidate()]);
        var export = new AllowlistRepairService().BuildPatchPreview(report, configured);

        var firstRound = export.PatchPreview.Patches.Single(x => x.GroupKey.Contains("1st round", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("NeedsManualReview", firstRound.CurrentAction);
        Assert.Equal("Low", firstRound.Confidence);
        Assert.Equal("ReviewOnly", firstRound.PatchType);
        Assert.Null(firstRound.ProposedGroup);
    }

    [Fact]
    public void Patched_preview_includes_all_original_groups_and_applies_only_high_medium()
    {
        var svc = new AllowlistRepairService();
        var report = svc.BuildReport(Configured(), ResolvedWithColombianPricingProblem(), Pricing(), [NbaCandidate(), PeruCandidate()]);
        var export = svc.BuildPatchPreview(report, Configured());
        var groups = export.PatchedPreviewConfig.AsArray();

        Assert.Equal(Configured().Count, groups.Count);
        Assert.Contains(groups, x => x!["groupKey"]!.GetValue<string>().Contains("women s us open", StringComparison.OrdinalIgnoreCase) && x["marketIds"]!.AsArray().Single()!.GetValue<string>() == "w-us-open-old");
        Assert.Contains(groups, x => x!["groupKey"]!.GetValue<string>().Contains("nba finals", StringComparison.OrdinalIgnoreCase) && x["marketIds"]!.AsArray().Any(m => m!.GetValue<string>() == "nba-1"));
        Assert.Equal(3, export.PatchPreview.Summary.PatchableHighConfidence);
    }

    [Fact]
    public void Post_apply_validation_plan_is_included()
    {
        var report = new AllowlistRepairService().BuildReport(Configured(), ResolvedWithColombianPricingProblem(), Pricing(), [NbaCandidate(), PeruCandidate()]);
        var preview = new AllowlistRepairService().BuildPatchPreview(report, Configured()).PatchPreview;

        Assert.NotEmpty(preview.PostApplyValidationPlan.Steps);
        Assert.Contains(preview.PostApplyValidationPlan.CommandsOrEndpointsToCheck, x => x.Contains("allowlist-repair-patch-preview", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(preview.PostApplyValidationPlan.ExpectedOutcomes, x => x.Contains("Colombian", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Api_patch_preview_file_shape_is_returnable()
    {
        var svc = new AllowlistRepairService();
        var dir = Directory.CreateTempSubdirectory();
        var report = svc.BuildReport(Configured(), ResolvedWithColombianPricingProblem(), Pricing(), [NbaCandidate(), PeruCandidate()]);
        var patchPath = Path.Combine(dir.FullName, "exports/verified-allowlist-repair-patch-preview-latest.json");
        svc.ExportPatchPreview(patchPath, Path.Combine(dir.FullName, "exports/verified-multi-outcome-groups-patched-preview.json"), Path.Combine(dir.FullName, "exports/verified-multi-outcome-groups-patched-preview.with-metadata.json"), report, Configured(), dir.FullName);

        var node = JsonNode.Parse(File.ReadAllText(patchPath))!.AsObject();

        Assert.Equal("ManualPreviewOnly", node["mode"]!.GetValue<string>());
        Assert.False(node["willOverwriteRealConfig"]!.GetValue<bool>());
        Assert.True(node["patches"]!.AsArray().Count > 0);
    }


    [Fact]
    public void No_op_refresh_match_is_keep_monitoring_and_not_patchable()
    {
        var cfg = new VerifiedMultiOutcomeGroupConfig(true, "winner:2026 peruvian presidential election|kind:person", "Peru", ["peru-1", "peru-2"], ["peru-c1", "peru-c2"], 2, "Verified");
        var resolved = new ResolvedVerifiedGroup(cfg.GroupKey, "Peru", cfg.MarketIds, cfg.ConditionIds, [], [], [], "Rejected", "VerifiedGroupMarketMismatch");
        var report = new AllowlistRepairService().BuildReport([cfg], [resolved], [], [PeruCandidate()]);
        var group = report.Groups.Single();
        var preview = new AllowlistRepairService().BuildPatchPreview(report, [cfg]).PatchPreview;

        Assert.Equal("KeepMonitoring", group.RecommendedAction);
        Assert.Equal("MonitoringOnly", group.HealthCategory);
        Assert.Empty(preview.Patches.Where(x => x.GroupKey == cfg.GroupKey && x.PatchType != "None"));
        Assert.Equal(0, preview.Summary.PatchableHighConfidence);
    }

    [Fact]
    public void Patchable_count_excludes_no_op_refresh_repairs()
    {
        var fifa = FifaConfig();
        var peru = new VerifiedMultiOutcomeGroupConfig(true, "winner:2026 peruvian presidential election|kind:person", "Peru", ["peru-1", "peru-2"], ["peru-c1", "peru-c2"], 2, "Verified");
        var resolved = new[]
        {
            FifaResolved(fifa),
            new ResolvedVerifiedGroup(peru.GroupKey, "Peru", peru.MarketIds, peru.ConditionIds, [], [], [], "Rejected", "VerifiedGroupMarketMismatch")
        };
        var report = new AllowlistRepairService().BuildReport([fifa, peru], resolved, [FifaPricing()], [PeruCandidate()]);
        var preview = new AllowlistRepairService().BuildPatchPreview(report, [fifa, peru]).PatchPreview;

        Assert.Equal(1, preview.Summary.PatchableHighConfidence);
        Assert.Single(preview.Patches.Where(x => x.PatchType == "PruneGroup"));
        Assert.DoesNotContain(preview.Patches, x => x.GroupKey == peru.GroupKey && x.PatchType == "ReplaceGroup");
    }

    [Fact]
    public void Fifa_patch_removes_558954_and_validates_final_35_legs()
    {
        var fifa = FifaConfig();
        var report = new AllowlistRepairService().BuildReport([fifa], [FifaResolved(fifa)], [FifaPricing()], []);
        var preview = new AllowlistRepairService().BuildPatchPreview(report, [fifa]).PatchPreview;
        var patch = preview.Patches.Single(x => x.GroupKey.Contains("fifa world cup", StringComparison.OrdinalIgnoreCase));
        var proposed = patch.ProposedGroup!.AsObject();
        var marketIds = proposed["marketIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        var conditionIds = proposed["conditionIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        var validation = AllowlistRepairService.ValidatePatch(patch);

        Assert.Equal("PruneGroup", patch.PatchType);
        Assert.DoesNotContain("558954", marketIds);
        Assert.Equal(35, marketIds.Length);
        Assert.Equal(35, conditionIds.Length);
        Assert.Equal(35, proposed["requiredOutcomeCount"]!.GetValue<int>());
        Assert.False(proposed["requireExactOutcomeCount"]!.GetValue<bool>());
        Assert.Contains("Missing NO ask leg excluded", proposed["settlementNotes"]!.GetValue<string>());
        Assert.Equal("PruneMissingNoAskLegs", proposed["repairAction"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(proposed["repairSourceSnapshotId"]!.GetValue<string>()));
        Assert.True(validation.Valid);
        Assert.Equal(35, validation.FinalLegs);
        Assert.Contains("558954", validation.RemovedMarketIds);
    }

    private static IReadOnlyList<VerifiedMultiOutcomeGroupConfig> Configured() =>
    [
        new(true, "winner:2026 colombian presidential election|kind:person", "Colombia", ["m-priced-1", "569373", "m-priced-2"], [], 3, "Verified"),
        new(true, "winner:2026 nba finals|kind:generic", "2026 NBA Finals", ["nba-old"], [], 1, "Verified"),
        new(true, "winner:2026 women s us open|kind:generic", "2026 Women s US Open", ["w-us-open-old"], [], 1, "Verified"),
        new(true, "winner:2026 peruvian presidential election|kind:person", "2026 Peruvian Presidential Election", ["peru-old"], [], 1, "Verified")
    ];

    private static IReadOnlyList<ResolvedVerifiedGroup> ResolvedWithColombianPricingProblem() =>
    [
        new("winner:2026 colombian presidential election|kind:person", "Colombia", ["m-priced-1", "569373", "m-priced-2"], [], [Market("m-priced-1"), Market("569373"), Market("m-priced-2")], [], [], "VerifiedGroupResolved", "VerifiedGroupResolved"),
        new("winner:2026 nba finals|kind:generic", "2026 NBA Finals", ["nba-old"], [], [], ["nba-old"], [], "Rejected", "VerifiedGroupMarketMismatch"),
        new("winner:2026 women s us open|kind:generic", "2026 Women s US Open", ["w-us-open-old"], [], [], ["w-us-open-old"], [], "Rejected", "VerifiedGroupMarketMismatch"),
        new("winner:2026 peruvian presidential election|kind:person", "Peru", ["peru-old"], [], [Market("peru-found")], ["peru-old"], [], "Rejected", "VerifiedGroupMarketMismatch")
    ];

    private static IReadOnlyList<ResolvedVerifiedGroup> ResolvedMismatches() => ResolvedWithColombianPricingProblem().Where(x => !x.GroupKey.Contains("colombian", StringComparison.OrdinalIgnoreCase)).ToArray();

    private static IReadOnlyList<object> Pricing() =>
    [
        new
        {
            groupKey = "winner:2026 colombian presidential election|kind:person",
            noAskResolvedCount = 2,
            missingNoAskCount = 1,
            pricedLegs = new[] { Leg("m-priced-1", "c1"), Leg("m-priced-2", "c2") },
            missingPriceLegs = new[] { Leg("569373", "c-missing") }
        }
    ];

    private static object Leg(string marketId, string conditionId) => new { marketId, conditionId, noAsk = 0.5m, noTokenId = "no-" + marketId };

    private static JsonObject NbaCandidate() => new()
    {
        ["groupKey"] = "winner:2026 nba finals|kind:generic",
        ["title"] = "Winner: 2026 NBA Finals",
        ["markets"] = new JsonArray(
            MarketNode("nba-1", "nba-c1", "Will Boston win the 2026 NBA Finals?"),
            MarketNode("nba-2", "nba-c2", "Will Denver win the 2026 NBA Finals?"))
    };

    private static JsonObject PeruCandidate() => new()
    {
        ["groupKey"] = "winner:2026 peruvian presidential election|kind:person",
        ["title"] = "Winner: 2026 Peruvian Presidential Election",
        ["markets"] = new JsonArray(
            MarketNode("peru-1", "peru-c1", "Will Candidate A win the 2026 Peruvian presidential election?"),
            MarketNode("peru-2", "peru-c2", "Will Candidate B win the 2026 Peruvian presidential election?"))
    };

    private static JsonObject WomensCandidate() => new()
    {
        ["groupKey"] = "winner:2026 women s us open|kind:generic",
        ["title"] = "Winner: 2026 Women s US Open",
        ["markets"] = new JsonArray(
            MarketNode("w-1", "w-c1", "Will Player A win the 2026 Women s US Open?"),
            MarketNode("w-2", "w-c2", "Will Player B win the 2026 Women s US Open?"))
    };

    private static VerifiedMultiOutcomeGroupConfig FifaConfig()
    {
        var marketIds = Enumerable.Range(0, 36).Select(i => i == 10 ? "558954" : $"fifa-{i:00}").ToArray();
        var conditionIds = Enumerable.Range(0, 36).Select(i => $"fifa-c-{i:00}").ToArray();
        return new VerifiedMultiOutcomeGroupConfig(true, "winner:2026 fifa world cup|kind:generic", "2026 FIFA World Cup", marketIds, conditionIds, 36, "Verified");
    }

    private static ResolvedVerifiedGroup FifaResolved(VerifiedMultiOutcomeGroupConfig cfg)
        => new(cfg.GroupKey, cfg.Title ?? cfg.GroupKey, cfg.MarketIds, cfg.ConditionIds, cfg.MarketIds.Select((id, i) => MarketWithCondition(id, cfg.ConditionIds[i])).ToArray(), [], [], "VerifiedGroupResolved", "VerifiedGroupResolved");

    private static object FifaPricing()
    {
        var cfg = FifaConfig();
        var priced = cfg.MarketIds.Zip(cfg.ConditionIds).Where(x => x.First != "558954").Select(x => Leg(x.First, x.Second)).ToArray();
        return new
        {
            groupKey = cfg.GroupKey,
            noAskResolvedCount = 35,
            missingNoAskCount = 1,
            pricedLegs = priced,
            missingPriceLegs = new[] { Leg("558954", cfg.ConditionIds[10]) }
        };
    }

    private static JsonObject ConfigNode(VerifiedMultiOutcomeGroupConfig cfg) => new()
    {
        ["enabled"] = cfg.Enabled,
        ["groupKey"] = cfg.GroupKey,
        ["title"] = cfg.Title,
        ["verificationStatus"] = cfg.VerificationStatus,
        ["verifiedBy"] = "operator",
        ["settlementNotes"] = "Manual metadata",
        ["groupType"] = "MutuallyExclusiveWinner",
        ["allowedStrategy"] = "BUY_ALL_NO_MUTUALLY_EXCLUSIVE",
        ["marketIds"] = new JsonArray(cfg.MarketIds.Select(x => JsonValue.Create(x)).ToArray()),
        ["conditionIds"] = new JsonArray(cfg.ConditionIds.Select(x => JsonValue.Create(x)).ToArray()),
        ["requiredOutcomeCount"] = cfg.RequiredOutcomeCount,
        ["requireExactOutcomeCount"] = false
    };

    private static void WriteCandidateExport(string root, IReadOnlyList<JsonObject> candidates, string suffix = "")
    {
        var dir = Path.Combine(root, "exports");
        Directory.CreateDirectory(dir);
        var json = new JsonArray(candidates.Select(x => JsonNode.Parse(x.ToJsonString())).ToArray());
        File.WriteAllText(Path.Combine(dir, "multi-outcome-candidates-latest.json"), json.ToJsonString() + suffix);
    }

    private static JsonObject MarketNode(string id, string conditionId, string question) => new() { ["marketId"] = id, ["conditionId"] = conditionId, ["question"] = question, ["active"] = true, ["closed"] = false, ["archived"] = false };

    private static Market Market(string id) => new() { id = id, question = "Will candidate win?", active = true, closed = false, archived = false, conditionId = "c-" + id, outcomes = ["Yes", "No"], clobTokenIds = ["yes-" + id, "no-" + id] };
}
