using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services.MultiOutcome;

public sealed class AllowlistRepairService
{
    private readonly VerifiedAllowlistGroupHealthClassifier _classifier = new();
    private readonly Dictionary<string, ActionVersionState> _actionState = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private string? _lastSnapshotId;
    private AllowlistRepairReport? _lastReport;

    public AllowlistRepairReport BuildReport(
        IReadOnlyList<VerifiedMultiOutcomeGroupConfig> configuredGroups,
        IReadOnlyList<ResolvedVerifiedGroup> resolvedGroups,
        IReadOnlyList<object> verifiedPricingExport,
        IReadOnlyList<object> candidateGroups,
        AllowlistRepairOptions? options = null,
        string? contentRootPath = null)
    {
        options ??= new AllowlistRepairOptions();
        var candidateSnapshot = BuildCandidateSnapshot(candidateGroups, configuredGroups.Count, options, contentRootPath);
        lock (_gate)
        {
            if (_lastReport is not null && (_lastSnapshotId == candidateSnapshot.SnapshotId || candidateSnapshot.IsRollingFallback))
                return _lastReport;

            var resolvedByGroup = resolvedGroups.ToDictionary(x => x.GroupKey, StringComparer.OrdinalIgnoreCase);
            var pricingByGroup = verifiedPricingExport.Select(ToObject)
                .Where(x => x is not null && x.TryGetPropertyValue("groupKey", out var g) && !string.IsNullOrWhiteSpace(g?.GetValue<string>()))
                .ToDictionary(x => x!["groupKey"]!.GetValue<string>(), x => x!, StringComparer.OrdinalIgnoreCase);

            var now = DateTime.UtcNow;
            var rows = configuredGroups.Select(cfg => BuildGroup(
                candidateSnapshot.SnapshotId,
                now,
                cfg,
                resolvedByGroup.TryGetValue(cfg.GroupKey, out var resolved) ? resolved : null,
                pricingByGroup.TryGetValue(cfg.GroupKey, out var pricing) ? pricing : null,
                _classifier.Classify(cfg, resolvedByGroup.TryGetValue(cfg.GroupKey, out var r) ? r : null, pricingByGroup.TryGetValue(cfg.GroupKey, out var p) ? p : null, candidateSnapshot.Candidates, options))).ToArray();
            var summary = BuildSummary(rows, configuredGroups.Count);
            var categoryCounts = BuildCategoryCounts(rows, summary);
            var snapshot = new AllowlistRepairSnapshot(
                candidateSnapshot.SnapshotId,
                candidateSnapshot.CreatedAt,
                candidateSnapshot.DiscoveryId,
                candidateSnapshot.CandidateExportPath,
                candidateSnapshot.CandidateGroupsCount,
                configuredGroups.Count,
                candidateSnapshot.Source,
                rows);
            var report = new AllowlistRepairReport(
                candidateSnapshot.SnapshotId,
                now,
                summary,
                categoryCounts,
                summary.InvariantOk,
                snapshot,
                summary.ConfiguredGroups,
                summary.Healthy + summary.MonitoringOnly,
                summary.MonitoringOnly,
                summary.Broken,
                summary.NeedsRefresh,
                summary.NeedsPricingPrune,
                summary.BrokenConfig,
                summary.Disabled,
                summary.Ignored,
                rows,
                rows.Where(IsRepairable).Select(ToSuggestion).ToArray(),
                "Review repairSuggestions. Copy suggestedJson/suggestedTemplate into config/verified-multi-outcome-groups.json only after manual verification. This workflow never overwrites live config.");
            _lastSnapshotId = candidateSnapshot.SnapshotId;
            _lastReport = report;
            return report;
        }
    }

    public AllowlistRepairSuggestedConfig BuildSuggestedConfig(AllowlistRepairReport report)
        => new(
            report.SnapshotId,
            DateTime.UtcNow,
            DateTime.UtcNow,
            "Suggested only. Does not overwrite config/verified-multi-outcome-groups.json. Review manually before copying.",
            report.Summary,
            report.CategoryCounts,
            report.Groups.Select(g =>
            {
                var template = Clone(g.SuggestedPrunedTemplate ?? g.SuggestedRefreshedTemplate);
                return new AllowlistRepairSuggestedGroup(
                    g.GroupKey,
                    g.Title,
                    g.Enabled,
                    g.HealthCategory,
                    g.RecommendedAction,
                    g.RepairConfidence,
                    g.RecommendedAction,
                    SuggestedEnabled(g),
                    template,
                    Clone(g.SuggestedPrunedTemplate),
                    Clone(g.SuggestedRefreshedTemplate),
                    BuildDiff(g, template),
                    g.Notes,
                    g.CopyInstructions);
            }).ToArray());

