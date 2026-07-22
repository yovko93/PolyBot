using System.Text.Json;
using TradingBot.Api;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record PaperPhase1PositiveCapture(
    string CaptureId, string ProcessRunId, long ScannerCycle, string CandidateId, string MarketId, string Question,
    string YesTokenId, string NoTokenId, decimal YesAsk, decimal NoAsk, decimal SumAsk, decimal RawEdge,
    decimal AfterCostEdge, decimal AfterSafetyEdge, decimal MinEdge, decimal DistanceToMinEdge,
    decimal ExpectedProfit, decimal ExpectedPayout, decimal Quantity, decimal Notional,
    DateTime YesOrderbookSnapshotTimestamp, DateTime NoOrderbookSnapshotTimestamp, double OrderbookAgeSeconds,
    bool TokenMappingVerified, bool HasBothBooks, bool HasYesAsk, bool HasNoAsk, bool SuspiciousYesNoAskSum,
    bool StaleOrderbook, bool OrderbookStableNow, bool ReducedUniverseOrderbookStableNow, bool MarketQuarantined,
    bool TokenQuarantined, bool EdgeStable, bool DepthSufficient, bool FillPassed, bool RiskPassed,
    bool PaperDiagnosticsLimitedEligible, bool DuplicatePosition, bool OpenPositionsLimitPassed,
    bool ExposureLimitPassed, bool OpensPerHourLimitPassed, bool PaperEligible, string FirstBlockingReason,
    IReadOnlyList<string> AllBlockingReasons, bool WouldOpenIfAllGatesPassed, bool ActualOpenAttempted,
    bool ActualOpened, string OpenedPositionId, bool LiveTradingDisabled = true, bool SigningDisabled = true);

public sealed record PaperPhase1PositiveCaptureState(bool Enabled, decimal Threshold, int CapturesTotal,
    int CapturesAboveMinEdge, int CapturesPaperEligible, int CapturesOpened, decimal? BestAfterSafetyEdge,
    string BestCandidateId, string BestFirstBlockingReason, bool Consistent, bool ExportWritten, string LastWriteError,
    IReadOnlyList<PaperPhase1PositiveCapture> TopCaptures);

/// <summary>Append-only diagnostic evidence. It has no dependency on, and cannot call, the paper engine.</summary>
public static class PaperPhase1PositiveCaptureService
{
    private static readonly object Sync = new();
    private static readonly List<PaperPhase1PositiveCapture> Buffer = [];
    private static TradingBotOptions? _options;
    private static string _root = Directory.GetCurrentDirectory();
    private const int Capacity = 500;
    public static PaperPhase1PositiveCaptureState Current { get; private set; } = Empty();

    public static void Configure(TradingBotOptions options, string root)
    {
        lock (Sync) { _options = options; _root = root; Buffer.Clear(); Current = Empty(); WriteLatest(); }
    }

