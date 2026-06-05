using TradingBot.Services;
using TradingBot.Services.MultiOutcome;
using Xunit;

namespace TradingBot.Tests;

public class QuietLogGateTests
{
    private static QuietLogPolicy Policy(int every = 100, int max = 3) => new(true, every, 10, true, max, false);

    [Fact]
    public void QuietLogGate_allows_first_event()
    {
        var gate = new QuietLogGate();
        Assert.True(gate.ShouldLog(new LogEventKey("multi", "MULTI_CANDIDATE_SCAN"), new LogEventFingerprint("hash"), LogImportance.Normal, Policy()));
    }

    [Fact]
    public void QuietLogGate_suppresses_same_hash()
    {
        var gate = new QuietLogGate();
        var key = new LogEventKey("multi", "MULTI_CANDIDATE_SCAN");
        var fp = new LogEventFingerprint("hash", "bucket");

        Assert.True(gate.ShouldLog(key, fp, LogImportance.Normal, Policy()));
        Assert.False(gate.ShouldLog(key, fp, LogImportance.Normal, Policy()));
        Assert.Equal(1, gate.Snapshot().QuietSuppressedTotal);
    }

    [Fact]
    public void QuietLogGate_allows_material_change()
    {
        var gate = new QuietLogGate();
        var key = new LogEventKey("multi", "MULTI_CANDIDATE_SCAN");

        Assert.True(gate.ShouldLog(key, new LogEventFingerprint("stable-1", "bucket-1"), LogImportance.Normal, Policy()));
        Assert.True(gate.ShouldLog(key, new LogEventFingerprint("stable-2", "bucket-2"), LogImportance.Normal, Policy()));
    }

    [Fact]
    public void QuietLogGate_allows_critical_always()
    {
        var gate = new QuietLogGate();
        var key = new LogEventKey("execution", "ORDER_ATTEMPTED");
        var fp = new LogEventFingerprint("same");

        Assert.True(gate.ShouldLog(key, fp, LogImportance.Critical, Policy(max: 1)));
        Assert.True(gate.ShouldLog(key, fp, LogImportance.Critical, Policy(max: 1)));
    }

    [Fact]
    public void MultiVerifiedScan_identical_fingerprint_is_suppressed()
    {
        var gate = new QuietLogGate();
        var key = new LogEventKey("multi-verified", "MULTI_VERIFIED_SCAN");
        var fp = new LogEventFingerprint("Configured=11|Resolved=8|Unresolved=3|ActiveExecutable=0|PaperOpened=0|BestActiveNet=-0.0040|groups=a,b,c", "same");

        Assert.True(gate.ShouldLog(key, fp, LogImportance.Normal, Policy(max: 5)));
        Assert.False(gate.ShouldLog(key, fp, LogImportance.Normal, Policy(max: 5)));
    }

    [Fact]
    public void Repair_suggestion_same_snapshot_hash_is_suppressed()
    {
        var gate = new QuietLogGate();
        var key = new LogEventKey("allowlist-repair", "ALLOWLIST_REPAIR_SUGGESTION", GroupKey: "group");
        var fp = new LogEventFingerprint("snapshot-1|group|action|confidence|added|removed|locked:false|quarantined:false", "group|action|confidence|added|removed");

        Assert.True(gate.ShouldLog(key, fp, LogImportance.Normal, Policy(max: 10)));
        Assert.False(gate.ShouldLog(key, fp, LogImportance.Normal, Policy(max: 10)));
    }

    [Fact]
    public void Womens_us_open_flip_flop_logs_once_then_suppresses()
    {
        var gate = new QuietLogGate();
        var group = "winner:2026 women s us open|kind:generic";
        var key = new LogEventKey("allowlist-repair", "ALLOWLIST_REPAIR_UNSTABLE_SUPPRESSED", GroupKey: group);
        var fp = new LogEventFingerprint(ScanLogSummaryService.RepairActionDirectionFingerprint(group, "RefreshFromCandidateExport", "NeedsManualReview"));

        Assert.True(ScanLogSummaryService.IsWomenUsOpenRepairFlipFlop(group, "RefreshFromCandidateExport", "NeedsManualReview", "RepairSnapshotReclassified"));
        Assert.True(gate.ShouldLog(key, fp, LogImportance.Normal, Policy(max: 10)));
        Assert.False(gate.ShouldLog(key, fp, LogImportance.Normal, Policy(max: 10)));
    }

    [Fact]
    public void Same_market_reason_data_quality_reject_is_suppressed_by_hash()
    {
        var gate = new QuietLogGate();
        var key = new LogEventKey("single-market", "SINGLE_MARKET_DATA_QUALITY_REJECTED", MarketId: "m1");
        var fp = new LogEventFingerprint("m1|SuspiciousYesNoAskSum", "SuspiciousYesNoAskSum");

        Assert.True(gate.ShouldLog(key, fp, LogImportance.Important, Policy(max: 1)));
        Assert.False(gate.ShouldLog(key, fp, LogImportance.Important, Policy(max: 1)));
    }

    [Fact]
    public void Sixty_minute_simulated_soak_stays_within_per_category_log_caps()
    {
        var gate = new QuietLogGate();
        var policy = Policy(max: 5);
        for (var i = 0; i < 1_000; i++)
        {
            gate.ShouldLog(new LogEventKey("multi-candidate", "MULTI_CANDIDATE_SCAN"), new LogEventFingerprint("same", "same"), LogImportance.Normal, policy);
            gate.ShouldLog(new LogEventKey("multi-verified", "MULTI_VERIFIED_SCAN"), new LogEventFingerprint("same", "same"), LogImportance.Normal, policy);
        }

        var stats = gate.Snapshot();
        Assert.True(stats.EmittedByCategory["multi-candidate"] <= 5);
        Assert.True(stats.EmittedByCategory["multi-verified"] <= 5);
        Assert.True(stats.QuietSuppressedTotal > 0);
    }
}
