using System.Text.Json;
using TradingBot.Api;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record PaperPhase1PositiveCapture(
    string CaptureId, string ProcessRunId, long ScannerCycle, string CandidateId, string MarketId, string Question,
    string Strategy, string Source, bool IsSyntheticCanary, string YesTokenId, string NoTokenId,
    decimal YesAsk, decimal NoAsk, decimal SumAsk, decimal RawEdge, decimal AfterCostEdge, decimal AfterSafetyEdge,
    decimal MinEdge, decimal DistanceToMinEdge, decimal ExpectedProfit, decimal ExpectedPayout, decimal Quantity,
    decimal Notional, DateTime YesOrderbookSnapshotTimestamp, DateTime NoOrderbookSnapshotTimestamp,
    double OrderbookAgeSeconds, bool TokenMappingVerified, bool HasBothBooks, bool HasYesAsk, bool HasNoAsk,
    bool SuspiciousYesNoAskSum, bool InvalidRawSpike, bool CandidateSnapshotMismatch, bool StaleOrderbook,
    bool OrderbookStableNow, bool ReducedUniverseOrderbookStableNow, bool MarketQuarantined, bool TokenQuarantined,
    bool EdgeStable, bool DepthSufficient, bool FillPassed, bool RiskPassed, bool PaperDiagnosticsLimitedEligible,
    bool DuplicatePosition, bool OpenPositionsLimitPassed, bool ExposureLimitPassed, bool OpensPerHourLimitPassed,
    bool PaperEligible, string FirstBlockingReason, IReadOnlyList<string> AllBlockingReasons,
    bool WouldOpenIfAllGatesPassed, bool ActualOpenAttempted, bool ActualOpened, string OpenedPositionId,
    bool LiveTradingDisabled = true, bool SigningDisabled = true)
{
    public bool IsValidClean => Strategy == "SingleMarketBuyBoth" && Source == "RealScanner" && !IsSyntheticCanary &&
        TokenMappingVerified && HasBothBooks && HasYesAsk && HasNoAsk && !SuspiciousYesNoAskSum && !InvalidRawSpike &&
        !CandidateSnapshotMismatch && !StaleOrderbook && AfterSafetyEdge >= 0m;
}

public sealed record PaperPhase1PositiveCaptureState(
    bool Enabled, decimal Threshold, int CapturesTotal, int CapturesAboveMinEdge, int CapturesPaperEligible,
    int CapturesOpened, decimal? BestAfterSafetyEdge, string BestCandidateId, string BestFirstBlockingReason,
    int ValidCapturesTotal, int ValidAboveMinEdge, int ValidPaperEligible, int ValidOpened,
    decimal? BestValidAfterSafetyEdge, string BestValidCandidateId, string BestValidFirstBlockingReason,
    int InvalidArtifactsTotal, int InvalidArtifactsAboveMinEdge, IReadOnlyDictionary<string,int> InvalidArtifactsByReason,
    decimal? BestInvalidArtifactAfterSafetyEdge, string BestInvalidArtifactCandidateId,
    string BestInvalidArtifactFirstReason, bool Consistent, bool ExportWritten, string LastWriteError,
    IReadOnlyList<PaperPhase1PositiveCapture> TopCaptures, IReadOnlyList<PaperPhase1PositiveCapture> TopInvalidArtifacts);

/// <summary>Diagnostic-only evidence store. This type cannot execute or enqueue paper/live orders.</summary>
public static class PaperPhase1PositiveCaptureService
{
    private const int Capacity = 500;
    private static readonly object Sync = new();
    private static readonly List<PaperPhase1PositiveCapture> Valid = [];
    private static readonly List<PaperPhase1PositiveCapture> Invalid = [];
    private static TradingBotOptions? _options;
    private static string _root = Directory.GetCurrentDirectory();
    private static DateTime _intervalStartedUtc = DateTime.MinValue;
    private static DateTime _lastSummaryUtc = DateTime.MinValue;
    private static int _validLogs;
    private static int _invalidLogs;
    private static int _suppressedInvalidLogs;
    public static PaperPhase1PositiveCaptureState Current { get; private set; } = Empty();

