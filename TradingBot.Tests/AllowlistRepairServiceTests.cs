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
    public void Diagnostics_only_during_soak_suppresses_non_critical_repair_patches()
    {
        var svc = new AllowlistRepairService();
        var opts = new TradingBot.Options.AllowlistRepairOptions
        {
            DiagnosticsOnlyDuringSoak = true,
            RequiredStableRepairSnapshots = 1,
            UseLatestCandidateExportForRepair = true
        };
        var report = svc.BuildReport(Configured(), ResolvedMismatches(), [], [WomensCandidate()], opts);

        var preview = svc.BuildPatchPreview(report, Configured()).PatchPreview;
        var womens = preview.Patches.Single(x => x.GroupKey.Contains("women s us open", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("ReviewOnly", womens.PatchType);
        Assert.Contains(womens.RiskNotes, x => x.Contains("DiagnosticsOnlyDuringSoak", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, preview.Summary.PatchableHighConfidence + preview.Summary.PatchableMediumConfidence);
    }

    [Fact]
    public void Womens_us_open_action_change_does_not_become_patchable_during_soak()
    {
        var svc = new AllowlistRepairService();
        var opts = new TradingBot.Options.AllowlistRepairOptions
        {
            DiagnosticsOnlyDuringSoak = true,
            RequiredStableRepairSnapshots = 3,
            QuarantineOnActionChange = true,
            UseLatestCandidateExportForRepair = true
        };
        var report = svc.BuildReport(Configured(), ResolvedMismatches(), [], [WomensCandidate()], opts);

        var preview = svc.BuildPatchPreview(report, Configured()).PatchPreview;
        var womens = preview.Patches.Single(x => x.GroupKey.Contains("women s us open", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("ReviewOnly", womens.PatchType);
        Assert.True(womens.RiskNotes.Any(x => x.Contains("DiagnosticsOnlyDuringSoak", StringComparison.OrdinalIgnoreCase) || x.Contains("RepairDiffNotStable", StringComparison.OrdinalIgnoreCase)));
    }

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
        var report = svc.BuildReport(Configured(), ResolvedWithColombianPricingProblem(), Pricing(), [NbaCandidate(), PeruCandidate()], StableRepairOptions());
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
        var report = svc.BuildReport(Configured(), ResolvedWithColombianPricingProblem(), Pricing(), [NbaCandidate(), PeruCandidate()], StableRepairOptions());
        var export = svc.BuildPatchPreview(report, Configured());
        var groups = export.PatchedPreviewConfig.AsArray();

        Assert.Equal(Configured().Count, groups.Count);
        Assert.Contains(groups, x => x!["groupKey"]!.GetValue<string>().Contains("women s us open", StringComparison.OrdinalIgnoreCase) && x["marketIds"]!.AsArray().Single()!.GetValue<string>() == "w-us-open-old");
        Assert.Contains(groups, x => x!["groupKey"]!.GetValue<string>().Contains("nba finals", StringComparison.OrdinalIgnoreCase) && x["marketIds"]!.AsArray().Any(m => m!.GetValue<string>() == "nba-1"));
        Assert.Equal(3, export.PatchPreview.Summary.PatchableHighConfidence);
    }


    [Fact]
    public void Refresh_match_with_zero_added_removed_and_same_conditions_is_not_patchable()
    {
        var configured = new[]
        {
            new VerifiedMultiOutcomeGroupConfig(true, "winner:2026 peruvian presidential election|kind:person", "2026 Peruvian Presidential Election", ["peru-1", "peru-2"], ["peru-c1", "peru-c2"], 2, "Verified")
        };
        var resolved = new[]
        {
            new ResolvedVerifiedGroup(configured[0].GroupKey, configured[0].Title!, configured[0].MarketIds, configured[0].ConditionIds, [], ["peru-1", "peru-2"], [], "Rejected", "VerifiedGroupMarketMismatch")
        };

        var report = new AllowlistRepairService().BuildReport(configured, resolved, [], [PeruCandidate()]);
        var peru = report.Groups.Single();
        var export = new AllowlistRepairService().BuildPatchPreview(report, configured);

        Assert.Empty(peru.RepairMatch!.AddedMarketIds);
        Assert.Empty(peru.RepairMatch!.RemovedMarketIds);
        Assert.Equal(0, peru.RepairMatch!.ChangedConditionIds);
        Assert.False(export.PatchPreview.Patches.Any(x => x.GroupKey == peru.GroupKey && x.PatchType == "ReplaceGroup"));
    }

    [Fact]
    public void No_op_refresh_becomes_keep_monitoring()
    {
        var configured = new[]
        {
            new VerifiedMultiOutcomeGroupConfig(true, "winner:2026 peruvian presidential election|kind:person", "2026 Peruvian Presidential Election", ["peru-1", "peru-2"], ["peru-c1", "peru-c2"], 2, "Verified")
        };
        var resolved = new[]
        {
            new ResolvedVerifiedGroup(configured[0].GroupKey, configured[0].Title!, configured[0].MarketIds, configured[0].ConditionIds, [], ["peru-1", "peru-2"], [], "Rejected", "VerifiedGroupMarketMismatch")
        };

        var report = new AllowlistRepairService().BuildReport(configured, resolved, [], [PeruCandidate()]);
        var peru = report.Groups.Single();

        Assert.Equal("KeepMonitoring", peru.RecommendedAction);
        Assert.Equal("MonitoringOnly", peru.HealthCategory);
    }

    [Fact]
    public void Patchable_count_excludes_no_op_repairs()
    {
        var configured = new[]
        {
            new VerifiedMultiOutcomeGroupConfig(true, "winner:2026 fifa world cup|kind:generic", "2026 FIFA World Cup", Enumerable.Range(1, 36).Select(i => i == 36 ? "558954" : $"fifa-{i}").ToArray(), Enumerable.Range(1, 36).Select(i => $"fifa-c{i}").ToArray(), 36, "Verified"),
            new VerifiedMultiOutcomeGroupConfig(true, "winner:2026 peruvian presidential election|kind:person", "2026 Peruvian Presidential Election", ["peru-1", "peru-2"], ["peru-c1", "peru-c2"], 2, "Verified")
        };
        var resolved = new[]
        {
            new ResolvedVerifiedGroup(configured[0].GroupKey, configured[0].Title!, configured[0].MarketIds, configured[0].ConditionIds, configured[0].MarketIds.Select(Market).ToArray(), [], [], "VerifiedGroupResolved", "VerifiedGroupResolved"),
            new ResolvedVerifiedGroup(configured[1].GroupKey, configured[1].Title!, configured[1].MarketIds, configured[1].ConditionIds, [], ["peru-1", "peru-2"], [], "Rejected", "VerifiedGroupMarketMismatch")
        };

        var report = new AllowlistRepairService().BuildReport(configured, resolved, FifaPricing(), [PeruCandidate()]);
        var export = new AllowlistRepairService().BuildPatchPreview(report, configured);

        Assert.Equal(1, export.PatchPreview.Summary.PatchableHighConfidence);
    }

    [Fact]
    public void Fifa_patch_removes_missing_no_ask_market_and_matching_condition()
    {
        var configured = FifaConfigured();
        var report = new AllowlistRepairService().BuildReport(configured, FifaResolved(), FifaPricing(), []);
        var export = new AllowlistRepairService().BuildPatchPreview(report, configured);
        var fifa = export.PatchPreview.Patches.Single(x => x.GroupKey.Contains("fifa", StringComparison.OrdinalIgnoreCase));
        var validation = AllowlistRepairService.ValidatePatchItem(fifa);
        var marketIds = fifa.ProposedGroup!["marketIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();
        var conditionIds = fifa.ProposedGroup!["conditionIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

        Assert.DoesNotContain("558954", marketIds);
        Assert.DoesNotContain("fifa-c36", conditionIds);
        Assert.Equal(35, marketIds.Length);
        Assert.Equal(35, conditionIds.Length);
        Assert.Equal(35, fifa.ProposedGroup!["requiredOutcomeCount"]!.GetValue<int>());
        Assert.False(fifa.ProposedGroup!["requireExactOutcomeCount"]!.GetValue<bool>());
        Assert.Contains("Missing NO ask leg excluded", fifa.ProposedGroup!["settlementNotes"]!.GetValue<string>());
        Assert.Equal("PruneMissingNoAskLegs", fifa.ProposedGroup!["repairAction"]!.GetValue<string>());
        Assert.False(string.IsNullOrWhiteSpace(fifa.ProposedGroup!["repairSourceSnapshotId"]!.GetValue<string>()));
        Assert.True(validation.Valid);
        Assert.Equal(35, validation.FinalLegs);
        Assert.Contains("558954", validation.RemovedMarketIds);
    }


    [Fact]
    public void Patched_preview_validation_passes_with_11_total_and_unique_group_keys()
    {
        var configured = ExpandedConfiguredForPreview();
        var svc = new AllowlistRepairService();
        var report = svc.BuildReport(configured, ExpandedResolvedForPreview(configured), ExpandedPricingForPreview(), [PeruReplacementCandidate()], StableRepairOptions());
        var export = svc.BuildPatchPreview(report, configured);

        Assert.Equal(11, export.PatchPreview.PatchedPreviewValidation.TotalGroups);
        Assert.Equal(11, export.PatchPreview.PatchedPreviewValidation.UniqueGroupKeys);
        Assert.Equal(0, export.PatchPreview.PatchedPreviewValidation.DuplicateGroupKeys);
        Assert.True(export.PatchPreview.PatchedPreviewValidation.Valid, string.Join(";", export.PatchPreview.PatchedPreviewValidation.Reasons));
        Assert.Contains(export.PatchPreview.ManualApplyInstructions.GroupsToApply, x => x.Contains("fifa world cup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Patched_preview_validation_fails_on_duplicate_group_key()
    {
        var configured = ExpandedConfiguredForPreview();
        var svc = new AllowlistRepairService();
        var report = svc.BuildReport(configured, ExpandedResolvedForPreview(configured), ExpandedPricingForPreview(), [PeruReplacementCandidate()], StableRepairOptions());
        var export = svc.BuildPatchPreview(report, configured);
        var duplicated = export.PatchedPreviewConfig.AsArray().Select(x => JsonNode.Parse(x!.ToJsonString())).ToArray();
        duplicated = duplicated.Concat([JsonNode.Parse(duplicated[0]!.ToJsonString())]).ToArray();

        var validation = AllowlistRepairService.ValidatePatchedPreview(new JsonArray(duplicated), export.PatchPreview.Patches);

        Assert.False(validation.Valid);
        Assert.Equal(12, validation.TotalGroups);
        Assert.Equal(11, validation.UniqueGroupKeys);
        Assert.Equal(1, validation.DuplicateGroupKeys);
    }

    [Fact]
    public void Peru_patch_diff_is_reflected_in_patched_preview()
    {
        var configured = ExpandedConfiguredForPreview();
        var svc = new AllowlistRepairService();
        var report = svc.BuildReport(configured, ExpandedResolvedForPreview(configured), ExpandedPricingForPreview(), [PeruReplacementCandidate()], StableRepairOptions());
        var export = svc.BuildPatchPreview(report, configured);
        var peruPatch = export.PatchPreview.Patches.Single(x => x.GroupKey.Contains("peruvian", StringComparison.OrdinalIgnoreCase));
        var peruGroup = export.PatchedPreviewConfig.AsArray().OfType<JsonObject>().Single(x => x["groupKey"]!.GetValue<string>().Contains("peruvian", StringComparison.OrdinalIgnoreCase));
        var marketIds = peruGroup["marketIds"]!.AsArray().Select(x => x!.GetValue<string>()).ToArray();

        Assert.Equal("ReplaceGroup", peruPatch.PatchType);
        Assert.Contains("947269", peruPatch.Diff!["removedMarketIds"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.DoesNotContain("947269", marketIds);
    }

    [Fact]
    public void Low_confidence_review_only_groups_remain_unchanged_in_patched_preview()
    {
        var configured = ExpandedConfiguredForPreview();
        var svc = new AllowlistRepairService();
        var report = svc.BuildReport(configured, ExpandedResolvedForPreview(configured), ExpandedPricingForPreview(), [PeruReplacementCandidate()], StableRepairOptions());
        var export = svc.BuildPatchPreview(report, configured);
        var groups = export.PatchedPreviewConfig.AsArray().OfType<JsonObject>().ToArray();

        var womens = groups.Single(x => x["groupKey"]!.GetValue<string>().Contains("women s us open", StringComparison.OrdinalIgnoreCase));
        var firstRound = groups.Single(x => x["groupKey"]!.GetValue<string>().Contains("1st round", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("w-us-open-old", womens["marketIds"]!.AsArray().Single()!.GetValue<string>());
        Assert.Equal("round-old", firstRound["marketIds"]!.AsArray().Single()!.GetValue<string>());
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
    public void Repair_history_is_persisted_and_loaded_across_scanner_restarts()
    {
        var dir = Directory.CreateTempSubdirectory();
        var opts = new TradingBot.Options.AllowlistRepairOptions { RequiredStableRepairSnapshots = 2, UseLatestCandidateExportForRepair = true };
        var configuredRemove = PeruConfigured(["947269", "peru-2", "peru-3"]);
        WriteCandidateExport(dir.FullName, [PeruCandidateWithout947269()]);
        var firstSvc = new AllowlistRepairService();
        var firstReport = firstSvc.BuildReport(configuredRemove, PeruResolved(configuredRemove), [], [], opts, dir.FullName);
        firstSvc.ExportPatchPreview(Path.Combine(dir.FullName, "exports/verified-allowlist-repair-patch-preview-latest.json"), Path.Combine(dir.FullName, "exports/verified-multi-outcome-groups-patched-preview.json"), Path.Combine(dir.FullName, "exports/verified-multi-outcome-groups-patched-preview.with-metadata.json"), firstReport, configuredRemove, dir.FullName);

        var configuredAdd = PeruConfigured(["peru-2", "peru-3"]);
        WriteCandidateExport(dir.FullName, [PeruCandidateWith947269()], "\n ");
        var restartedSvc = new AllowlistRepairService();
        var secondReport = restartedSvc.BuildReport(configuredAdd, PeruResolved(configuredAdd), [], [], opts, dir.FullName);
        var second = restartedSvc.ExportPatchPreview(Path.Combine(dir.FullName, "exports/verified-allowlist-repair-patch-preview-latest.json"), Path.Combine(dir.FullName, "exports/verified-multi-outcome-groups-patched-preview.json"), Path.Combine(dir.FullName, "exports/verified-multi-outcome-groups-patched-preview.with-metadata.json"), secondReport, configuredAdd, dir.FullName).PatchPreview;

        Assert.Equal("ReviewOnly", second.Patches.Single().PatchType);
        Assert.Equal(1, second.Summary.Quarantined);
        Assert.True(File.Exists(Path.Combine(dir.FullName, "exports/allowlist-repair-history-latest.json")));
    }

    [Fact]
    public void Repair_history_detects_removed_then_added_market_as_oscillation()
    {
        var svc = new AllowlistRepairService();
        var dir = Directory.CreateTempSubdirectory();
        var opts = new TradingBot.Options.AllowlistRepairOptions { RequiredStableRepairSnapshots = 2, UseLatestCandidateExportForRepair = true };
        var configuredRemove = PeruConfigured(["947269", "peru-2", "peru-3"]);
        WriteCandidateExport(dir.FullName, [PeruCandidateWithout947269()]);
        var reportRemove = svc.BuildReport(configuredRemove, PeruResolved(configuredRemove), [], [], opts, dir.FullName);
        _ = svc.BuildPatchPreview(reportRemove, configuredRemove);
        var configuredAdd = PeruConfigured(["peru-2", "peru-3"]);
        WriteCandidateExport(dir.FullName, [PeruCandidateWith947269()], "\n ");
        var reportAdd = svc.BuildReport(configuredAdd, PeruResolved(configuredAdd), [], [], opts, dir.FullName);

        var preview = svc.BuildPatchPreview(reportAdd, configuredAdd).PatchPreview;
        var peru = preview.Patches.Single();
        var history = svc.BuildRepairHistoryExport().Groups.Single(x => x.GroupKey.Contains("peruvian", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("ReviewOnly", peru.PatchType);
        Assert.Equal("NeedsManualReview", peru.CurrentAction);
        Assert.Equal("Low", peru.Confidence);
        Assert.Contains(peru.RiskNotes, x => x.Contains("RepairDiffOscillation", StringComparison.OrdinalIgnoreCase));
        Assert.True(history.OscillationDetected);
        Assert.Contains("947269", history.AddedMarketIds.Concat(history.RemovedMarketIds));
    }

    [Fact]
    public void Oscillating_group_is_not_patchable_and_counted_as_quarantined()
    {
        var svc = new AllowlistRepairService();
        var dir = Directory.CreateTempSubdirectory();
        var opts = new TradingBot.Options.AllowlistRepairOptions { RequiredStableRepairSnapshots = 2, UseLatestCandidateExportForRepair = true };
        var configuredRemove = PeruConfigured(["947269", "peru-2", "peru-3"]);
        WriteCandidateExport(dir.FullName, [PeruCandidateWithout947269()]);
        _ = svc.BuildPatchPreview(svc.BuildReport(configuredRemove, PeruResolved(configuredRemove), [], [], opts, dir.FullName), configuredRemove);
        var configuredAdd = PeruConfigured(["peru-2", "peru-3"]);
        WriteCandidateExport(dir.FullName, [PeruCandidateWith947269()], "\n ");
        var preview = svc.BuildPatchPreview(svc.BuildReport(configuredAdd, PeruResolved(configuredAdd), [], [], opts, dir.FullName), configuredAdd).PatchPreview;

        Assert.Equal(0, preview.Summary.PatchableHighConfidence + preview.Summary.PatchableMediumConfidence);
        Assert.Equal(1, preview.Summary.Quarantined);
    }

    [Fact]
    public void Locked_group_is_never_patchable()
    {
        var svc = new AllowlistRepairService();
        var configured = PeruConfigured(["peru-old"]);
        var opts = new TradingBot.Options.AllowlistRepairOptions
        {
            RequiredStableRepairSnapshots = 1,
            UseLatestCandidateExportForRepair = false,
            LockedGroups = [new TradingBot.Options.AllowlistRepairLockedGroupOptions { GroupKey = configured[0].GroupKey, Reason = "manual lock", AllowPatchPreview = false }]
        };
        var report = svc.BuildReport(configured, PeruResolved(configured), [], [PeruCandidateWith947269()], opts);

        var patch = svc.BuildPatchPreview(report, configured).PatchPreview.Patches.Single();

        Assert.Equal("ReviewOnly", patch.PatchType);
        Assert.Equal("NeedsManualReview", patch.CurrentAction);
        Assert.Contains(patch.RiskNotes, x => x.Contains("manual lock", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, svc.BuildPatchPreview(report, configured).PatchPreview.Summary.Locked);
    }

    [Fact]
    public void Same_stable_diff_across_required_snapshots_becomes_patchable()
    {
        var svc = new AllowlistRepairService();
        var dir = Directory.CreateTempSubdirectory();
        var configured = PeruConfigured(["peru-old"]);
        var opts = new TradingBot.Options.AllowlistRepairOptions { RequiredStableRepairSnapshots = 2, UseLatestCandidateExportForRepair = true };
        WriteCandidateExport(dir.FullName, [PeruCandidateWith947269()]);
        var first = svc.BuildPatchPreview(svc.BuildReport(configured, PeruResolved(configured), [], [], opts, dir.FullName), configured).PatchPreview.Patches.Single();
        WriteCandidateExport(dir.FullName, [PeruCandidateWith947269()], "\n ");
        var second = svc.BuildPatchPreview(svc.BuildReport(configured, PeruResolved(configured), [], [], opts, dir.FullName), configured).PatchPreview.Patches.Single();

        Assert.Equal("ReviewOnly", first.PatchType);
        Assert.Equal("ReplaceGroup", second.PatchType);
    }

    [Fact]
    public void Repair_history_export_contains_peru_oscillation()
    {
        var svc = new AllowlistRepairService();
        var dir = Directory.CreateTempSubdirectory();
        var opts = new TradingBot.Options.AllowlistRepairOptions { RequiredStableRepairSnapshots = 2, UseLatestCandidateExportForRepair = true };
        var configuredRemove = PeruConfigured(["947269", "peru-2", "peru-3"]);
        WriteCandidateExport(dir.FullName, [PeruCandidateWithout947269()]);
        _ = svc.BuildPatchPreview(svc.BuildReport(configuredRemove, PeruResolved(configuredRemove), [], [], opts, dir.FullName), configuredRemove);
        var configuredAdd = PeruConfigured(["peru-2", "peru-3"]);
        WriteCandidateExport(dir.FullName, [PeruCandidateWith947269()], "\n ");
        _ = svc.BuildPatchPreview(svc.BuildReport(configuredAdd, PeruResolved(configuredAdd), [], [], opts, dir.FullName), configuredAdd);

        var history = svc.BuildRepairHistoryExport();
        var peru = history.Groups.Single(x => x.GroupKey.Contains("peruvian", StringComparison.OrdinalIgnoreCase));

        Assert.True(peru.OscillationDetected);
        Assert.False(peru.Patchable);
        Assert.Equal("NeedsManualReview", peru.RecommendedAction);
        Assert.Equal("RepairDiffOscillation", peru.QuarantineReason);
        Assert.Contains("947269", peru.OscillatingMarketIds);
        Assert.Contains("947269", peru.LastAddedMarketIds);
        Assert.Contains("947269", peru.PreviousRemovedMarketIds);
    }



    private static TradingBot.Options.AllowlistRepairOptions StableRepairOptions() => new() { DiagnosticsOnlyDuringSoak = false, RequiredStableRepairSnapshots = 1, UseLatestCandidateExportForRepair = false };

    private static IReadOnlyList<VerifiedMultiOutcomeGroupConfig> PeruConfigured(IReadOnlyList<string> marketIds) =>
    [
        new(true, "winner:2026 peruvian presidential election|kind:person", "2026 Peruvian Presidential Election", marketIds, marketIds.Select(x => "c-" + x).ToArray(), marketIds.Count, "Verified")
    ];

    private static IReadOnlyList<ResolvedVerifiedGroup> PeruResolved(IReadOnlyList<VerifiedMultiOutcomeGroupConfig> configured) =>
    [
        new(configured[0].GroupKey, configured[0].Title!, configured[0].MarketIds, configured[0].ConditionIds, [], configured[0].MarketIds, [], "Rejected", "VerifiedGroupMarketMismatch")
    ];

    private static JsonObject PeruCandidateWith947269() => new()
    {
        ["groupKey"] = "winner:2026 peruvian presidential election|kind:person",
        ["title"] = "Winner: 2026 Peruvian Presidential Election",
        ["markets"] = new JsonArray(
            MarketNode("947269", "c-947269", "Will Candidate A win the 2026 Peruvian presidential election?"),
            MarketNode("peru-2", "c-peru-2", "Will Candidate B win the 2026 Peruvian presidential election?"),
            MarketNode("peru-3", "c-peru-3", "Will Candidate C win the 2026 Peruvian presidential election?"))
    };

    private static JsonObject PeruCandidateWithout947269() => new()
    {
        ["groupKey"] = "winner:2026 peruvian presidential election|kind:person",
        ["title"] = "Winner: 2026 Peruvian Presidential Election",
        ["markets"] = new JsonArray(
            MarketNode("peru-2", "c-peru-2", "Will Candidate B win the 2026 Peruvian presidential election?"),
            MarketNode("peru-3", "c-peru-3", "Will Candidate C win the 2026 Peruvian presidential election?"))
    };

    private static IReadOnlyList<VerifiedMultiOutcomeGroupConfig> ExpandedConfiguredForPreview() =>
    [
        FifaConfigured()[0],
        new(true, "winner:2026 peruvian presidential election|kind:person", "2026 Peruvian Presidential Election", ["947269", "peru-2"], ["peru-old-c", "peru-c2"], 2, "Verified"),
        new(true, "winner:2026 women s us open|kind:generic", "2026 Women s US Open", ["w-us-open-old"], ["w-us-open-c"], 1, "Verified"),
        new(true, "winner:1st round of 2026 colombian presidential election|kind:person", "1st round Colombia", ["round-old"], ["round-c"], 1, "Verified"),
        ..Enumerable.Range(1, 7).Select(i => new VerifiedMultiOutcomeGroupConfig(true, $"winner:healthy-{i}|kind:generic", $"Healthy {i}", [$"healthy-{i}"], [$"healthy-c{i}"], 1, "Verified"))
    ];

    private static IReadOnlyList<ResolvedVerifiedGroup> ExpandedResolvedForPreview(IReadOnlyList<VerifiedMultiOutcomeGroupConfig> configured)
        => configured.Select(cfg => cfg.GroupKey switch
        {
            "winner:2026 fifa world cup|kind:generic" => FifaResolved()[0],
            "winner:2026 peruvian presidential election|kind:person" => new ResolvedVerifiedGroup(cfg.GroupKey, cfg.Title!, cfg.MarketIds, cfg.ConditionIds, [], ["947269"], [], "Rejected", "VerifiedGroupMarketMismatch"),
            "winner:2026 women s us open|kind:generic" => new ResolvedVerifiedGroup(cfg.GroupKey, cfg.Title!, cfg.MarketIds, cfg.ConditionIds, [], ["w-us-open-old"], [], "Rejected", "VerifiedGroupMarketMismatch"),
            "winner:1st round of 2026 colombian presidential election|kind:person" => new ResolvedVerifiedGroup(cfg.GroupKey, cfg.Title!, cfg.MarketIds, cfg.ConditionIds, [], ["round-old"], [], "Rejected", "VerifiedGroupMarketMismatch"),
            _ => new ResolvedVerifiedGroup(cfg.GroupKey, cfg.Title!, cfg.MarketIds, cfg.ConditionIds, cfg.MarketIds.Select(Market).ToArray(), [], [], "VerifiedGroupResolved", "VerifiedGroupResolved")
        }).ToArray();

    private static IReadOnlyList<object> ExpandedPricingForPreview()
        => FifaPricing().Concat(Enumerable.Range(1, 7).Select(i => (object)new
        {
            groupKey = $"winner:healthy-{i}|kind:generic",
            noAskResolvedCount = 1,
            missingNoAskCount = 0,
            pricedLegs = new[] { Leg($"healthy-{i}", $"healthy-c{i}") },
            missingPriceLegs = Array.Empty<object>()
        })).ToArray();

    private static JsonObject PeruReplacementCandidate() => new()
    {
        ["groupKey"] = "winner:2026 peruvian presidential election|kind:person",
        ["title"] = "Winner: 2026 Peruvian Presidential Election",
        ["markets"] = new JsonArray(
            MarketNode("peru-1", "peru-c1", "Will Candidate A win the 2026 Peruvian presidential election?"),
            MarketNode("peru-2", "peru-c2", "Will Candidate B win the 2026 Peruvian presidential election?"))
    };

    private static IReadOnlyList<VerifiedMultiOutcomeGroupConfig> FifaConfigured() =>
    [
        new(true, "winner:2026 fifa world cup|kind:generic", "2026 FIFA World Cup", Enumerable.Range(1, 36).Select(i => i == 36 ? "558954" : $"fifa-{i}").ToArray(), Enumerable.Range(1, 36).Select(i => $"fifa-c{i}").ToArray(), 36, "Verified")
    ];

    private static IReadOnlyList<ResolvedVerifiedGroup> FifaResolved() =>
    [
        new("winner:2026 fifa world cup|kind:generic", "2026 FIFA World Cup", FifaConfigured()[0].MarketIds, FifaConfigured()[0].ConditionIds, FifaConfigured()[0].MarketIds.Select(Market).ToArray(), [], [], "VerifiedGroupResolved", "VerifiedGroupResolved")
    ];

    private static IReadOnlyList<object> FifaPricing() =>
    [
        new
        {
            groupKey = "winner:2026 fifa world cup|kind:generic",
            noAskResolvedCount = 35,
            missingNoAskCount = 1,
            pricedLegs = Enumerable.Range(1, 35).Select(i => Leg($"fifa-{i}", $"fifa-c{i}")).ToArray(),
            missingPriceLegs = new[] { Leg("558954", "fifa-c36") }
        }
    ];

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