    public (AllowlistRepairReport Report, AllowlistRepairSuggestedConfig SuggestedConfig) Export(
        string reportPath,
        string suggestedConfigPath,
        IReadOnlyList<VerifiedMultiOutcomeGroupConfig> configuredGroups,
        IReadOnlyList<ResolvedVerifiedGroup> resolvedGroups,
        IReadOnlyList<object> verifiedPricingExport,
        IReadOnlyList<object> candidateGroups,
        AllowlistRepairOptions? options = null,
        string? contentRootPath = null)
    {
        var report = BuildReport(configuredGroups, resolvedGroups, verifiedPricingExport, candidateGroups, options, contentRootPath);
        var suggested = BuildSuggestedConfig(report);
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(suggestedConfigPath)!);
        var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, json));
        File.WriteAllText(suggestedConfigPath, JsonSerializer.Serialize(suggested, json));
        return (report, suggested);
    }

    public AllowlistRepairPatchExport ExportPatchPreview(
        string patchPreviewPath,
        string patchedPreviewPath,
        string patchedPreviewMetadataPath,
        AllowlistRepairReport report,
        IReadOnlyList<VerifiedMultiOutcomeGroupConfig> configuredGroups,
        string? contentRootPath = null,
        string sourceConfigRelativePath = "config/verified-multi-outcome-groups.json")
    {
        var export = BuildPatchPreview(report, configuredGroups, contentRootPath, sourceConfigRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(patchPreviewPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(patchedPreviewPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(patchedPreviewMetadataPath)!);
        var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        File.WriteAllText(patchPreviewPath, JsonSerializer.Serialize(export.PatchPreview, json));
        File.WriteAllText(patchedPreviewPath, export.PatchedPreviewConfig.ToJsonString(json));
        if (export.PatchedPreviewWithMetadata is not null)
            File.WriteAllText(patchedPreviewMetadataPath, export.PatchedPreviewWithMetadata.ToJsonString(json));
        return export;
    }

    public AllowlistRepairPatchExport BuildPatchPreview(
        AllowlistRepairReport report,
        IReadOnlyList<VerifiedMultiOutcomeGroupConfig> configuredGroups,
        string? contentRootPath = null,
        string sourceConfigRelativePath = "config/verified-multi-outcome-groups.json")
    {
        var now = DateTime.UtcNow;
        var sourceFullPath = !string.IsNullOrWhiteSpace(contentRootPath)
            ? Path.Combine(contentRootPath, sourceConfigRelativePath.Replace('/', Path.DirectorySeparatorChar))
            : string.Empty;
        var originalConfig = ReadSourceConfig(sourceFullPath, configuredGroups);
        var byKey = originalConfig.OfType<JsonObject>()
            .Where(x => !string.IsNullOrWhiteSpace(x["groupKey"]?.GetValue<string>()))
            .ToDictionary(x => x["groupKey"]!.GetValue<string>(), x => x, StringComparer.OrdinalIgnoreCase);

        var patches = report.Groups
            .Where(IsPatchPreviewCandidate)
            .Select(g => BuildPatchItem(report.SnapshotId, g, byKey.TryGetValue(g.GroupKey, out var current) ? current : ConfigToJson(configuredGroups.FirstOrDefault(x => x.GroupKey.Equals(g.GroupKey, StringComparison.OrdinalIgnoreCase)))))
            .ToArray();
        var patchable = patches.Where(IsPatchablePatch).ToArray();
        var patchedByKey = patchable.Where(x => x.ProposedGroup is not null).ToDictionary(x => x.GroupKey, x => x.ProposedGroup!, StringComparer.OrdinalIgnoreCase);
        var patchedConfig = new JsonArray(originalConfig.Select(x => patchedByKey.TryGetValue((x as JsonObject)?["groupKey"]?.GetValue<string>() ?? string.Empty, out var replacement) ? Clone(replacement) : Clone(x)).ToArray());
        var reviewOnlyGroups = patches.Where(x => !IsPatchablePatch(x)).Select(x => x.GroupKey).ToArray();
        var groupsExpectedResolved = patchable.Select(x => x.GroupKey).ToArray();
        var expectedHealthy = report.Summary.Healthy + report.Summary.MonitoringOnly + patchable.Length;
        var summary = new AllowlistRepairPatchSummary(
            report.ConfiguredGroups,
            patchable.Count(x => x.Confidence.Equals("High", StringComparison.OrdinalIgnoreCase)),
            patchable.Count(x => x.Confidence.Equals("Medium", StringComparison.OrdinalIgnoreCase)),
            patches.Count(x => x.Confidence.Equals("Low", StringComparison.OrdinalIgnoreCase) && !IsPatchablePatch(x)),
            patches.Count(x => x.CurrentAction.Equals(nameof(AllowlistRepairRecommendedAction.DisableMissingMarkets), StringComparison.OrdinalIgnoreCase)),
            expectedHealthy);
        var plan = new AllowlistRepairPostApplyValidationPlan(
            ["Manually review exports/verified-allowlist-repair-patch-preview-latest.json.", "Copy the reviewed contents from exports/verified-multi-outcome-groups-patched-preview.json into config/verified-multi-outcome-groups.json.", "Restart the bot or call the safe allowlist reload endpoint if enabled.", "Re-run the scanner and inspect allowlist health/repair logs."],
            report.ConfiguredGroups,
            Math.Min(report.ConfiguredGroups, report.Healthy + patchable.Length),
            Math.Max(0, report.CategoryCounts.HasMissingNoAsk - patchable.Count(x => x.PatchType == "PruneGroup")),
            groupsExpectedResolved,
            reviewOnlyGroups,
            ["GET /api/bot/verified-allowlist-health", "GET /api/bot/verified-allowlist-repair-report", "GET /api/bot/allowlist-repair-patch-preview", "Check logs for [ALLOWLIST_HEALTH] and [ALLOWLIST_REPAIR_REPORT]."],
            ["Colombian presidential election should no longer show MissingNoAsk=1.", "Peru should no longer show VerifiedGroupMarketMismatch if refreshed candidate is correct.", "NBA Finals should no longer show VerifiedGroupMarketMismatch if refreshed candidate is correct.", "Women’s US Open remains BrokenConfig/ReviewOnly unless manually handled.", "1st round Colombian remains NeedsManualReview."]);
        var preview = new AllowlistRepairPatchPreview(now, report.SnapshotId, sourceConfigRelativePath, "ManualPreviewOnly", false, summary, patches, plan);
        var wrapped = new JsonObject
        {
            ["metadata"] = new JsonObject
            {
                ["timestamp"] = now.ToString("O"),
                ["snapshotId"] = report.SnapshotId,
                ["mode"] = "ManualPreviewOnly",
                ["willOverwriteRealConfig"] = false,
                ["sourceConfigPath"] = sourceConfigRelativePath,
                ["note"] = "Preview only. Manually copy after review; this file is not loaded automatically."
            },
            ["groups"] = Clone(patchedConfig)
        };
        return new AllowlistRepairPatchExport(preview, patchedConfig, wrapped);
    }


    public static AllowlistPatchValidationResult ValidatePatchItem(AllowlistRepairPatchItem patch)
    {
        var proposed = patch.ProposedGroup as JsonObject;
        var marketIds = ReadStringArray(proposed, "marketIds");
        var conditionIds = ReadStringArray(proposed, "conditionIds");
        var removed = patch.Diff?["removedMarketIds"] is JsonArray rm
            ? rm.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray()
            : [];
        var required = GetNullableInt(proposed, "requiredOutcomeCount");
        var exact = GetNullableBool(proposed, "requireExactOutcomeCount");
        var notes = proposed?["settlementNotes"]?.GetValue<string>() ?? string.Empty;
        var repairAction = proposed?["repairAction"]?.GetValue<string>() ?? string.Empty;
        var snapshot = proposed?["repairSourceSnapshotId"]?.GetValue<string>() ?? string.Empty;
        var valid = patch.PatchType == "PruneGroup"
            && proposed is not null
            && marketIds.Count == conditionIds.Count
            && required == marketIds.Count
            && exact == false
            && removed.All(x => !marketIds.Contains(x, StringComparer.OrdinalIgnoreCase))
            && notes.Contains(VerifiedAllowlistGroupHealthClassifier.PruneSettlementNote, StringComparison.OrdinalIgnoreCase)
            && repairAction == nameof(AllowlistRepairRecommendedAction.PruneMissingNoAskLegs)
            && !string.IsNullOrWhiteSpace(snapshot);
        return new AllowlistPatchValidationResult(patch.GroupKey, removed, valid, marketIds.Count);
    }

    private static bool IsPatchPreviewCandidate(AllowlistRepairGroup g)
        => g.RecommendedAction is nameof(AllowlistRepairRecommendedAction.PruneMissingNoAskLegs)
            or nameof(AllowlistRepairRecommendedAction.RefreshFromCandidateExport)
            or nameof(AllowlistRepairRecommendedAction.DisableMissingMarkets)
            or nameof(AllowlistRepairRecommendedAction.NeedsManualReview);

    private static bool IsPatchablePatch(AllowlistRepairPatchItem p)
        => (p.PatchType is "ReplaceGroup" or "PruneGroup" or "DisableGroup")
            && (p.Confidence is "High" or "Medium")
            && HasRealPatchDiff(p);

    private static bool HasRealPatchDiff(AllowlistRepairPatchItem p)
        => CountArray(p.Diff, "addedMarketIds") > 0
            || CountArray(p.Diff, "removedMarketIds") > 0
            || CountArray(p.Diff, "changedConditionIds") > 0
            || ChangedValue(p.Diff, "requiredOutcomeCountBefore", "requiredOutcomeCountAfter")
            || ChangedValue(p.Diff, "enabledBefore", "enabledAfter");

    private static int CountArray(JsonNode? node, string property)
        => node?[property] is JsonArray arr ? arr.Count : 0;

    private static bool ChangedValue(JsonNode? node, string beforeProperty, string afterProperty)
    {
        var before = node?[beforeProperty]?.ToJsonString();
        var after = node?[afterProperty]?.ToJsonString();
        return !string.Equals(before, after, StringComparison.OrdinalIgnoreCase);
    }

    private static AllowlistRepairPatchItem BuildPatchItem(string snapshotId, AllowlistRepairGroup g, JsonObject? currentGroup)
    {
        var patchableConfidence = g.RepairConfidence is "High" or "Medium";
        var patchType = (g.RecommendedAction, patchableConfidence) switch
        {
            (nameof(AllowlistRepairRecommendedAction.PruneMissingNoAskLegs), true) => "PruneGroup",
            (nameof(AllowlistRepairRecommendedAction.RefreshFromCandidateExport), true) => "ReplaceGroup",
            (nameof(AllowlistRepairRecommendedAction.DisableMissingMarkets), true) => "DisableGroup",
            _ => "ReviewOnly"
        };
        var proposed = patchType switch
        {
            "PruneGroup" => MergeManualMetadata(currentGroup, g.SuggestedPrunedTemplate as JsonObject, snapshotId, g.RecommendedAction),
            "ReplaceGroup" => MergeManualMetadata(currentGroup, g.SuggestedRefreshedTemplate as JsonObject, snapshotId, g.RecommendedAction),
            "DisableGroup" => DisabledProposal(currentGroup, snapshotId, g.RecommendedAction),
            _ => null
        };
        if (g.RecommendedAction == nameof(AllowlistRepairRecommendedAction.DisableMissingMarkets) && g.RepairConfidence.Equals("Low", StringComparison.OrdinalIgnoreCase))
            proposed = null;
        var diff = BuildPatchDiff(g, currentGroup, proposed);
        if (patchType == "ReplaceGroup" && !HasRealPatchDiff(new AllowlistRepairPatchItem(g.GroupKey, g.RecommendedAction, g.RepairConfidence, patchType, null, proposed, diff, [], string.Empty, string.Empty)))
        {
            patchType = "None";
            proposed = null;
            diff = BuildPatchDiff(g, currentGroup, proposed);
        }
        var risks = BuildRiskNotes(g, patchType).ToArray();
        var manualInstructions = patchType is "ReviewOnly" or "None"
            ? "No trusted automatic config change. Review diagnostics manually; do not copy an executable group from this preview."
            : "Review currentGroup, proposedGroup, and diff. If acceptable, manually copy the matching group from exports/verified-multi-outcome-groups-patched-preview.json into config/verified-multi-outcome-groups.json, then restart/reload allowlist.";
        return new AllowlistRepairPatchItem(g.GroupKey, g.RecommendedAction, g.RepairConfidence, patchType, Clone(currentGroup), Clone(proposed), diff, risks, manualInstructions, g.ExpectedResultAfterManualApply);
    }

    private static IEnumerable<string> BuildRiskNotes(AllowlistRepairGroup g, string patchType)
    {
        yield return "Preview only; this workflow never overwrites config/verified-multi-outcome-groups.json.";
        if (g.RepairConfidence.Equals("Low", StringComparison.OrdinalIgnoreCase)) yield return "Low confidence. Review before disabling or refreshing.";
        if (patchType == "ReplaceGroup") yield return "Refresh replaces marketIds/conditionIds from the candidate export; verify settlement remains mutually exclusive.";
        if (patchType == "PruneGroup") yield return VerifiedAllowlistGroupHealthClassifier.PruneSettlementNote;
        if (g.GroupKey.Contains("women s us open", StringComparison.OrdinalIgnoreCase)) yield return "Low confidence. Review before disabling or refreshing.";
    }

    private static JsonObject? MergeManualMetadata(JsonObject? currentGroup, JsonObject? proposedTemplate, string snapshotId, string action)
    {
        if (proposedTemplate is null) return null;
        var proposed = (JsonObject)Clone(proposedTemplate)!;
        foreach (var key in new[] { "verifiedBy", "verificationStatus", "settlementNotes", "groupType", "allowedStrategy" })
        {
            if (currentGroup is not null && currentGroup.TryGetPropertyValue(key, out var value) && value is not null)
                proposed[key] = Clone(value);
        }
        if (action == nameof(AllowlistRepairRecommendedAction.PruneMissingNoAskLegs))
        {
            var note = proposed["settlementNotes"]?.GetValue<string>() ?? string.Empty;
            if (!note.Contains(VerifiedAllowlistGroupHealthClassifier.PruneSettlementNote, StringComparison.OrdinalIgnoreCase))
                proposed["settlementNotes"] = string.IsNullOrWhiteSpace(note) ? VerifiedAllowlistGroupHealthClassifier.PruneSettlementNote : note + " " + VerifiedAllowlistGroupHealthClassifier.PruneSettlementNote;
            proposed["requiredOutcomeCount"] = (proposed["marketIds"] as JsonArray)?.Count ?? proposed["requiredOutcomeCount"]?.GetValue<int>() ?? 0;
            proposed["requireExactOutcomeCount"] = false;
            proposed["enabled"] = true;
        }
        proposed["repairSourceSnapshotId"] = snapshotId;
        proposed["repairAction"] = action;
        return proposed;
    }

    private static JsonObject? DisabledProposal(JsonObject? currentGroup, string snapshotId, string action)
    {
        if (currentGroup is null) return null;
        var proposed = (JsonObject)Clone(currentGroup)!;
        proposed["enabled"] = false;
        proposed["repairSourceSnapshotId"] = snapshotId;
        proposed["repairAction"] = action;
        return proposed;
    }

    private static JsonObject? BuildPatchDiff(AllowlistRepairGroup g, JsonObject? currentGroup, JsonObject? proposed)
    {
        var currentMarketIds = ReadStringArray(currentGroup, "marketIds");
        var proposedMarketIds = ReadStringArray(proposed, "marketIds");
        var currentConditionIds = ReadStringArray(currentGroup, "conditionIds");
        var proposedConditionIds = ReadStringArray(proposed, "conditionIds");
        return new JsonObject
        {
            ["addedMarketIds"] = ToArray(proposedMarketIds.Except(currentMarketIds, StringComparer.OrdinalIgnoreCase)),
            ["removedMarketIds"] = ToArray(currentMarketIds.Except(proposedMarketIds, StringComparer.OrdinalIgnoreCase).Concat(g.RepairMatch?.RemovedMarketIds ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase)),
            ["changedConditionIds"] = ToArray(SymmetricExcept(currentConditionIds, proposedConditionIds)),
            ["missingNoAskMarketIds"] = ToArray(g.MissingNoAskMarketIds),
            ["requiredOutcomeCountBefore"] = GetNullableInt(currentGroup, "requiredOutcomeCount"),
            ["requiredOutcomeCountAfter"] = GetNullableInt(proposed, "requiredOutcomeCount"),
            ["enabledBefore"] = GetNullableBool(currentGroup, "enabled"),
            ["enabledAfter"] = GetNullableBool(proposed, "enabled")
        };
    }

    private static JsonArray ReadSourceConfig(string sourceFullPath, IReadOnlyList<VerifiedMultiOutcomeGroupConfig> configuredGroups)
    {
        if (!string.IsNullOrWhiteSpace(sourceFullPath) && File.Exists(sourceFullPath))
        {
            try
            {
                var node = JsonNode.Parse(File.ReadAllText(sourceFullPath));
                if (node is JsonArray arr) return new JsonArray(arr.Select(Clone).ToArray());
                if (node is JsonObject obj && obj["groups"] is JsonArray groups) return new JsonArray(groups.Select(Clone).ToArray());
            }
            catch
            {
                // Fall through to in-memory config.
            }
        }
        return new JsonArray(configuredGroups.Select(x => ConfigToJson(x)).ToArray());
    }

    private static JsonObject? ConfigToJson(VerifiedMultiOutcomeGroupConfig? cfg)
    {
        if (cfg is null) return null;
        return new JsonObject
        {
            ["enabled"] = cfg.Enabled,
            ["groupKey"] = cfg.GroupKey,
            ["title"] = cfg.Title,
            ["verificationStatus"] = cfg.VerificationStatus,
            ["groupType"] = "MutuallyExclusiveWinner",
            ["allowedStrategy"] = "BUY_ALL_NO_MUTUALLY_EXCLUSIVE",
            ["marketIds"] = ToArray(cfg.MarketIds),
            ["conditionIds"] = ToArray(cfg.ConditionIds),
            ["requiredOutcomeCount"] = cfg.RequiredOutcomeCount,
            ["requireExactOutcomeCount"] = false
        };
    }

    private static IReadOnlyList<string> ReadStringArray(JsonObject? obj, string property)
        => (obj?[property] as JsonArray)?.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];

    private static IEnumerable<string> SymmetricExcept(IReadOnlyList<string> left, IReadOnlyList<string> right)
        => left.Except(right, StringComparer.OrdinalIgnoreCase).Concat(right.Except(left, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase);

    private static int? GetNullableInt(JsonObject? obj, string property)
        => obj?[property] is JsonNode n && n.GetValueKind() == JsonValueKind.Number ? n.GetValue<int>() : null;

    private static bool? GetNullableBool(JsonObject? obj, string property)
        => obj?[property] is JsonNode n && n.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? n.GetValue<bool>() : null;

    private AllowlistRepairGroup BuildGroup(string snapshotId, DateTime now, VerifiedMultiOutcomeGroupConfig cfg, ResolvedVerifiedGroup? resolved, JsonObject? pricing, AllowlistRepairClassification classification)
    {
        var noAskResolved = GetInt(pricing, "noAskResolvedCount");
        var missingNoAsk = GetInt(pricing, "missingNoAskCount");
        var resolvedOk = resolved?.ValidationStatus == "VerifiedGroupResolved";
        var evaluated = pricing is not null;
        var status = classification.HealthCategory switch
        {
            nameof(AllowlistRepairHealthCategory.Healthy) => "Healthy",
            nameof(AllowlistRepairHealthCategory.MonitoringOnly) => "MonitoringOnly",
            nameof(AllowlistRepairHealthCategory.Disabled) => "Disabled",
            nameof(AllowlistRepairHealthCategory.Ignored) => "Ignored",
            _ => "NeedsRepair"
        };
        var notes = NotesFor(classification).ToArray();
        var version = VersionAction(cfg.GroupKey, classification.RecommendedAction, snapshotId, now, classification.ConsecutiveMatchMisses);
        return new AllowlistRepairGroup(
            snapshotId,
            version.ActionVersion,
            version.PreviousAction,
            classification.RecommendedAction,
            version.ActionChangedAt,
            version.ReasonForChange,
            cfg.GroupKey,
            cfg.Title ?? cfg.GroupKey,
            cfg.Enabled,
            status,
            classification.HealthCategory,
            resolvedOk,
            evaluated,
            cfg.MarketIds.Count,
            resolved?.ResolvedMarkets.Count ?? 0,
            classification.MissingMarketIds.Count,
            classification.MissingMarketIds,
            noAskResolved,
            missingNoAsk,
            classification.MissingNoAskMarketIds,
            resolvedOk ? null : resolved?.RejectionReason ?? "ResolverMissingConfiguredGroup",
            missingNoAsk > 0 ? "MissingNoAsk" : null,
            classification.RecommendedAction,
            classification.RepairConfidence,
            classification.Reason,
            Clone(classification.SuggestedPrunedTemplate),
            Clone(classification.SuggestedRefreshedTemplate),
            classification.RepairMatch,
            classification.ConsecutiveMatchMisses,
            notes,
            ExpectedResult(classification),
            "Copy suggestedTemplate into config/verified-multi-outcome-groups.json only after manual review. This export does not modify live config.");
    }

    private ActionVersionState VersionAction(string groupKey, string currentAction, string snapshotId, DateTime now, int consecutiveSnapshotMisses)
    {
        if (!_actionState.TryGetValue(groupKey, out var previous))
        {
            var created = new ActionVersionState(1, null, currentAction, now, "InitialSnapshot", snapshotId, consecutiveSnapshotMisses);
            _actionState[groupKey] = created;
            return created;
        }

        if (previous.CurrentAction.Equals(currentAction, StringComparison.OrdinalIgnoreCase))
        {
            var same = previous with { SnapshotId = snapshotId, ConsecutiveSnapshotMisses = consecutiveSnapshotMisses };
            _actionState[groupKey] = same;
            return same;
        }

        var reason = consecutiveSnapshotMisses > 0 ? "NoMatchAcrossSnapshots" : "RepairSnapshotReclassified";
        var changed = new ActionVersionState(previous.ActionVersion + 1, previous.CurrentAction, currentAction, now, reason, snapshotId, consecutiveSnapshotMisses);
        _actionState[groupKey] = changed;
        return changed;
    }

    private static AllowlistRepairSummary BuildSummary(IReadOnlyList<AllowlistRepairGroup> rows, int configured)
    {
        var healthy = rows.Count(x => x.HealthCategory == nameof(AllowlistRepairHealthCategory.Healthy));
        var monitoring = rows.Count(x => x.HealthCategory == nameof(AllowlistRepairHealthCategory.MonitoringOnly));
        var prune = rows.Count(x => x.HealthCategory == nameof(AllowlistRepairHealthCategory.NeedsPricingPrune));
        var refresh = rows.Count(x => x.HealthCategory == nameof(AllowlistRepairHealthCategory.NeedsRefresh));
        var brokenConfig = rows.Count(x => x.HealthCategory == nameof(AllowlistRepairHealthCategory.BrokenConfig));
        var disabled = rows.Count(x => x.HealthCategory == nameof(AllowlistRepairHealthCategory.Disabled));
        var ignored = rows.Count(x => x.HealthCategory == nameof(AllowlistRepairHealthCategory.Ignored));
        var invariant = configured == healthy + monitoring + prune + refresh + brokenConfig + disabled + ignored;
        return new AllowlistRepairSummary(configured, healthy, monitoring, prune, refresh, brokenConfig, disabled, ignored, prune + refresh + brokenConfig, invariant);
    }

    private static AllowlistRepairCategoryCounts BuildCategoryCounts(IReadOnlyList<AllowlistRepairGroup> rows, AllowlistRepairSummary summary)
        => new(
            summary.Healthy,
            summary.MonitoringOnly,
            summary.NeedsPricingPrune,
            summary.NeedsRefresh,
            summary.BrokenConfig,
            summary.Disabled,
            summary.Ignored,
            summary.Broken,
            rows.Count(x => x.MissingNoAsk > 0),
            rows.Count(x => !string.IsNullOrWhiteSpace(x.MismatchReason)),
            rows.Count(x => x.RepairMatch is not null),
            rows.Count(x => x.SuggestedPrunedTemplate is not null || x.SuggestedRefreshedTemplate is not null));

    private static IEnumerable<string> NotesFor(AllowlistRepairClassification c)
    {
        if (c.HealthCategory == nameof(AllowlistRepairHealthCategory.NeedsPricingPrune)) yield return VerifiedAllowlistGroupHealthClassifier.PruneSettlementNote;
        if (c.RepairMatch is not null) yield return $"Stable candidate match: {c.RepairMatch.CandidateGroupKey} score={c.RepairMatch.Score:0.###}.";
        if (c.ConsecutiveMatchMisses > 0) yield return $"Candidate match missing for {c.ConsecutiveMatchMisses} repair snapshot(s); hysteresis prevents immediate downgrade.";
        yield return c.Reason;
    }

    private static string ExpectedResult(AllowlistRepairClassification c) => c.RecommendedAction switch
    {
        nameof(AllowlistRepairRecommendedAction.PruneMissingNoAskLegs) => "Configured group keeps only priced mutually exclusive legs for manual review; not automatically executable.",
        nameof(AllowlistRepairRecommendedAction.RefreshFromCandidateExport) => "Configured market/condition ids are refreshed from a stable candidate after manual review.",
        nameof(AllowlistRepairRecommendedAction.DisableMissingMarkets) => "Group remains in suggested config but disabled until markets can be manually repaired.",
        nameof(AllowlistRepairRecommendedAction.NeedsManualReview) => "No automatic template is trusted; operator reviews candidate exports and current config.",
        _ => "No repair action required."
    };

    private static bool SuggestedEnabled(AllowlistRepairGroup g) => g.Enabled && g.RecommendedAction is not (
        nameof(AllowlistRepairRecommendedAction.DisableMissingMarkets) or
        nameof(AllowlistRepairRecommendedAction.NeedsManualReview) or
        nameof(AllowlistRepairRecommendedAction.RemoveFromAllowlist));

    private static bool IsRepairable(AllowlistRepairGroup g) => g.RecommendedAction is
        nameof(AllowlistRepairRecommendedAction.PruneMissingNoAskLegs) or
        nameof(AllowlistRepairRecommendedAction.RefreshFromCandidateExport) or
        nameof(AllowlistRepairRecommendedAction.DisableMissingMarkets) or
        nameof(AllowlistRepairRecommendedAction.NeedsManualReview) or
        nameof(AllowlistRepairRecommendedAction.DisableUntilBetterPricing) or
        nameof(AllowlistRepairRecommendedAction.RemoveFromAllowlist);

    private static AllowlistRepairSuggestion ToSuggestion(AllowlistRepairGroup g)
        => new(g.GroupKey, g.RecommendedAction, g.RepairConfidence, Clone(g.SuggestedPrunedTemplate ?? g.SuggestedRefreshedTemplate), g.ExpectedResultAfterManualApply, g.CopyInstructions);

    private static JsonObject? BuildDiff(AllowlistRepairGroup group, JsonNode? template)
    {
        if (template is null) return null;
        var suggestedMarketIds = (template["marketIds"] as JsonArray)?.Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray() ?? [];
        return new JsonObject
        {
            ["suggestedMarketCount"] = suggestedMarketIds.Length,
            ["removedMarketIds"] = ToArray(group.MissingMarketIds.Concat(group.MissingNoAskMarketIds).Distinct(StringComparer.OrdinalIgnoreCase)),
            ["addedMarketIds"] = group.RepairMatch is null ? new JsonArray() : ToArray(group.RepairMatch.AddedMarketIds),
            ["changedConditionIds"] = new JsonArray()
        };
    }

    private static CandidateSnapshot BuildCandidateSnapshot(IReadOnlyList<object> candidateGroups, int configuredCount, AllowlistRepairOptions options, string? contentRootPath)
    {
        var exportPath = !string.IsNullOrWhiteSpace(contentRootPath) ? Path.Combine(contentRootPath, "exports", "multi-outcome-candidates-latest.json") : string.Empty;
        if (options.UseLatestCandidateExportForRepair && !string.IsNullOrWhiteSpace(exportPath) && File.Exists(exportPath))
        {
            try
            {
                var text = File.ReadAllText(exportPath);
                var node = JsonNode.Parse(text) as JsonArray;
                var candidates = (node ?? []).OfType<JsonObject>().Select(x => (JsonObject)Clone(x)!).ToArray();
                var info = new FileInfo(exportPath);
                var hash = StableHash(text);
                var snapshotId = $"candidate-export-{info.LastWriteTimeUtc.Ticks:x}-{hash[..12]}";
                return new CandidateSnapshot(snapshotId, info.LastWriteTimeUtc, snapshotId, exportPath, candidates.Length, "CandidateExportSnapshot", false, candidates);
            }
            catch
            {
                // Fall back to the supplied candidate set when the export is unreadable.
            }
        }

        var fallback = candidateGroups.Select(ToObject).Where(x => x is not null).Cast<JsonObject>().ToArray();
        var fingerprint = StableHash(string.Join("|", fallback.Select(x => x["groupKey"]?.GetValue<string>() ?? x.ToJsonString())));
        return new CandidateSnapshot($"in-memory-candidate-snapshot-{fingerprint[..12]}", DateTime.UtcNow, $"in-memory-{fingerprint[..12]}", exportPath, fallback.Length, "CandidateExportSnapshot", true, fallback);
    }

    internal static JsonObject? ToObject(object value)
    {
        if (value is JsonObject obj) return (JsonObject?)Clone(obj);
        return JsonSerializer.SerializeToNode(value) as JsonObject;
    }

    internal static int GetInt(JsonObject? o, string name) => o is not null && o.TryGetPropertyValue(name, out var n) && n is not null && n.GetValueKind() == JsonValueKind.Number ? n.GetValue<int>() : 0;
    internal static IReadOnlyList<string> ReadMarketIds(JsonObject? o, string name) => ((o?[name] as JsonArray) ?? []).Select(x => x?["marketId"]?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    internal static JsonArray ToArray(IEnumerable<string> values) { var arr = new JsonArray(); foreach (var v in values) arr.Add(v); return arr; }
    internal static JsonNode? Clone(JsonNode? node) => node is null ? null : JsonNode.Parse(node.ToJsonString());
    private static string StableHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record CandidateSnapshot(string SnapshotId, DateTime CreatedAt, string DiscoveryId, string CandidateExportPath, int CandidateGroupsCount, string Source, bool IsRollingFallback, IReadOnlyList<JsonObject> Candidates);
    private sealed record ActionVersionState(int ActionVersion, string? PreviousAction, string CurrentAction, DateTime ActionChangedAt, string ReasonForChange, string SnapshotId, int ConsecutiveSnapshotMisses);
}

public sealed class VerifiedAllowlistGroupHealthClassifier
{
    public const string PruneSettlementNote = "Pruned to priced mutually exclusive subset. Missing NO ask leg excluded.";
    private readonly Dictionary<string, CachedRepairMatch> _cache = new(StringComparer.OrdinalIgnoreCase);

    public AllowlistRepairClassification Classify(
        VerifiedMultiOutcomeGroupConfig cfg,
        ResolvedVerifiedGroup? resolved,
        JsonObject? pricing,
        IReadOnlyList<JsonObject> candidates,
        AllowlistRepairOptions options)
    {
        var missingMarketIds = (resolved?.MissingMarketIds ?? cfg.MarketIds).Concat(resolved?.MissingConditionIds ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var noAskResolved = AllowlistRepairService.GetInt(pricing, "noAskResolvedCount");
        var missingNoAsk = AllowlistRepairService.GetInt(pricing, "missingNoAskCount");
        var missingNoAskIds = AllowlistRepairService.ReadMarketIds(pricing, "missingPriceLegs");
        var resolvedOk = resolved?.ValidationStatus == "VerifiedGroupResolved";

        if (!cfg.Enabled)
            return Result(cfg, AllowlistRepairHealthCategory.Disabled, AllowlistRepairRecommendedAction.KeepMonitoring, "High", "Group disabled in current allowlist.", missingMarketIds, missingNoAskIds);

        if (resolvedOk && missingNoAsk > 0 && noAskResolved >= 2)
            return Result(cfg, AllowlistRepairHealthCategory.NeedsPricingPrune, AllowlistRepairRecommendedAction.PruneMissingNoAskLegs, missingNoAsk == 1 ? "High" : "Medium", "Resolved but one or more NO asks are unavailable.", missingMarketIds, missingNoAskIds, BuildPrunedTemplate(cfg, pricing, noAskResolved));

        if (resolvedOk && pricing is not null)
            return Result(cfg, AllowlistRepairHealthCategory.MonitoringOnly, AllowlistRepairRecommendedAction.KeepMonitoring, "High", "Resolved and evaluated; monitor pricing/execution status.", missingMarketIds, missingNoAskIds);

        if (!resolvedOk)
        {
            var match = FindStableMatch(cfg, resolved, candidates, options);
            if (match.Match is not null)
            {
                var template = BuildRefreshTemplate(cfg, match.Match.Candidate);
                if (template is not null)
                {
                    if (IsNoOpRefresh(cfg, template, match.Match.Diagnostics))
                        return Result(cfg, AllowlistRepairHealthCategory.MonitoringOnly, AllowlistRepairRecommendedAction.KeepMonitoring, match.Match.Diagnostics.Confidence, "Stable candidate export matches current group; no repair patch is required.", missingMarketIds, missingNoAskIds, repairMatch: match.Match.Diagnostics, misses: match.ConsecutiveMisses);
                    return Result(cfg, AllowlistRepairHealthCategory.NeedsRefresh, AllowlistRepairRecommendedAction.RefreshFromCandidateExport, match.Match.Diagnostics.Confidence, "Market mismatch; stable refreshed candidate is available for manual review.", missingMarketIds, missingNoAskIds, refreshed: template, repairMatch: match.Match.Diagnostics, misses: match.ConsecutiveMisses);
                }
            }

            var forceDisable = IsWomenUsOpen(cfg.GroupKey) && (resolved?.ResolvedMarkets.Count ?? 0) < 2;
            var action = forceDisable ? AllowlistRepairRecommendedAction.DisableMissingMarkets : AllowlistRepairRecommendedAction.NeedsManualReview;
            var category = forceDisable ? AllowlistRepairHealthCategory.BrokenConfig : AllowlistRepairHealthCategory.NeedsRefresh;
            return Result(cfg, category, action, "Low", match.ConsecutiveMisses > 0 ? $"No stable candidate match yet. ConsecutiveSnapshotMisses={match.ConsecutiveMisses}." : "No stable candidate match found in repair snapshot; manual review required.", missingMarketIds, missingNoAskIds, misses: match.ConsecutiveMisses);
        }

        return Result(cfg, AllowlistRepairHealthCategory.Healthy, AllowlistRepairRecommendedAction.Keep, "High", "Group healthy.", missingMarketIds, missingNoAskIds);
    }

    private StableMatch FindStableMatch(VerifiedMultiOutcomeGroupConfig cfg, ResolvedVerifiedGroup? resolved, IReadOnlyList<JsonObject> candidates, AllowlistRepairOptions options)
    {
        var scored = candidates.Select(c => ScoreCandidate(cfg, resolved, c)).Where(x => x.Diagnostics.Score >= options.MinRefreshMatchScore).OrderByDescending(x => x.Diagnostics.Score).FirstOrDefault();
        if (scored.Candidate is not null)
        {
            _cache[cfg.GroupKey] = new CachedRepairMatch(scored.Candidate, scored.Diagnostics, 0);
            return new StableMatch(new CachedRepairMatch(scored.Candidate, scored.Diagnostics, 0), 0);
        }

        if (_cache.TryGetValue(cfg.GroupKey, out var cached))
        {
            var misses = cached.ConsecutiveMisses + 1;
            if (options.PreferStableCachedMatches && misses < options.MatchFailureDowngradeCycles)
            {
                _cache[cfg.GroupKey] = cached with { ConsecutiveMisses = misses };
                return new StableMatch(cached with { ConsecutiveMisses = misses }, misses);
            }
            _cache.Remove(cfg.GroupKey);
            return new StableMatch(null, misses);
        }

        return new StableMatch(null, 0);
    }

    private static (JsonObject? Candidate, AllowlistRepairMatch Diagnostics) ScoreCandidate(VerifiedMultiOutcomeGroupConfig cfg, ResolvedVerifiedGroup? resolved, JsonObject candidate)
    {
        var candidateKey = candidate["groupKey"]?.GetValue<string>() ?? string.Empty;
        var title = candidate["title"]?.GetValue<string>() ?? candidateKey;
        var hay = Normalize(candidateKey + " " + title + " " + string.Join(" ", ((candidate["markets"] as JsonArray) ?? []).Select(m => m?["question"]?.GetValue<string>() ?? string.Empty)));
        var wanted = Normalize(cfg.GroupKey + " " + cfg.Title);
        var titleSimilarity = TokenSimilarity(wanted, hay);
        if (Normalize(candidateKey) == Normalize(cfg.GroupKey)) titleSimilarity = 1m;
        var semantic = SemanticScore(cfg.GroupKey, hay);
        var marketIds = ReadCandidateArray(candidate, "marketId");
        var conditionIds = ReadCandidateArray(candidate, "conditionId");
        var marketOverlap = Overlap(cfg.MarketIds, marketIds);
        var conditionOverlap = Overlap(cfg.ConditionIds, conditionIds);
        var pricedLegs = marketIds.Length;
        var score = Math.Clamp((titleSimilarity * 0.55m) + (semantic * 0.30m) + (marketOverlap * 0.10m) + (conditionOverlap * 0.05m), 0m, 1m);
        if (marketIds.Length < 2) score = 0m;
        var added = marketIds.Except(cfg.MarketIds, StringComparer.OrdinalIgnoreCase).ToArray();
        var removed = cfg.MarketIds.Except(marketIds, StringComparer.OrdinalIgnoreCase).Concat(resolved?.MissingMarketIds ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var changedConditionIds = cfg.ConditionIds.Count == 0 && conditionIds.Length == 0
            ? 0
            : cfg.ConditionIds.Except(conditionIds, StringComparer.OrdinalIgnoreCase).Concat(conditionIds.Except(cfg.ConditionIds, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var confidence = score >= 0.85m ? "High" : score >= 0.70m ? "Medium" : "Low";
        return (candidate, new AllowlistRepairMatch(candidateKey, score, titleSimilarity, marketOverlap, conditionOverlap, semantic, pricedLegs, 0, added, removed, changedConditionIds, confidence));
    }

    private static bool IsNoOpRefresh(VerifiedMultiOutcomeGroupConfig cfg, JsonObject template, AllowlistRepairMatch diagnostics)
    {
        var marketIds = ((template["marketIds"] as JsonArray) ?? []).Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
        var conditionIds = ((template["conditionIds"] as JsonArray) ?? []).Select(x => x?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToArray();
        var marketsSame = cfg.MarketIds.Count == marketIds.Length && cfg.MarketIds.SequenceEqual(marketIds, StringComparer.OrdinalIgnoreCase);
        var conditionsSame = cfg.ConditionIds.Count == conditionIds.Length && cfg.ConditionIds.SequenceEqual(conditionIds, StringComparer.OrdinalIgnoreCase);
        var requiredSame = !cfg.RequiredOutcomeCount.HasValue || cfg.RequiredOutcomeCount.Value == marketIds.Length;
        return diagnostics.AddedMarketIds.Count == 0
            && diagnostics.RemovedMarketIds.Count == 0
            && diagnostics.ChangedConditionIds == 0
            && marketsSame
            && conditionsSame
            && requiredSame;
    }

    private static JsonObject? BuildPrunedTemplate(VerifiedMultiOutcomeGroupConfig cfg, JsonObject? pricing, int noAskResolved)
    {
        var priced = pricing?["pricedLegs"] as JsonArray;
        if (priced is null || noAskResolved < 2) return null;
        var marketIds = priced.Select(x => x?["marketId"]?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var conditionIds = priced.Select(x => x?["conditionId"]?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new JsonObject
        {
            ["enabled"] = true,
            ["groupKey"] = cfg.GroupKey,
            ["title"] = cfg.Title ?? cfg.GroupKey,
            ["verificationStatus"] = "Verified",
            ["groupType"] = "MutuallyExclusiveWinner",
            ["allowedStrategy"] = "BUY_ALL_NO_MUTUALLY_EXCLUSIVE",
            ["marketIds"] = AllowlistRepairService.ToArray(marketIds),
            ["conditionIds"] = AllowlistRepairService.ToArray(conditionIds),
            ["requiredOutcomeCount"] = marketIds.Length,
            ["requireExactOutcomeCount"] = false,
            ["settlementNotes"] = PruneSettlementNote,
            ["verifiedBy"] = "manual-review-required",
            ["verifiedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd")
        };
    }

    private static JsonObject? BuildRefreshTemplate(VerifiedMultiOutcomeGroupConfig cfg, JsonObject candidate)
    {
        var markets = candidate["markets"] as JsonArray;
        if (markets is null) return null;
        var active = markets.Where(m => GetBool(m, "active") != false && GetBool(m, "closed") != true && GetBool(m, "archived") != true).ToArray();
        if (active.Length < 2) return null;
        var marketIds = active.Select(x => x?["marketId"]?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var conditionIds = active.Select(x => x?["conditionId"]?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (marketIds.Length < 2) return null;
        return new JsonObject
        {
            ["enabled"] = true,
            ["groupKey"] = cfg.GroupKey,
            ["title"] = cfg.Title ?? candidate["title"]?.GetValue<string>() ?? cfg.GroupKey,
            ["verificationStatus"] = "Verified",
            ["groupType"] = "MutuallyExclusiveWinner",
            ["allowedStrategy"] = "BUY_ALL_NO_MUTUALLY_EXCLUSIVE",
            ["marketIds"] = AllowlistRepairService.ToArray(marketIds),
            ["conditionIds"] = AllowlistRepairService.ToArray(conditionIds),
            ["requiredOutcomeCount"] = marketIds.Length,
            ["requireExactOutcomeCount"] = false,
            ["settlementNotes"] = "Refreshed from candidate export. Manually verify mutually exclusive settlement before enabling.",
            ["verifiedBy"] = "manual-review-required",
            ["verifiedAt"] = DateTime.UtcNow.ToString("yyyy-MM-dd")
        };
    }

    private static AllowlistRepairClassification Result(VerifiedMultiOutcomeGroupConfig cfg, AllowlistRepairHealthCategory health, AllowlistRepairRecommendedAction action, string confidence, string reason, IReadOnlyList<string> missingMarkets, IReadOnlyList<string> missingNoAsk, JsonNode? pruned = null, JsonNode? refreshed = null, AllowlistRepairMatch? repairMatch = null, int misses = 0)
        => new(cfg.GroupKey, health.ToString(), action.ToString(), confidence, reason, missingMarkets, missingNoAsk, AllowlistRepairService.Clone(pruned), AllowlistRepairService.Clone(refreshed), repairMatch, misses);

    private static string[] ReadCandidateArray(JsonObject c, string property) => ((c["markets"] as JsonArray) ?? []).Select(x => x?[property]?.GetValue<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    private static decimal Overlap(IReadOnlyList<string> expected, IReadOnlyList<string> actual) => expected.Count == 0 ? 0m : expected.Intersect(actual, StringComparer.OrdinalIgnoreCase).Count() / (decimal)expected.Count;
    private static decimal TokenSimilarity(string a, string b) { var aa = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase); var bb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase); return aa.Count == 0 ? 0m : aa.Intersect(bb, StringComparer.OrdinalIgnoreCase).Count() / (decimal)aa.Count; }
    private static decimal SemanticScore(string groupKey, string candidateHaystack) { var n = Normalize(groupKey); if (n.Contains("nba finals")) return candidateHaystack.Contains("nba finals") && candidateHaystack.Contains("2026") ? 1m : 0m; if (n.Contains("peruvian presidential")) return (candidateHaystack.Contains("peru") || candidateHaystack.Contains("peruvian")) && candidateHaystack.Contains("presidential") ? 1m : 0m; if (n.Contains("women s us open")) return candidateHaystack.Contains("women") && candidateHaystack.Contains("us open") ? 1m : 0m; return candidateHaystack.Contains("winner") || candidateHaystack.Contains(" win ") ? 0.5m : 0m; }
    private static bool IsWomenUsOpen(string groupKey) => Normalize(groupKey).Contains("women s us open");
    private static string Normalize(string s) => Regex.Replace(s.ToLowerInvariant().Replace("’", " ").Replace("'", " "), @"[^a-z0-9]+", " ").Trim();
    private static bool? GetBool(JsonNode? node, string name) => node is JsonObject o && o.TryGetPropertyValue(name, out var n) && n is not null && n.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? n.GetValue<bool>() : null;

    private sealed record StableMatch(CachedRepairMatch? Match, int ConsecutiveMisses);
    private sealed record CachedRepairMatch(JsonObject Candidate, AllowlistRepairMatch Diagnostics, int ConsecutiveMisses);
}
