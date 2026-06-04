using System.Globalization;
using System.Numerics;
using System.Text;
using TradingBot.Models;

namespace TradingBot.Services;

public class OpportunityMonitor
{
    private readonly object _lock = new();

    private readonly string _csvPath;
    private readonly decimal _alertEdgeThreshold;
    private readonly decimal _minRecordEdgePerShare;
    private readonly TimeSpan _alertCooldown;
    private readonly decimal _minAlertExpectedProfit;

    private readonly List<ArbMonitorRecord> _buffer = new();
    private readonly List<ArbMonitorRecord> _cycleRecords = new();
    private readonly Dictionary<string, DateTime> _lastAlerts = new();
    private readonly Dictionary<string, (DateTime Time, decimal Edge, bool Executable)> _lastWritten = new();
    private readonly DryRunLiveOrderBuilder? _dryRunOrderBuilder;

    private readonly TimeSpan _csvWriteCooldown = TimeSpan.FromMinutes(1);
    private readonly decimal _minEdgeChangeToRewrite = 0.001m;

    public OpportunityMonitor(
        string csvPath = "data/arb-opportunities.csv",
        decimal alertEdgeThreshold = 0.003m,
        decimal minRecordEdgePerShare = -0.02m,
        TimeSpan? alertCooldown = null,
        decimal minAlertExpectedProfit = 0,
        DryRunLiveOrderBuilder? dryRunOrderBuilder = null)
    {
        _csvPath = csvPath;
        _alertEdgeThreshold = alertEdgeThreshold;
        _minRecordEdgePerShare = minRecordEdgePerShare;
        _minAlertExpectedProfit = minAlertExpectedProfit;
        _alertCooldown = alertCooldown ?? TimeSpan.FromMinutes(2);
        _dryRunOrderBuilder = dryRunOrderBuilder;

        EnsureCsvHeader();
    }

    public void BeginCycle()
    {
        lock (_lock)
        {
            _cycleRecords.Clear();
        }
    }

    public void Record(ArbMonitorRecord record)
    {
        if (record.EdgePerShare < _minRecordEdgePerShare)
            return;

        lock (_lock)
        {
            _cycleRecords.Add(record);

            if (ShouldWriteToCsvUnderLock(record))
                _buffer.Add(record);

            TryAlertUnderLock(record);
        }
    }

    public void AddExternalOpportunity(string key, string strategy, string leg1, decimal edgePerShare, decimal expectedProfit, decimal costOrProceeds, decimal guaranteedPayout, decimal qty)
    {
        Record(new ArbMonitorRecord(DateTime.UtcNow, "CrossExchangeArbEngine", strategy, key, edgePerShare, costOrProceeds, guaranteedPayout, qty, expectedProfit, true, leg1));
    }

    public void PrintCycleRanking(int top = 15, bool executableOnly = false)
    {
        var ranked = GetTopCycleRecords(top, executableOnly);

        Console.WriteLine();

        if (executableOnly)
            Console.WriteLine("========== OPPORTUNITY RANKING: EXECUTABLE ==========");
        else
            Console.WriteLine("========== OPPORTUNITY RANKING: ALL RECORDED ==========");

        if (ranked.Count == 0)
        {
            if (executableOnly)
                Console.WriteLine("No executable opportunities recorded this cycle.");
            else
                Console.WriteLine("No opportunities recorded above minRecordEdgePerShare this cycle.");

            Console.WriteLine("======================================================");
            Console.WriteLine();
            return;
        }

        foreach (var item in ranked)
        {
            Console.WriteLine("----------------------------------------");
            Console.WriteLine($"{item.Engine} | {item.Strategy}");
            Console.WriteLine($"Edge/share: {item.EdgePerShare:0.####}");
            Console.WriteLine($"Expected profit: {item.ExpectedProfit:0.####}");
            Console.WriteLine($"Cost/Proceeds: {item.CostOrProceeds:0.####}");
            Console.WriteLine($"Guaranteed: {item.GuaranteedPayout:0.####}");
            Console.WriteLine($"Executable: {item.IsExecutable}");
            Console.WriteLine($"Qty available: {item.QuantityAvailable:0.####}");

            if (!string.IsNullOrWhiteSpace(item.GroupKey))
                Console.WriteLine($"Group: {item.GroupKey}");

            Console.WriteLine($"Leg1: {item.Leg1}");

            if (!string.IsNullOrWhiteSpace(item.Leg2))
                Console.WriteLine($"Leg2: {item.Leg2}");
        }

        Console.WriteLine("======================================================");
        Console.WriteLine();
    }

