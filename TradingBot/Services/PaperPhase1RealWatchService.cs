using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradingBot.Api;
using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed record PaperPhase1RealWatchState(bool Enabled = true, bool ProfileActive = false, bool Armed = false,
    bool WaitingForEdge = true, decimal? BestAfterSafetyEdge = null, decimal? BestDistanceToMinEdge = null,
    string BestCandidateId = "None", string BestMarketId = "None", string TopBlockingReason = "None",
    string LastEligibleCandidateId = "None", DateTime? LastOpenAttemptUtc = null, string LastOpenResult = "None",
    string LastOpenBlockedReason = "None", int OpenAttempts = 0, int OpenSucceeded = 0, int OpenFailed = 0,
    string OpenedPositionId = "None", int OpenPositions = 0, int ClosedPositions = 0, decimal Exposure = 0,
    decimal Locked = 0, decimal RealizedPnl = 0, string LastClosedPositionId = "None",
    string LastSettlementReason = "None", bool Consistent = true, string ConsistencyReason = "None");

public sealed class PaperPhase1RealWatchService(TradingBotOptions options)
{
    public static PaperPhase1RealWatchState Latest { get; private set; } = new();
    private readonly object _sync = new();
    private PaperTradingEngine? _paper;
    private PaperPositionBook? _book;
    private BotRuntimeState? _state;
    public PaperPhase1RealWatchState Current { get; private set; } = new();

    public void Attach(PaperTradingEngine paper, PaperPositionBook book, BotRuntimeState state)
    { lock (_sync) { _paper = paper; _book = book; _state = state; Current = Current with { ProfileActive = string.Equals(options.RuntimeProfile, RuntimeProfileService.ReducedDiagnosticsPaperPhase1, StringComparison.OrdinalIgnoreCase) }; RefreshLifecycle(); Latest = Current; } }

    public void Refresh(RuntimeHealthSnapshot h)
    {
        lock (_sync)
        {
            RefreshLifecycle();
            var best = PaperPhase1EligibilityLadderExporter.LatestTopNearEligible
                .Select(item => JsonSerializer.SerializeToElement(item))
                .FirstOrDefault();
            var candidateId = best.ValueKind == JsonValueKind.Object && best.TryGetProperty("candidateId", out var c) ? c.GetString() ?? "None" : "None";
            var marketId = best.ValueKind == JsonValueKind.Object && best.TryGetProperty("marketId", out var m) ? m.GetString() ?? "None" : "None";
            var profileActive = string.Equals(options.RuntimeProfile, RuntimeProfileService.ReducedDiagnosticsPaperPhase1, StringComparison.OrdinalIgnoreCase);
            Current = Current with { Enabled = true, ProfileActive = profileActive, Armed = h.PaperPhase1Armed,
                WaitingForEdge = h.PaperPhase1LadderPaperEligible == 0 && Current.OpenPositions == 0,
                BestAfterSafetyEdge = h.PaperPhase1LadderBestAfterSafetyEdge, BestDistanceToMinEdge = h.PaperPhase1LadderBestDistanceToMinEdge,
                BestCandidateId = candidateId, BestMarketId = marketId, TopBlockingReason = h.PaperPhase1LadderTopBlockingReason };
            Latest = Current;
            Export(h);
        }
    }

    public void ApplyReconciliation(PaperPhase1PositiveReconciliationState reconciliation)
    {
        lock (_sync)
        {
            var lifecycleConsistent = Current.ConsistencyReason is "None" or "PositiveCandidateReconciliationMissing";
            Current = Current with { Consistent = lifecycleConsistent && reconciliation.Consistent,
                ConsistencyReason = !lifecycleConsistent ? Current.ConsistencyReason : reconciliation.MismatchBlocking ? "PositiveCandidateReconciliationMissing" : "None" };
            Latest = Current;
        }
    }