    public static void Configure(TradingBotOptions options, string root)
    {
        lock (Sync)
        {
            _options = options; _root = root; Valid.Clear(); Invalid.Clear(); Current = Empty();
            _intervalStartedUtc = DateTime.UtcNow; _lastSummaryUtc = DateTime.MinValue;
            WriteExports();
        }
    }

    public static void Observe(BinaryOrderBookSnapshot book, SingleMarketOpportunityAuditDto audit, long cycle)
    {
        lock (Sync)
        {
            if (_options is null || !IsProfile() || audit.AfterSafetyEdge < 0m) return;
            RotateLogInterval();
            var min = _options.PaperDiagnosticsLimited.MinEdgeOverride;
            var validityReasons = ValidityReasons(book, audit);
            var executionReasons = ExecutionReasons(audit, min);
            var allReasons = OrderedReasons(validityReasons.Concat(executionReasons));
            var validClean = validityReasons.Count == 0;
            var paperEligible = validClean && executionReasons.Count == 0;
            var candidateId = $"SingleMarketBuyBoth:{book.MarketId}:{cycle}";
            var now = DateTime.UtcNow;
            var capture = new PaperPhase1PositiveCapture(
                $"{ProcessRunContext.ProcessRunId}:{candidateId}", ProcessRunContext.ProcessRunId, cycle, candidateId,
                book.MarketId, book.Question, "SingleMarketBuyBoth", "RealScanner", false, book.YesTokenId, book.NoTokenId,
                audit.YesAsk, audit.NoAsk, audit.YesAsk + audit.NoAsk, audit.RawEdge, audit.AfterCostEdge,
                audit.AfterSafetyEdge, min, audit.AfterSafetyEdge-min, audit.AfterSafetyEdge*audit.ExecutableQty,
                audit.ExecutableQty, audit.ExecutableQty, audit.NotionalAtCap, book.TimestampUtc, book.TimestampUtc,
                book.TimestampUtc == default ? 0 : Math.Max(0,(now-book.TimestampUtc).TotalSeconds),
                !validityReasons.Contains("TokenOutcomeMappingUnverified"), book.YesAsk is not null && book.NoAsk is not null,
                book.YesAsk is not null && audit.YesAsk > 0m, book.NoAsk is not null && audit.NoAsk > 0m,
                validityReasons.Contains("SuspiciousYesNoAskSum"), validityReasons.Contains("InvalidRawSpike"),
                validityReasons.Contains("CandidateSnapshotMismatch"), validityReasons.Contains("StaleOrderbook"),
                !audit.RejectedReason.Contains("OrderbookHealth",StringComparison.OrdinalIgnoreCase), true, false, false,
                !executionReasons.Contains("EdgeNotStable"), audit.DepthPassed, audit.FillPassed, audit.RiskPassed,
                audit.PaperDiagnosticsLimitedGatePassed, audit.RejectedReason.Contains("Duplicate",StringComparison.OrdinalIgnoreCase),
                !audit.RejectedReason.Contains("MaxOpen",StringComparison.OrdinalIgnoreCase),
                !audit.RejectedReason.Contains("Exposure",StringComparison.OrdinalIgnoreCase),
                !audit.RejectedReason.Contains("OpensPerHour",StringComparison.OrdinalIgnoreCase), paperEligible,
                allReasons.FirstOrDefault() ?? "None", allReasons, paperEligible, false, false, "None");

            var target = validClean ? Valid : Invalid;
            Upsert(target,capture);
            Append(validClean ? "paper-phase1-positive-captures.jsonl" : "paper-phase1-invalid-positive-artifacts.jsonl",capture);
            if (validClean) LogValid(capture); else LogInvalid(capture);
            RefreshAndExport();
            MaybeLogSummaries();
        }
    }