    public List<ArbMonitorRecord> GetTopCycleRecords(int top = 10, bool executableOnly = true)
    {
        lock (_lock)
        {
            var query = _cycleRecords.AsEnumerable();

            if (executableOnly)
                query = query.Where(x => x.IsExecutable);

            return query
                .OrderByDescending(x => x.EdgePerShare)
                .Take(top)
                .ToList();
        }
    }

    public void FlushCsv()
    {
        List<ArbMonitorRecord> rows;

        lock (_lock)
        {
            if (_buffer.Count == 0)
                return;

            rows = _buffer.ToList();
            _buffer.Clear();
        }

        var sb = new StringBuilder();

        foreach (var row in rows)
            sb.AppendLine(ToCsvLine(row));

        File.AppendAllText(_csvPath, sb.ToString());
    }

    // todo remove debug
    public void BuildDebugDryRunForTopRecorded(int top = 3)
    {
        if (_dryRunOrderBuilder == null)
            return;

        var records = GetTopCycleRecords(top, executableOnly: false);

        if (records.Count == 0)
        {
            Console.WriteLine("[DEBUG DRY-RUN] No recorded opportunities.");
            return;
        }

        Console.WriteLine();
        Console.WriteLine("========== DEBUG DRY-RUN ORDER PLANS ==========");

        foreach (var record in records)
        {
            if (record.OrderLegs.Count == 0)
            {
                Console.WriteLine($"[DEBUG DRY-RUN SKIP] No OrderLegs | {record.Engine} | {record.Strategy}");
                continue;
            }

            var planId =
                $"DEBUG_{record.Engine}_{record.Strategy}_{record.Key}_{DateTime.UtcNow:yyyyMMddHHmmssfff}"
                    .Replace(" ", "_")
                    .Replace("|", "_")
                    .Replace(":", "_");

            var plan = _dryRunOrderBuilder.BuildPlan(
                planId: planId,
                legs: record.OrderLegs,
                errors: out var errors
            );

            if (plan == null)
            {
                Console.WriteLine($"[DEBUG DRY-RUN REJECTED] {record.Engine} | {record.Strategy}");
                Console.WriteLine($"Edge/share: {record.EdgePerShare:0.####}");

                foreach (var error in errors)
                    Console.WriteLine($" - {error}");

                continue;
            }

            _dryRunOrderBuilder.SavePlan(plan);

            Console.WriteLine($"[DEBUG DRY-RUN CREATED] {record.Engine} | {record.Strategy}");
            Console.WriteLine($"Plan: {plan.PlanId}");
            Console.WriteLine($"Orders: {plan.Orders.Count}");
            Console.WriteLine($"Estimated cost: {plan.TotalEstimatedCost:0.####}");
            Console.WriteLine($"Edge/share: {plan.EdgePerShare:0.####}");
        }

        Console.WriteLine("================================================");
        Console.WriteLine();
    }

    #region Helpers
    private bool ShouldWriteToCsvUnderLock(ArbMonitorRecord record)
    {
        var key = $"{record.Engine}|{record.Strategy}|{record.Key}";
        var now = DateTime.UtcNow;

        if (!_lastWritten.TryGetValue(key, out var previous))
        {
            _lastWritten[key] = (now, record.EdgePerShare, record.IsExecutable);
            return true;
        }

        if (record.IsExecutable && !previous.Executable)
        {
            _lastWritten[key] = (now, record.EdgePerShare, record.IsExecutable);
            return true;
        }

        var edgeChangedEnough =
            Math.Abs(record.EdgePerShare - previous.Edge) >= _minEdgeChangeToRewrite;

        if (edgeChangedEnough)
        {
            _lastWritten[key] = (now, record.EdgePerShare, record.IsExecutable);
            return true;
        }

        if (now - previous.Time >= _csvWriteCooldown)
        {
            _lastWritten[key] = (now, record.EdgePerShare, record.IsExecutable);
            return true;
        }

        return false;
    }