    public bool AllowRealOpen(string candidateId, string marketId, decimal afterSafetyEdge, out string reason)
    {
        lock (_sync)
        {
            reason = !string.Equals(options.RuntimeProfile, RuntimeProfileService.ReducedDiagnosticsPaperPhase1, StringComparison.OrdinalIgnoreCase) ? "ProfileNotReducedDiagnosticsPaperPhase1"
                : options.PaperPhase1SyntheticCanary.Enabled ? "SyntheticCanaryEnabled"
                : PaperPhase1PositiveReconciliationService.Latest.MismatchBlocking ? "PositiveCandidateReconciliationMissing"
                : afterSafetyEdge < options.PaperDiagnosticsLimited.MinEdgeOverride ? "BelowMinEdge"
                : options.TradingMode.LiveTradingEnabled || options.EnableLiveExecution ? "LiveTradingEnabled"
                : LiveTradingGuard.SigningAttempts > 0 ? "SigningAttemptDetected"
                : _book is null ? "PositionBookUnavailable"
                : _book.OpenPositions.Count >= 1 ? "MaxOpenPositions"
                : _book.OpenPositions.Sum(x => x.TotalCost) >= 5m ? "MaxPaperTotalExposure"
                : "None";
            if (reason != "None") { Current = Current with { LastOpenBlockedReason = reason }; Console.WriteLine($"[PAPER_PHASE1_REAL_OPEN_BLOCKED] CandidateId={candidateId} Reason={reason} ProcessRunId={ProcessRunContext.ProcessRunId}"); return false; }
            Current = Current with { LastEligibleCandidateId = candidateId, LastOpenAttemptUtc = DateTime.UtcNow, LastOpenResult = "Pending", LastOpenBlockedReason = "None", OpenAttempts = Current.OpenAttempts + 1 };
            Console.WriteLine($"[PAPER_PHASE1_REAL_ELIGIBLE] CandidateId={candidateId} MarketId={marketId} AfterSafetyEdge={afterSafetyEdge:0.####} ProcessRunId={ProcessRunContext.ProcessRunId}");
            return true;
        }
    }

    public void RecordOpenResult(string candidateId, string marketId, decimal afterSafetyEdge, decimal notional, decimal expectedProfit, bool opened)
    {
        lock (_sync)
        {
            var position = _book?.OpenPositions.LastOrDefault(x => x.GroupKey.Equals($"single-market:{marketId}", StringComparison.OrdinalIgnoreCase) && !x.IsSyntheticCanary);
            if (position is not null) { position.Source = "RealScanner"; position.SourceKind = "RealScanner"; position.SourceCandidateId = candidateId; position.ProcessRunId = ProcessRunContext.ProcessRunId; }
            Current = Current with { LastOpenResult = opened ? "Opened" : "Failed", OpenSucceeded = Current.OpenSucceeded + (opened ? 1 : 0), OpenFailed = Current.OpenFailed + (opened ? 0 : 1), OpenedPositionId = position?.PositionId ?? "None" };
            RefreshLifecycle();
            Latest = Current;
            if (opened) Console.WriteLine($"[PAPER_PHASE1_REAL_OPENED] PositionId={Current.OpenedPositionId} CandidateId={candidateId} MarketId={marketId} Notional={notional:0.####} AfterSafetyEdge={afterSafetyEdge:0.####} ExpectedProfit={expectedProfit:0.####} LiveTradingDisabled=true SigningDisabled=true ProcessRunId={ProcessRunContext.ProcessRunId}");
        }
    }

    public PaperSettlementResult SettlePaperPhase1Position(string positionId, decimal realizedPayout, string reason)
    {
        lock (_sync)
        {
            Console.WriteLine($"[PAPER_PHASE1_REAL_SETTLEMENT_REQUESTED] PositionId={positionId} RealizedPayout={realizedPayout:0.####} Reason={reason} ProcessRunId={ProcessRunContext.ProcessRunId}");
            var position = _book?.OpenPositions.FirstOrDefault(x => x.PositionId.Equals(positionId, StringComparison.OrdinalIgnoreCase));
            if (!options.PaperPhase1RealSettlement.Enabled || !options.PaperPhase1RealSettlement.AllowManualSettle || position is null || position.IsSyntheticCanary || _paper is null)
            {
                var reject = position is null ? "PositionNotFound" : position.IsSyntheticCanary ? "SyntheticCanaryNotAllowed" : "ManualSettlementDisabled";
                Console.WriteLine($"[PAPER_PHASE1_REAL_SETTLEMENT_REJECTED] PositionId={positionId} Reason={reject} ProcessRunId={ProcessRunContext.ProcessRunId}");
                var rejected = new PaperSettlementResult(false, reject, position, null); ExportSettlement(rejected, reason); return rejected;
            }
            var result = _paper.SettlePositionDetailed(positionId, realizedPayout, reason, false);
            if (result.Accepted) { Current = Current with { LastClosedPositionId = positionId, LastSettlementReason = reason }; RefreshLifecycle(); Console.WriteLine($"[PAPER_PHASE1_REAL_SETTLED] PositionId={positionId} RealizedPayout={realizedPayout:0.####} RealizedPnl={result.Position?.RealizedProfit:0.####} Reason={reason} ProcessRunId={ProcessRunContext.ProcessRunId}"); }
            else Console.WriteLine($"[PAPER_PHASE1_REAL_SETTLEMENT_REJECTED] PositionId={positionId} Reason={result.Reason} ProcessRunId={ProcessRunContext.ProcessRunId}");
            ExportSettlement(result, reason);
            return result;
        }
    }