    public static bool IsValidOpenCandidate(string candidateId)
    {
        lock (Sync)
        {
            return Valid.Any(x => x.CandidateId == candidateId && x.IsValidClean);
        }
    }

    public static bool IsInvalidArtifact(string candidateId)
    {
        lock (Sync)
        {
            return Invalid.Any(x => x.CandidateId == candidateId);
        }
    }

    public static void MarkOpen(string candidateId, bool attempted, bool opened, string positionId)
    {
        lock (Sync)
        {
            var i=Valid.FindLastIndex(x=>x.CandidateId==candidateId);
            if(i<0) return; // invalid artifacts can never acquire an open outcome
            Valid[i]=Valid[i] with{ActualOpenAttempted=attempted,ActualOpened=opened,OpenedPositionId=opened?positionId:"None"};
            Append("paper-phase1-positive-captures.jsonl",Valid[i]); RefreshAndExport();
        }
    }

    public static bool Replay(string captureId)
    {
        lock(Sync)
        {
            var path=Path.Combine(_root,"exports/paper-phase1-positive-captures.jsonl");
            if(!File.Exists(path)) return false;
            var c=File.ReadLines(path).Select(TryParseCapture).LastOrDefault(x=>x?.CaptureId==captureId);
            if(c is null || !c.IsValidClean) return false;
            var eligible=c.AllBlockingReasons.Count==0;
            var payload=new{replayedAtUtc=DateTime.UtcNow,c.CaptureId,sourceProcessRunId=c.ProcessRunId,c.CandidateId,c.MarketId,capturedAfterSafetyEdge=c.AfterSafetyEdge,capturedFirstBlockingReason=c.FirstBlockingReason,replayPaperEligible=eligible,replayFirstBlockingReason=c.AllBlockingReasons.FirstOrDefault()??"None",replayAllBlockingReasons=c.AllBlockingReasons,wouldOpen=false,opened=false,reason="ReplayModeNeverOpens",consistentWithOriginal=eligible==c.PaperEligible};
            WriteAtomic(Path.Combine(_root,"exports/paper-phase1-capture-replay-latest.json"),payload);
            Console.WriteLine($"[PAPER_PHASE1_CAPTURE_REPLAY] CaptureId={c.CaptureId} CandidateId={c.CandidateId} AfterSafetyEdge={c.AfterSafetyEdge:0.####} ReplayPaperEligible={eligible.ToString().ToLowerInvariant()} WouldOpen=false Opened=false ConsistentWithOriginal={(eligible==c.PaperEligible).ToString().ToLowerInvariant()} ProcessRunId={ProcessRunContext.ProcessRunId}");
            return true;
        }
    }

