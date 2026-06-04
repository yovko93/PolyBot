using TradingBot.Models;
using TradingBot.Options;

namespace TradingBot.Services;

public sealed class SingleMarketFullCycleSummaryAggregator
{
    private readonly SingleMarketArbOptions _options;
    private long _cycleId = -1;
    private bool _hasLogged;
    private string _lastLoggedFingerprint = string.Empty;
    private int _lastLoggedHighSeverityRejectCount;
    private int _lastLoggedPositiveEdgeCount;
    private int _lastLoggedPaperOpenedCount;
    private int _batchesSeen;
    private int _marketsScanned;
    private int _dataQualityRejected;
    private int _belowMinEdge;
    private int _positiveEdge;
    private int _edgeStable;
    private int _executionReady;
    private int _fillPassed;
    private int _paperOpened;
    private int _highSeverityRejectCount;
    private decimal? _bestValidEdge;
    private decimal? _bestRejectedRawEdge;
    private readonly Dictionary<string, int> _rejectCounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SingleMarketDataQualityRejectSampleDto> _sampledRejects = new();

    public SingleMarketFullCycleSummaryAggregator(SingleMarketArbOptions options) => _options = options;

    public SingleMarketFullCycleSummary AddBatch(long cycleId, SingleMarketScanSummaryDto batch, IReadOnlyList<SingleMarketDataQualityRejectSampleDto> samples)
    {
        if (_cycleId != cycleId)
        {
            _cycleId = cycleId;
            _batchesSeen = _marketsScanned = _dataQualityRejected = _belowMinEdge = _positiveEdge = _edgeStable = _executionReady = _fillPassed = _paperOpened = _highSeverityRejectCount = 0;
            _bestValidEdge = null;
            _bestRejectedRawEdge = null;
            _rejectCounts.Clear();
            _sampledRejects.Clear();
            _lastLoggedHighSeverityRejectCount = 0;
            _lastLoggedPositiveEdgeCount = 0;
            _lastLoggedPaperOpenedCount = 0;
        }

        _batchesSeen++;
        _marketsScanned += batch.Scanned;
        _dataQualityRejected += batch.DataQualityRejected;
        _belowMinEdge += batch.BelowMinEdge;
        _positiveEdge += batch.PositiveEdge;
        _edgeStable += batch.EdgeStable;
        _executionReady += batch.ExecutionReady;
        _fillPassed += batch.FillPassed;
        _paperOpened += batch.PaperOpened;
        if (batch.BestEdgeSeen.HasValue && (!_bestValidEdge.HasValue || batch.BestEdgeSeen.Value > _bestValidEdge.Value)) _bestValidEdge = batch.BestEdgeSeen;
        if (batch.BestRejectedRawEdge.HasValue && (!_bestRejectedRawEdge.HasValue || batch.BestRejectedRawEdge.Value > _bestRejectedRawEdge.Value)) _bestRejectedRawEdge = batch.BestRejectedRawEdge;
        foreach (var kv in batch.RejectedByReason) _rejectCounts[kv.Key] = _rejectCounts.GetValueOrDefault(kv.Key) + kv.Value;
        foreach (var sample in samples)
        {
            if (_sampledRejects.Count < Math.Max(0, _options.TopDataQualityRejectSampleCount)) _sampledRejects.Add(sample);
            if (IsHighSeverity(sample)) _highSeverityRejectCount++;
        }
        return Current();
    }