    public static string ExecutionId(string candidateId, string marketId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{candidateId}|{marketId}|{ProcessRunContext.ProcessRunId}"))).ToLowerInvariant()[..24];

    private void RefreshLifecycle()
    {
        if (_book is null) return;
        var open = _book.OpenPositions.Where(IsRealPhase1).ToArray(); var closed = _book.ClosedPositions.Where(IsRealPhase1).ToArray();
        var exposure = open.Sum(x => x.TotalCost); var consistent = open.Length <= 1 && exposure <= 5m && Current.OpenSucceeded >= open.Length + closed.Length;
        Current = Current with { OpenPositions = open.Length, ClosedPositions = closed.Length, Exposure = exposure, Locked = open.Sum(x => x.LockedCapital), RealizedPnl = closed.Sum(x => x.RealizedProfit ?? 0m), Consistent = consistent, ConsistencyReason = consistent ? "None" : "LifecycleAccountingMismatch" };
        Latest = Current;
    }
    private static bool IsRealPhase1(PaperPosition p) => !p.IsSyntheticCanary && p.Engine.Equals("SingleMarketBuyBoth", StringComparison.OrdinalIgnoreCase) && p.ProcessRunId == ProcessRunContext.ProcessRunId;

    private void Export(RuntimeHealthSnapshot h)
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "exports/paper-phase1-real-watch-latest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var payload = new { generatedAtUtc=DateTime.UtcNow, processRunId=ProcessRunContext.ProcessRunId, profile=options.RuntimeProfile, enabled=Current.Enabled, armed=Current.Armed, waitingForEdge=Current.WaitingForEdge, minEdge=options.PaperDiagnosticsLimited.MinEdgeOverride, limits=new { maxOpenPositions=1,maxNotional=5,maxExposure=5,maxOpensPerHour=1 }, summary=new { candidatesSeen=h.PaperPhase1LadderSeen,validPriced=h.PaperPhase1LadderValidPriced,nearBreakEven=h.PaperPhase1LadderNearBreakEven,positiveAfterSafety=h.PaperPhase1LadderPositiveAfterSafety,paperEligible=h.PaperPhase1LadderPaperEligible,openAttempts=Current.OpenAttempts,openSucceeded=Current.OpenSucceeded,openFailed=Current.OpenFailed,openedPositionId=Current.OpenedPositionId,bestAfterSafetyEdge=Current.BestAfterSafetyEdge,bestDistanceToMinEdge=Current.BestDistanceToMinEdge,topBlockingReason=Current.TopBlockingReason,consistent=Current.Consistent }, topDecisions=PaperPhase1EligibilityLadderExporter.LatestTopNearEligible.Take(5), openedPosition=_book?.OpenPositions.FirstOrDefault(IsRealPhase1), safety=new { liveTradingDisabled=true,signingDisabled=LiveTradingGuard.SigningAttempts==0,realOrderSent=false,signingAttempted=false } };
        File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented=true, PropertyNamingPolicy=JsonNamingPolicy.CamelCase }));
    }

    private void ExportSettlement(PaperSettlementResult result, string requestedReason)
    {
        var configured = options.PaperPhase1RealSettlement.ExportPath;
        var path = Path.IsPathRooted(configured) ? configured : Path.Combine(Directory.GetCurrentDirectory(), configured);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new { generatedAtUtc=DateTime.UtcNow, processRunId=ProcessRunContext.ProcessRunId, accepted=result.Accepted, result.Reason, requestedReason, position=result.Position, settlement=result.Settlement, safety=new { paperOnly=true, liveTradingDisabled=true, signingDisabled=LiveTradingGuard.SigningAttempts==0 } }, new JsonSerializerOptions { WriteIndented=true, PropertyNamingPolicy=JsonNamingPolicy.CamelCase }));
    }
}
