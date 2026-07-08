using System.Text.Json;
using TradingBot.Api;
using TradingBot.Options;

namespace TradingBot.Services;

public static class PaperPhase1ReadinessExporter
{
    private static DateTime _lastReadinessLogUtc = DateTime.MinValue;
    private static DateTime _lastGateLogUtc = DateTime.MinValue;

    public static object Build(RuntimeHealthSnapshot h) => new
    {
        generatedAtUtc = DateTime.UtcNow,
        processRunId = h.ProcessRunId,
        profile = h.RuntimeProfile,
        enabled = h.PaperPhase1Enabled,
        armed = h.PaperPhase1Armed,
        readiness = h.PaperPhase1Readiness,
        readinessReason = h.PaperPhase1ReadinessReason,
        allowedStrategy = h.PaperPhase1AllowedStrategy,
        liveTradingDisabled = h.PaperPhase1LiveTradingDisabled,
        signingDisabled = h.PaperPhase1SigningDisabled,
        reducedUniversePaperExplicitlyAllowed = h.PaperPhase1ReducedUniversePaperExplicitlyAllowed,
        limits = new { maxOpenPositions = h.PaperPhase1MaxOpenPositions, maxNotionalPerTrade = h.PaperPhase1MaxNotionalPerTrade, maxTotalExposure = h.PaperPhase1MaxTotalExposure, maxOpensPerHour = h.PaperPhase1MaxOpensPerHour, minEdge = h.PaperPhase1MinEdge },
        counters = new { candidatesSeen = h.PaperPhase1CandidatesSeen, candidatesRejected = h.PaperPhase1CandidatesRejected, candidatesEligible = h.PaperPhase1CandidatesEligible, openAttempts = h.PaperPhase1OpenAttempts, openSucceeded = h.PaperPhase1OpenSucceeded, openFailed = h.PaperPhase1OpenFailed, paperOpened = h.PaperPhase1PaperOpened },
        lastRejectReason = h.PaperPhase1LastRejectReason,
        lastOpenBlockedReason = h.PaperPhase1LastOpenBlockedReason,
        consistencyOk = h.PaperPhase1ConsistencyOk,
        warnings = h.PaperPhase1ConsistencyOk ? Array.Empty<string>() : new[] { "PaperPhase1ConsistencyFailed" }
    };

    public static void ExportLatest(RuntimeHealthSnapshot h, string root)
    {
        try
        {
            var path = Path.Combine(root, "exports/paper-phase1-readiness-latest.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(Build(h), new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            File.WriteAllText(path + ".tmp", json);
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) { Console.WriteLine($"[PAPER_PHASE1_EXPORT_WARNING] Error={ex.Message}"); }
    }

    public static void MaybeLog(RuntimeHealthSnapshot h, TradingBotOptions options)
    {
        var now = DateTime.UtcNow;
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.DiagnosticsDashboard.WriteIntervalSeconds));
        if (now - _lastReadinessLogUtc >= interval)
        {
            _lastReadinessLogUtc = now;
            Console.WriteLine($"[PAPER_PHASE1_READINESS] Enabled={h.PaperPhase1Enabled.ToString().ToLowerInvariant()} Profile={h.RuntimeProfile} Armed={h.PaperPhase1Armed.ToString().ToLowerInvariant()} Readiness={h.PaperPhase1Readiness.ToString().ToLowerInvariant()} Reason={h.PaperPhase1ReadinessReason} AllowedStrategy={h.PaperPhase1AllowedStrategy} MaxOpenPositions={h.PaperPhase1MaxOpenPositions} MaxNotional={h.PaperPhase1MaxNotionalPerTrade:0.####} MaxExposure={h.PaperPhase1MaxTotalExposure:0.####} MinEdge={h.PaperPhase1MinEdge:0.####} LiveTradingDisabled={h.PaperPhase1LiveTradingDisabled.ToString().ToLowerInvariant()} SigningDisabled={h.PaperPhase1SigningDisabled.ToString().ToLowerInvariant()} ReducedUniversePaperExplicitlyAllowed={h.PaperPhase1ReducedUniversePaperExplicitlyAllowed.ToString().ToLowerInvariant()} ProcessRunId={h.ProcessRunId}");
        }
        if (now - _lastGateLogUtc >= interval)
        {
            _lastGateLogUtc = now;
            Console.WriteLine($"[PAPER_PHASE1_GATE_SUMMARY] CandidatesSeen={h.PaperPhase1CandidatesSeen} Rejected={h.PaperPhase1CandidatesRejected} Eligible={h.PaperPhase1CandidatesEligible} OpenAttempts={h.PaperPhase1OpenAttempts} OpenSucceeded={h.PaperPhase1OpenSucceeded} PaperOpened={h.PaperPhase1PaperOpened} LastRejectReason={h.PaperPhase1LastRejectReason} LastOpenBlockedReason={h.PaperPhase1LastOpenBlockedReason} ConsistencyOk={h.PaperPhase1ConsistencyOk.ToString().ToLowerInvariant()} ProcessRunId={h.ProcessRunId}");
        }
    }
}
