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
    public void Repair_report_export_is_created_when_broken_exists()
    {
        var svc = new AllowlistRepairService();
        var dir = Directory.CreateTempSubdirectory();
        var reportPath = Path.Combine(dir.FullName, "verified-allowlist-repair-report-latest.json");
        var suggestedPath = Path.Combine(dir.FullName, "verified-multi-outcome-groups-repair-suggested.json");

        svc.Export(reportPath, suggestedPath, Configured(), ResolvedWithColombianPricingProblem(), Pricing(), []);

        Assert.True(File.Exists(reportPath));
        Assert.True(File.Exists(suggestedPath));
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

    private static JsonObject MarketNode(string id, string conditionId, string question) => new() { ["marketId"] = id, ["conditionId"] = conditionId, ["question"] = question, ["active"] = true, ["closed"] = false, ["archived"] = false };

    private static Market Market(string id) => new() { id = id, question = "Will candidate win?", active = true, closed = false, archived = false, conditionId = "c-" + id, outcomes = ["Yes", "No"], clobTokenIds = ["yes-" + id, "no-" + id] };
}