    private void TryAlertUnderLock(ArbMonitorRecord record)
    {
        if (record.ExpectedProfit < _minAlertExpectedProfit)
            return;

        if (record.EdgePerShare < _alertEdgeThreshold)
            return;

        var alertKey = $"{record.Engine}|{record.Strategy}|{record.Key}";
        var now = DateTime.UtcNow;

        if (_lastAlerts.TryGetValue(alertKey, out var lastAlert) &&
            now - lastAlert < _alertCooldown)
        {
            return;
        }

        _lastAlerts[alertKey] = now;

        var previousColor = Console.ForegroundColor;
        Console.ForegroundColor = ConsoleColor.Yellow;

        Console.WriteLine();
        Console.WriteLine("========== ARB ALERT ==========");
        Console.WriteLine($"{record.Engine} | {record.Strategy}");
        Console.WriteLine($"Edge/share: {record.EdgePerShare:0.####}");
        Console.WriteLine($"Expected profit: {record.ExpectedProfit:0.####}");
        Console.WriteLine($"Cost/Proceeds: {record.CostOrProceeds:0.####}");
        Console.WriteLine($"Guaranteed: {record.GuaranteedPayout:0.####}");
        Console.WriteLine($"Qty available: {record.QuantityAvailable:0.####}");

        if (!string.IsNullOrWhiteSpace(record.GroupKey))
            Console.WriteLine($"Group: {record.GroupKey}");

        Console.WriteLine($"Leg1: {record.Leg1}");

        if (!string.IsNullOrWhiteSpace(record.Leg2))
            Console.WriteLine($"Leg2: {record.Leg2}");

        Console.WriteLine("===============================");
        Console.WriteLine();

        TryBuildDryRunOrderPlanUnderLock(record);

        Console.ForegroundColor = previousColor;

        try
        {
            Console.Beep();
        }
        catch
        {
            // Console.Beep may not work on every OS/terminal.
        }
    }

    private void TryBuildDryRunOrderPlanUnderLock(ArbMonitorRecord record)
    {
        if (_dryRunOrderBuilder == null)
            return;

        if (!record.IsExecutable)
            return;

        if (record.OrderLegs.Count == 0)
        {
            Console.WriteLine("[DRY-RUN ORDER SKIPPED] No structured order legs on record.");
            return;
        }

        var plan = _dryRunOrderBuilder.BuildPlan(
            planId: BuildDryRunPlanId(record),
            legs: record.OrderLegs,
            errors: out var errors
        );

        if (plan == null)
        {
            Console.WriteLine("[DRY-RUN ORDER REJECTED]");

            foreach (var error in errors)
                Console.WriteLine($" - {error}");

            return;
        }

        _dryRunOrderBuilder.SavePlan(plan);

        Console.WriteLine("[DRY_RUN_ORDER_PLAN_CREATED]");
        Console.WriteLine($"Plan: {plan.PlanId}");
        Console.WriteLine($"Orders: {plan.Orders.Count}");
        Console.WriteLine($"Estimated cost: {plan.TotalEstimatedCost:0.####}");
    }

    private static string BuildDryRunPlanId(ArbMonitorRecord record)
    {
        var raw =
            $"{record.Engine}_{record.Strategy}_{record.Key}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";

        return raw
            .Replace(" ", "_")
            .Replace("|", "_")
            .Replace(":", "_")
            .Replace("/", "_")
            .Replace("\\", "_");
    }

    private void EnsureCsvHeader()
    {
        var directory = Path.GetDirectoryName(_csvPath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(_csvPath))
            return;

        var header =
            "timestampUtc,engine,strategy,key,edgePerShare,costOrProceeds,guaranteedPayout,quantityAvailable,isExecutable,groupKey,leg1,leg2";

        File.WriteAllText(_csvPath, header + Environment.NewLine);
    }

    private static string ToCsvLine(ArbMonitorRecord row)
    {
        return string.Join(",",
            Csv(row.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
            Csv(row.Engine),
            Csv(row.Strategy),
            Csv(row.Key),
            Csv(row.EdgePerShare),
            Csv(row.CostOrProceeds),
            Csv(row.GuaranteedPayout),
            Csv(row.QuantityAvailable),
            Csv(row.IsExecutable ? "true" : "false"),
            Csv(row.GroupKey ?? ""),
            Csv(row.Leg1),
            Csv(row.Leg2 ?? "")
        );
    }

    private static string Csv(decimal value)
    {
        return Csv(value.ToString("0.########", CultureInfo.InvariantCulture));
    }

    private static string Csv(string value)
    {
        value = value.Replace("\"", "\"\"");
        return $"\"{value}\"";
    }
    #endregion
}
