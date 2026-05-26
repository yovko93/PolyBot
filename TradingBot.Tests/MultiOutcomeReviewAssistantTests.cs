using System.Text.Json;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class MultiOutcomeReviewAssistantTests
{
    private static MultiOutcomeGroupArbEngine.CandidateGroupReview G(string key, string title, string kind, int count, params string[] qs)
    {
        var markets = qs.Select((q, i) => new Market { id = $"m{i}", conditionId = $"c{i}", question = q, active = true, closed = false }).ToArray();
        return new(key, title, kind, count, "Candidate", "AutoCandidateUnverified", 0.95m, 0.05m, 0.01m, 1m, new[] { "w" }, markets);
    }

    [Fact] public void Winner_group_gets_positive_score() { var svc = new MultiOutcomeCandidateExportService(new(), Path.GetTempPath()); var r = svc.BuildReviewReport(new[] { G("winner:2026 nba finals", "winner", "winner", 5, "Will X win finals?") }); var n=JsonSerializer.SerializeToNode(r[0])!.AsObject(); Assert.True(n["candidateQualityScore"]!.GetValue<int>() > 0); }
    [Fact] public void Spread_group_is_do_not_verify() { var svc = new MultiOutcomeCandidateExportService(new(), Path.GetTempPath()); var r = svc.BuildReviewReport(new[] { G("colon-event:spread", "spread", "generic", 5, "spread") }); var n=JsonSerializer.SerializeToNode(r[0])!.AsObject(); Assert.Equal("DoNotVerify", n["recommendedAction"]!.GetValue<string>()); }
    [Fact] public void Over_under_group_is_do_not_verify() { var svc = new MultiOutcomeCandidateExportService(new(), Path.GetTempPath()); var r = svc.BuildReviewReport(new[] { G("colon-event:o/u", "knicks vs cavaliers o/u", "generic", 5, "o/u") }); var n=JsonSerializer.SerializeToNode(r[0])!.AsObject(); Assert.Equal("DoNotVerify", n["recommendedAction"]!.GetValue<string>()); }
    [Fact] public void Independent_matches_are_false_positive() { var svc = new MultiOutcomeCandidateExportService(new(), Path.GetTempPath()); var r = svc.BuildReviewReport(new[] { G("g", "t20", "generic", 5, "A vs B", "C vs D") }); var n=JsonSerializer.SerializeToNode(r[0])!.AsObject(); Assert.Equal("LikelyFalsePositive", n["recommendedAction"]!.GetValue<string>()); }
    [Fact] public void Safe_candidate_has_template() { var svc = new MultiOutcomeCandidateExportService(new(), Path.GetTempPath()); var r = svc.BuildReviewReport(new[] { G("winner:2026 nhl stanley cup", "winner", "winner", 5, "winner") }); var n=JsonSerializer.SerializeToNode(r[0])!.AsObject(); Assert.Equal("SafeCandidateForManualVerification", n["recommendedAction"]!.GetValue<string>()); Assert.NotNull(n["suggestedAllowlistTemplate"]); }
    [Fact] public void Already_allowlisted_marked() { var root=Path.Combine(Path.GetTempPath(),"tb_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Path.Combine(root,"config")); File.WriteAllText(Path.Combine(root,"config","verified-multi-outcome-groups.json"),"[{\"groupKey\":\"winner:2026 fifa world cup\"}]"); var svc = new MultiOutcomeCandidateExportService(new(), root); var r = svc.BuildReviewReport(new[] { G("winner:2026 fifa world cup", "winner", "winner", 5, "winner") }); var n=JsonSerializer.SerializeToNode(r[0])!.AsObject(); Assert.Equal("AlreadyVerified", n["recommendedAction"]!.GetValue<string>()); }
    [Fact] public void Report_export_created() { var root=Path.Combine(Path.GetTempPath(),"tb_"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); var svc = new MultiOutcomeCandidateExportService(new MultiOutcomeReviewOptions{ExportCandidates=true,ExportIntervalMinutes=0}, root); svc.ExportIfDue(new[] { G("winner:2026 nba finals", "winner", "winner", 5, "winner") }); Assert.True(File.Exists(Path.Combine(root,"exports","multi-outcome-review-report-latest.json"))); }
    [Fact] public void Review_report_bounded() { var svc = new MultiOutcomeCandidateExportService(new(), Path.GetTempPath()); var rows = Enumerable.Range(0, 30).Select(i => G($"winner:{i}","winner","winner",5,"winner")).ToArray(); var r = svc.BuildReviewReport(rows); Assert.True(r.Count <= 30); }
    [Fact] public void Verified_ranking_logging_defaults_throttled() { var o = new MultiOutcomeLoggingOptions(); Assert.Equal(10, o.LogVerifiedBasketRankingEveryNCycles); Assert.True(o.LogVerifiedBasketOnlyOnChangeRanking); Assert.False(o.LogScanConfigEachCycle); }
}
