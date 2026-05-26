using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class MultiOutcomeCandidateExportServiceTests
{
    [Fact]
    public void Relative_path_resolves_with_content_root_and_creates_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "tb_export_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var svc = new MultiOutcomeCandidateExportService(new MultiOutcomeReviewOptions
        {
            ExportCandidates = true,
            ExportPath = "exports/multi-outcome-candidates-latest.json",
            ExportIntervalMinutes = 5
        }, root);
        var group = new MultiOutcomeGroupArbEngine.CandidateGroupReview("g", "t", "generic", 1, "Candidate", "AutoCandidateUnverified", 0.8m, 0.2m, 0.2m, 1m, new[] { "w" }, new[] { new Market { id = "m1", conditionId = "c1", question = "q" } });
        svc.ExportIfDue(new[] { group });
        Assert.True(File.Exists(Path.Combine(root, "exports", "multi-outcome-candidates-latest.json")));
    }

    [Fact]
    public void Export_overwrites_latest_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "tb_export_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "exports", "multi-outcome-candidates-latest.json");
        var svc = new MultiOutcomeCandidateExportService(new MultiOutcomeReviewOptions { ExportCandidates = true, ExportPath = "exports/multi-outcome-candidates-latest.json", ExportIntervalMinutes = 0 }, root);
        var g1 = new MultiOutcomeGroupArbEngine.CandidateGroupReview("g1", "t1", "generic", 1, "Candidate", "AutoCandidateUnverified", 0.8m, 0.2m, 0.2m, 1m, new[] { "w" }, new[] { new Market { id = "m1", conditionId = "c1", question = "q1" } });
        var g2 = new MultiOutcomeGroupArbEngine.CandidateGroupReview("g2", "t2", "generic", 1, "Candidate", "AutoCandidateUnverified", 0.9m, 0.1m, 0.1m, 1m, new[] { "w" }, new[] { new Market { id = "m2", conditionId = "c2", question = "q2" } });
        svc.ExportIfDue(new[] { g1 });
        svc.ExportIfDue(new[] { g2 });
        var txt = File.ReadAllText(path);
        Assert.Contains("g2", txt);
        Assert.DoesNotContain("g1", txt);
    }
}