    public static void Observe(BinaryOrderBookSnapshot book, SingleMarketOpportunityAuditDto audit, long cycle)
    {
        lock (Sync)
        {
            if (_options is null || audit.AfterSafetyEdge < 0m || !IsProfile()) return;
            var min = _options.PaperDiagnosticsLimited.MinEdgeOverride;
            var reasons = Reasons(audit, min);
            var candidateId = $"SingleMarketBuyBoth:{book.MarketId}:{cycle}";
            var captureId = $"{ProcessRunContext.ProcessRunId}:{candidateId}";
            var now = DateTime.UtcNow;
            var eligible = reasons.Count == 0;
            var c = new PaperPhase1PositiveCapture(captureId, ProcessRunContext.ProcessRunId, cycle, candidateId,
                book.MarketId, book.Question, book.YesTokenId, book.NoTokenId, audit.YesAsk, audit.NoAsk,
                audit.YesAsk + audit.NoAsk, audit.RawEdge, audit.AfterCostEdge, audit.AfterSafetyEdge, min,
                audit.AfterSafetyEdge - min, audit.AfterSafetyEdge * audit.ExecutableQty, audit.ExecutableQty,
                audit.ExecutableQty, audit.NotionalAtCap, book.TimestampUtc, book.TimestampUtc,
                book.TimestampUtc == default ? 0 : Math.Max(0, (now - book.TimestampUtc).TotalSeconds),
                !string.IsNullOrWhiteSpace(book.YesTokenId) && !string.IsNullOrWhiteSpace(book.NoTokenId),
                book.YesAsk is not null && book.NoAsk is not null, book.YesAsk is not null, book.NoAsk is not null,
                audit.YesAsk + audit.NoAsk <= 0m || audit.YesAsk + audit.NoAsk > 2m, audit.RejectedReason.Contains("Stale", StringComparison.OrdinalIgnoreCase),
                !audit.RejectedReason.Contains("Orderbook", StringComparison.OrdinalIgnoreCase), true, false, false,
                audit.RejectedReason != "EdgeNotStable", audit.DepthPassed, audit.FillPassed, audit.RiskPassed,
                audit.PaperDiagnosticsLimitedGatePassed, audit.RejectedReason.Contains("Duplicate", StringComparison.OrdinalIgnoreCase),
                !audit.RejectedReason.Contains("MaxOpen", StringComparison.OrdinalIgnoreCase), !audit.RejectedReason.Contains("Exposure", StringComparison.OrdinalIgnoreCase),
                !audit.RejectedReason.Contains("OpensPerHour", StringComparison.OrdinalIgnoreCase), eligible,
                reasons.FirstOrDefault() ?? "None", reasons, eligible, false, false, "None");
            var index = Buffer.FindIndex(x => x.CaptureId == captureId);
            if (index >= 0) Buffer[index] = c; else Buffer.Add(c);
            Append(c);
            if (Buffer.Count > Capacity) Buffer.RemoveRange(0, Buffer.Count - Capacity);
            Refresh();
            Console.WriteLine($"[PAPER_PHASE1_POSITIVE_CAPTURED] CandidateId={c.CandidateId} MarketId={c.MarketId} AfterSafetyEdge={c.AfterSafetyEdge:0.####} MinEdge={min:0.####} PaperEligible={c.PaperEligible.ToString().ToLowerInvariant()} FirstBlockingReason={c.FirstBlockingReason} ActualOpenAttempted=false ActualOpened=false ProcessRunId={c.ProcessRunId}");
        }
    }

    public static void MarkOpen(string candidateId, bool attempted, bool opened, string positionId)
    {
        lock (Sync)
        {
            var i = Buffer.FindLastIndex(x => x.CandidateId == candidateId);
            if (i < 0) return;
            Buffer[i] = Buffer[i] with { ActualOpenAttempted = attempted, ActualOpened = opened, OpenedPositionId = opened ? positionId : "None" };
            Append(Buffer[i]);
            Refresh();
        }
    }

    public static bool Replay(string captureId)
    {
        lock (Sync)
        {
            var path = Path.Combine(_root, "exports/paper-phase1-positive-captures.jsonl");
            if (!File.Exists(path)) return false;
            var capture = File.ReadLines(path).Select(x => JsonSerializer.Deserialize<PaperPhase1PositiveCapture>(x, JsonOptions)).LastOrDefault(x => x?.CaptureId == captureId);
            if (capture is null) return false;
            var reasons = capture.AllBlockingReasons.ToArray(); // captured immutable gate inputs are the replay inputs
            var eligible = reasons.Length == 0;
            var payload = new { replayedAtUtc=DateTime.UtcNow, capture.CaptureId, sourceProcessRunId=capture.ProcessRunId,
                capture.CandidateId, capture.MarketId, capturedAfterSafetyEdge=capture.AfterSafetyEdge,
                capturedFirstBlockingReason=capture.FirstBlockingReason, replayPaperEligible=eligible,
                replayFirstBlockingReason=reasons.FirstOrDefault() ?? "None", replayAllBlockingReasons=reasons,
                wouldOpen=false, opened=false, reason="ReplayModeNeverOpens", consistentWithOriginal=eligible == capture.PaperEligible };
            Directory.CreateDirectory(Path.Combine(_root, "exports"));
            File.WriteAllText(Path.Combine(_root, "exports/paper-phase1-capture-replay-latest.json"), JsonSerializer.Serialize(payload, JsonOptions));
            Console.WriteLine($"[PAPER_PHASE1_CAPTURE_REPLAY] CaptureId={capture.CaptureId} CandidateId={capture.CandidateId} AfterSafetyEdge={capture.AfterSafetyEdge:0.####} ReplayPaperEligible={eligible.ToString().ToLowerInvariant()} WouldOpen=false Opened=false ConsistentWithOriginal={(eligible == capture.PaperEligible).ToString().ToLowerInvariant()} ProcessRunId={ProcessRunContext.ProcessRunId}");
            return true;
        }
    }