    public bool ShouldLog(SingleMarketFullCycleSummary summary, MultiOutcomeLoggingOptions logging, bool fullCycleComplete)
    {
        if (summary.MarketsScanned <= 0) return false;
        var fingerprint = Fingerprint(summary, logging);
        var changed = !string.Equals(_lastLoggedFingerprint, fingerprint, StringComparison.Ordinal);
        var periodic = logging.LogSingleMarketDataQualityEveryNCycles > 0 && summary.CycleId % logging.LogSingleMarketDataQualityEveryNCycles == 0;
        var critical = summary.PositiveEdge > _lastLoggedPositiveEdgeCount
            || summary.PaperOpened > _lastLoggedPaperOpenedCount
            || summary.HighSeverityRejectCount > _lastLoggedHighSeverityRejectCount;
        var materialFullCycleChange = fullCycleComplete && (!logging.LogSingleMarketDataQualityOnChangeOnly || changed);
        var shouldLog = !_hasLogged || critical || periodic || materialFullCycleChange;
        if (!shouldLog) return false;
        _hasLogged = true;
        _lastLoggedFingerprint = fingerprint;
        _lastLoggedHighSeverityRejectCount = summary.HighSeverityRejectCount;
        _lastLoggedPositiveEdgeCount = summary.PositiveEdge;
        _lastLoggedPaperOpenedCount = summary.PaperOpened;
        return true;
    }

    public static string ToLogLine(SingleMarketFullCycleSummary summary)
    {
        var best = summary.BestValidEdge.HasValue ? summary.BestValidEdge.Value.ToString("0.####") : "N/A";
        var bestRejected = summary.BestRejectedRawEdge.HasValue ? summary.BestRejectedRawEdge.Value.ToString("0.####") : "N/A";
        return $"[SINGLE_MARKET_FULL_CYCLE_SUMMARY] Cycle={summary.CycleId} Batches={summary.BatchesSeen} Markets={summary.MarketsScanned} DataQualityRejected={summary.DataQualityRejected} BelowMinEdge={summary.BelowMinEdge} PositiveEdge={summary.PositiveEdge} EdgeStable={summary.EdgeStable} ExecutionReady={summary.ExecutionReady} FillPassed={summary.FillPassed} PaperOpened={summary.PaperOpened} BestEdge={best} BestRejectedRawEdge={bestRejected} HighSeverityRejects={summary.HighSeverityRejectCount}";
    }

    private SingleMarketFullCycleSummary Current()
        => new(_cycleId, _batchesSeen, _marketsScanned, _dataQualityRejected, _belowMinEdge, _positiveEdge, _edgeStable, _executionReady, _fillPassed, _paperOpened, new Dictionary<string, int>(_rejectCounts, StringComparer.OrdinalIgnoreCase), _bestValidEdge, _bestRejectedRawEdge, _highSeverityRejectCount, _sampledRejects.ToArray());

    private static string Fingerprint(SingleMarketFullCycleSummary summary, MultiOutcomeLoggingOptions logging)
    {
        var bucket = Math.Max(1, logging.SingleMarketDataQualityBucketSize);
        var reasonBuckets = string.Join(",", summary.RejectCountsByReason.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase).Select(x => $"{x.Key}:{Bucket(x.Value, bucket)}"));
        var bestBucket = summary.BestValidEdge.HasValue ? Math.Round(summary.BestValidEdge.Value / 0.001m, 0, MidpointRounding.AwayFromZero).ToString() : "N/A";
        return $"dq:{Bucket(summary.DataQualityRejected, bucket)}|reasons:{reasonBuckets}|high:{Bucket(summary.HighSeverityRejectCount, bucket)}|positive:{summary.PositiveEdge}|paper:{summary.PaperOpened}|best:{bestBucket}";
    }

    private bool IsHighSeverity(SingleMarketDataQualityRejectSampleDto sample)
    {
        if (sample.Reason is "SameYesNoTokenId" or "MarketIdMismatch") return true;
        if (sample.Reason != "SuspiciousYesNoAskSum") return false;
        var distance = sample.RawSum < _options.MinReasonableYesNoAskSum
            ? _options.MinReasonableYesNoAskSum - sample.RawSum
            : sample.RawSum > _options.MaxReasonableYesNoAskSum
                ? sample.RawSum - _options.MaxReasonableYesNoAskSum
                : 0m;
        return distance >= _options.HighSeveritySuspiciousAskSumDistance;
    }

    private static int Bucket(int value, int size) => size <= 1 ? value : value / size * size;
}