    private static List<string> ValidityReasons(BinaryOrderBookSnapshot b,SingleMarketOpportunityAuditDto a)
    {
        var text=$"{a.DataQualityReason}|{a.RejectedReason}";
        var r=new List<string>();
        if(b.YesAsk is null || a.YesAsk<=0m || text.Contains("MissingYes",StringComparison.OrdinalIgnoreCase)) r.Add("MissingYesAsk");
        if(b.NoAsk is null || a.NoAsk<=0m || text.Contains("MissingNo",StringComparison.OrdinalIgnoreCase)) r.Add("MissingNoAsk");
        if(text.Contains("MissingBook",StringComparison.OrdinalIgnoreCase)) r.Add("MissingBook");
        if(string.IsNullOrWhiteSpace(b.YesTokenId)||string.IsNullOrWhiteSpace(b.NoTokenId)||text.Contains("Token",StringComparison.OrdinalIgnoreCase)) r.Add("TokenOutcomeMappingUnverified");
        if(text.Contains("Suspicious",StringComparison.OrdinalIgnoreCase)||a.YesAsk+a.NoAsk<=0m||a.YesAsk+a.NoAsk>2m) r.Add("SuspiciousYesNoAskSum");
        if(!string.IsNullOrWhiteSpace(a.DataQualityReason)&&r.Count==0) r.Add("InvalidRawSpike");
        if(text.Contains("SnapshotMismatch",StringComparison.OrdinalIgnoreCase)) r.Add("CandidateSnapshotMismatch");
        if(text.Contains("Stale",StringComparison.OrdinalIgnoreCase)) r.Add("StaleOrderbook");
        if(text.Contains("OrderbookHealth",StringComparison.OrdinalIgnoreCase)) r.Add("OrderbookHealthSkipped");
        return r;
    }
    private static List<string> ExecutionReasons(SingleMarketOpportunityAuditDto a,decimal min)
    { var r=new List<string>(); if(a.AfterSafetyEdge<min)r.Add("BelowMinEdge"); if(a.RejectedReason.Contains("Stable",StringComparison.OrdinalIgnoreCase))r.Add("EdgeNotStable"); if(!a.DepthPassed)r.Add("DepthInsufficient"); if(!a.FillPassed)r.Add("FillFailed"); if(!a.RiskPassed)r.Add("RiskFailed"); if(!a.PaperDiagnosticsLimitedGatePassed)r.Add("PaperDiagnosticsLimitedIneligible"); return r; }
    private static readonly string[] Priority=["MissingYesAsk","MissingNoAsk","MissingBook","TokenOutcomeMappingUnverified","SuspiciousYesNoAskSum","InvalidRawSpike","CandidateSnapshotMismatch","StaleOrderbook","OrderbookHealthSkipped","BelowMinEdge","EdgeNotStable","DepthInsufficient","FillFailed","RiskFailed","PaperDiagnosticsLimitedIneligible"];
    private static string[] OrderedReasons(IEnumerable<string> reasons)=>reasons.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(PriorityIndex).ToArray();
    private static int PriorityIndex(string reason){var i=Array.FindIndex(Priority,p=>p.Equals(reason,StringComparison.OrdinalIgnoreCase));return i>=0?i:int.MaxValue;}
    private static void Upsert(List<PaperPhase1PositiveCapture> list,PaperPhase1PositiveCapture c){var i=list.FindIndex(x=>x.CaptureId==c.CaptureId);if(i>=0)list[i]=c;else list.Add(c);if(list.Count>Capacity)list.RemoveRange(0,list.Count-Capacity);}
    private static void RefreshAndExport(){var valid=Valid.OrderByDescending(x=>x.AfterSafetyEdge).Take(20).ToArray();var invalid=Invalid.OrderByDescending(x=>x.AfterSafetyEdge).Take(20).ToArray();var vb=valid.FirstOrDefault();var ib=invalid.FirstOrDefault();var by=Invalid.SelectMany(x=>x.AllBlockingReasons).GroupBy(x=>x,StringComparer.OrdinalIgnoreCase).ToDictionary(x=>x.Key,x=>x.Count(),StringComparer.OrdinalIgnoreCase);var consistent=Invalid.All(x=>!x.ActualOpenAttempted&&!x.ActualOpened)&&Valid.All(x=>!x.ActualOpened||(x.IsValidClean&&x.PaperEligible&&x.FirstBlockingReason=="None"&&x.OpenedPositionId!="None"));Current=new(true,0m,Valid.Count+Invalid.Count,Valid.Count(x=>x.AfterSafetyEdge>=x.MinEdge)+Invalid.Count(x=>x.AfterSafetyEdge>=x.MinEdge),Valid.Count(x=>x.PaperEligible),Valid.Count(x=>x.ActualOpened),vb?.AfterSafetyEdge,vb?.CandidateId??"None",vb?.FirstBlockingReason??"None",Valid.Count,Valid.Count(x=>x.AfterSafetyEdge>=x.MinEdge),Valid.Count(x=>x.PaperEligible),Valid.Count(x=>x.ActualOpened),vb?.AfterSafetyEdge,vb?.CandidateId??"None",vb?.FirstBlockingReason??"None",Invalid.Count,Invalid.Count(x=>x.AfterSafetyEdge>=x.MinEdge),by,ib?.AfterSafetyEdge,ib?.CandidateId??"None",ib?.FirstBlockingReason??"None",consistent,false,"None",valid,invalid);WriteExports();}
    private static void WriteExports(){try{var s=Current;var mainItems=_options?.PaperPhase1PositiveCapture.IncludeInvalidArtifactsInMainExport==true?s.TopCaptures.Concat(s.TopInvalidArtifacts).OrderByDescending(x=>x.AfterSafetyEdge).Take(20):s.TopCaptures;WriteAtomic(Path.Combine(_root,"exports/paper-phase1-positive-captures-latest.json"),new{generatedAtUtc=DateTime.UtcNow,processRunId=ProcessRunContext.ProcessRunId,profile=_options?.RuntimeProfile??RuntimeProfileService.ReducedDiagnosticsPaperPhase1,enabled=true,captureThreshold=0m,minEdge=_options?.PaperDiagnosticsLimited.MinEdgeOverride??.01m,scope="ValidPositiveCapture",summary=new{capturesTotal=s.ValidCapturesTotal,capturesAboveMinEdge=s.ValidAboveMinEdge,capturesPaperEligible=s.ValidPaperEligible,capturesOpened=s.ValidOpened,bestCapturedAfterSafetyEdge=s.BestValidAfterSafetyEdge?.ToString("0.####")??"N/A",bestCapturedCandidateId=s.BestValidCandidateId,bestCapturedFirstBlockingReason=s.BestValidFirstBlockingReason,consistent=s.Consistent},topCaptures=mainItems});if(_options?.PaperPhase1PositiveCapture.InvalidArtifactExportEnabled!=false)WriteAtomic(Path.Combine(_root,"exports/paper-phase1-invalid-positive-artifacts-latest.json"),new{generatedAtUtc=DateTime.UtcNow,processRunId=ProcessRunContext.ProcessRunId,summary=new{artifactsTotal=s.InvalidArtifactsTotal,aboveMinEdge=s.InvalidArtifactsAboveMinEdge,bestAfterSafetyEdge=s.BestInvalidArtifactAfterSafetyEdge,bestCandidateId=s.BestInvalidArtifactCandidateId,topReason=s.InvalidArtifactsByReason.OrderByDescending(x=>x.Value).FirstOrDefault().Key??"None",byReason=s.InvalidArtifactsByReason,consistent=s.Consistent},topArtifacts=s.TopInvalidArtifacts.Select(x=>new{x.CandidateId,x.MarketId,x.AfterSafetyEdge,firstReason=x.FirstBlockingReason,allReasons=x.AllBlockingReasons,x.HasYesAsk,x.HasNoAsk,x.HasBothBooks,x.CandidateSnapshotMismatch,x.InvalidRawSpike,paperOpenAllowed=false})});Current=s with{ExportWritten=true,LastWriteError="None"};}catch(Exception ex){Current=Current with{ExportWritten=false,LastWriteError=ex.Message};}}
    private static void RotateLogInterval(){var seconds=_options!.PaperPhase1PositiveCapture.SummaryIntervalSeconds;if(DateTime.UtcNow-_intervalStartedUtc<TimeSpan.FromSeconds(seconds))return;_intervalStartedUtc=DateTime.UtcNow;_validLogs=0;_invalidLogs=0;}
    private static void LogValid(PaperPhase1PositiveCapture c){if(_validLogs++>=_options!.PaperPhase1PositiveCapture.LogFirstNValidPerInterval)return;Console.WriteLine($"[PAPER_PHASE1_POSITIVE_CAPTURED] CandidateId={c.CandidateId} MarketId={c.MarketId} AfterSafetyEdge={c.AfterSafetyEdge:0.####} MinEdge={c.MinEdge:0.####} PaperEligible={c.PaperEligible.ToString().ToLowerInvariant()} FirstBlockingReason={c.FirstBlockingReason} ActualOpenAttempted=false ActualOpened=false ProcessRunId={c.ProcessRunId}");}
    private static void LogInvalid(PaperPhase1PositiveCapture c){if(_invalidLogs++>=_options!.PaperPhase1PositiveCapture.LogFirstNInvalidArtifactsPerInterval){_suppressedInvalidLogs++;return;}Console.WriteLine($"[PAPER_PHASE1_INVALID_POSITIVE_ARTIFACT_CAPTURED] CandidateId={c.CandidateId} MarketId={c.MarketId} AfterSafetyEdge={c.AfterSafetyEdge:0.####} FirstReason={c.FirstBlockingReason} AllReasons={string.Join("|",c.AllBlockingReasons)} PaperOpenAllowed=false ProcessRunId={c.ProcessRunId}");}
    private static void MaybeLogSummaries(){var cfg=_options!.PaperPhase1PositiveCapture;if(cfg.SuppressRepeatedSummary&&DateTime.UtcNow-_lastSummaryUtc<TimeSpan.FromSeconds(cfg.SummaryIntervalSeconds))return;_lastSummaryUtc=DateTime.UtcNow;var s=Current;Console.WriteLine($"[PAPER_PHASE1_POSITIVE_CAPTURE_SUMMARY] ValidCapturesTotal={s.ValidCapturesTotal} ValidAboveMinEdge={s.ValidAboveMinEdge} ValidPaperEligible={s.ValidPaperEligible} ValidOpened={s.ValidOpened} BestValidAfterSafetyEdge={s.BestValidAfterSafetyEdge?.ToString("0.####")??"N/A"} BestValidFirstBlockingReason={s.BestValidFirstBlockingReason} ProcessRunId={ProcessRunContext.ProcessRunId}");Console.WriteLine($"[PAPER_PHASE1_INVALID_POSITIVE_ARTIFACT_SUMMARY] ArtifactsTotal={s.InvalidArtifactsTotal} AboveMinEdge={s.InvalidArtifactsAboveMinEdge} BestArtifactAfterSafetyEdge={s.BestInvalidArtifactAfterSafetyEdge?.ToString("0.####")??"N/A"} TopReason={s.BestInvalidArtifactFirstReason} ByReason={string.Join("|",s.InvalidArtifactsByReason.Select(x=>$"{x.Key}={x.Value}"))} SuppressedLogs={_suppressedInvalidLogs} ProcessRunId={ProcessRunContext.ProcessRunId}");}
    private static void Append(string name,PaperPhase1PositiveCapture c){Directory.CreateDirectory(Path.Combine(_root,"exports"));File.AppendAllText(Path.Combine(_root,"exports",name),JsonSerializer.Serialize(c,JsonLineOptions)+Environment.NewLine);}
    private static void WriteAtomic(string path,object value){Directory.CreateDirectory(Path.GetDirectoryName(path)!);var tmp=path+".tmp";File.WriteAllText(tmp,JsonSerializer.Serialize(value,JsonOptions));File.Move(tmp,path,true);}
    private static PaperPhase1PositiveCapture? TryParseCapture(string line){try{return JsonSerializer.Deserialize<PaperPhase1PositiveCapture>(line,JsonOptions);}catch(JsonException){return null;}}
    private static bool IsProfile()=>_options is not null&&(_options.RuntimeProfile==RuntimeProfileService.ReducedDiagnosticsPaperPhase1||_options.RuntimeProfile==RuntimeProfileService.ReducedDiagnosticsPaperPhase1Canary);
    private static PaperPhase1PositiveCaptureState Empty()=>new(true,0,0,0,0,0,null,"None","None",0,0,0,0,null,"None","None",0,0,new Dictionary<string,int>(),null,"None","None",true,false,"None",[],[]);
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true,PropertyNamingPolicy=JsonNamingPolicy.CamelCase,PropertyNameCaseInsensitive=true};
    private static readonly JsonSerializerOptions JsonLineOptions=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
}