    private static List<string> Reasons(SingleMarketOpportunityAuditDto a, decimal min)
    {
        var r = new List<string>();
        if (a.AfterSafetyEdge < min) r.Add("BelowMinEdge");
        if (!a.DepthPassed) r.Add("DepthInsufficient");
        if (!a.FillPassed) r.Add("FillFailed");
        if (!a.RiskPassed) r.Add("RiskFailed");
        if (!a.PaperDiagnosticsLimitedGatePassed) r.Add("PaperDiagnosticsLimitedIneligible");
        if (a.RejectedReason is not ("None" or "BelowMinEdge") && !r.Contains(a.RejectedReason)) r.Add(a.RejectedReason);
        return r;
    }
    private static bool IsProfile() => _options is not null && (_options.RuntimeProfile == RuntimeProfileService.ReducedDiagnosticsPaperPhase1 || _options.RuntimeProfile == RuntimeProfileService.ReducedDiagnosticsPaperPhase1Canary);
    private static void Append(PaperPhase1PositiveCapture c) { Directory.CreateDirectory(Path.Combine(_root,"exports")); File.AppendAllText(Path.Combine(_root,"exports/paper-phase1-positive-captures.jsonl"), JsonSerializer.Serialize(c,JsonOptions)+Environment.NewLine); }
    private static void Refresh()
    {
        var top=Buffer.OrderByDescending(x=>x.AfterSafetyEdge).Take(20).ToArray(); var best=top.FirstOrDefault();
        var consistent=Buffer.All(x=>!x.ActualOpened || (x.AfterSafetyEdge>=x.MinEdge && x.PaperEligible && x.ActualOpenAttempted && x.OpenedPositionId!="None" && x.LiveTradingDisabled && x.SigningDisabled));
        Current=new(true,0m,Buffer.Count,Buffer.Count(x=>x.AfterSafetyEdge>=x.MinEdge),Buffer.Count(x=>x.PaperEligible),Buffer.Count(x=>x.ActualOpened),best?.AfterSafetyEdge,best?.CandidateId??"None",best?.FirstBlockingReason??"None",consistent,false,"None",top);
        WriteLatest();
        Console.WriteLine($"[PAPER_PHASE1_POSITIVE_CAPTURE_SUMMARY] CapturesTotal={Current.CapturesTotal} CapturesAboveMinEdge={Current.CapturesAboveMinEdge} CapturesPaperEligible={Current.CapturesPaperEligible} CapturesOpened={Current.CapturesOpened} BestAfterSafetyEdge={Current.BestAfterSafetyEdge?.ToString("0.####")??"N/A"} BestCandidateId={Current.BestCandidateId} BestFirstBlockingReason={Current.BestFirstBlockingReason} Consistent={Current.Consistent.ToString().ToLowerInvariant()} ProcessRunId={ProcessRunContext.ProcessRunId}");
    }
    private static void WriteLatest()
    {
        try { Directory.CreateDirectory(Path.Combine(_root,"exports")); var s=Current; var payload=new{generatedAtUtc=DateTime.UtcNow,processRunId=ProcessRunContext.ProcessRunId,profile=_options?.RuntimeProfile??RuntimeProfileService.ReducedDiagnosticsPaperPhase1,enabled=true,captureThreshold=0m,minEdge=_options?.PaperDiagnosticsLimited.MinEdgeOverride??.01m,summary=new{capturesTotal=s.CapturesTotal,capturesAboveMinEdge=s.CapturesAboveMinEdge,capturesPaperEligible=s.CapturesPaperEligible,capturesOpened=s.CapturesOpened,bestCapturedAfterSafetyEdge=s.BestAfterSafetyEdge?.ToString("0.####")??"N/A",bestCapturedCandidateId=s.BestCandidateId,bestCapturedFirstBlockingReason=s.BestFirstBlockingReason,topBlockingReason=s.TopCaptures.GroupBy(x=>x.FirstBlockingReason).OrderByDescending(x=>x.Count()).FirstOrDefault()?.Key??"None",consistent=s.Consistent},topCaptures=s.TopCaptures}; File.WriteAllText(Path.Combine(_root,"exports/paper-phase1-positive-captures-latest.json"),JsonSerializer.Serialize(payload,JsonOptions)); Current=s with{ExportWritten=true,LastWriteError="None"}; }
        catch(Exception ex){Current=Current with{ExportWritten=false,LastWriteError=ex.Message};}
    }
    private static PaperPhase1PositiveCaptureState Empty()=>new(true,0m,0,0,0,0,null,"None","None",true,false,"None",[]);
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true};
}
