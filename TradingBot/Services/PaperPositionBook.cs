using System.Globalization;
using System.Text;
using TradingBot.Models;

namespace TradingBot.Services;

public class PaperPositionBook
{
    private readonly object _lock = new();

    private readonly Dictionary<string, PaperPosition> _openPositions = new();
    private readonly List<PaperPosition> _closedPositions = new();

    private readonly Dictionary<string, DateTime> _basketCooldownUntil = new();
    private readonly TimeSpan _basketCooldown = TimeSpan.FromMinutes(5);

    private readonly string _csvPath;

    public PaperPositionBook(string csvPath = "data/paper-positions.csv")
    {
        _csvPath = csvPath;
        EnsureCsvHeader();
    }

    public IReadOnlyCollection<PaperPosition> OpenPositions
    {
        get
        {
            lock (_lock)
            {
                return _openPositions.Values.ToList();
            }
        }
    }

    public IReadOnlyCollection<PaperPosition> ClosedPositions
    {
        get
        {
            lock (_lock)
            {
                return _closedPositions.ToList();
            }
        }
    }

    public decimal OpenTotalCost
    {
        get
        {
            lock (_lock)
            {
                return _openPositions.Values.Sum(x => x.TotalCost);
            }
        }
    }

    public decimal OpenExpectedProfit
    {
        get
        {
            lock (_lock)
            {
                return _openPositions.Values.Sum(x => x.ExpectedProfit);
            }
        }
    }

    private bool IsBlockedNoLock(string positionId, out string reason)
    {
        var now = DateTime.UtcNow;

        if (_openPositions.ContainsKey(positionId))
        {
            reason = $"[PAPER BASKET SKIP] Duplicate open position. ID={positionId}";
            return true;
        }

        if (_basketCooldownUntil.TryGetValue(positionId, out var cooldownUntil))
        {
            if (now < cooldownUntil)
            {
                var remaining = cooldownUntil - now;

                reason =
                    $"[PAPER BASKET SKIP] Basket in cooldown. " +
                    $"ID={positionId}, RemainingSeconds={remaining.TotalSeconds:0}";

                return true;
            }

            _basketCooldownUntil.Remove(positionId);
        }

        reason = "";
        return false;
    }

    public PaperPosition? AddBasketPosition(
        BasketArbOpportunity opportunity,
        decimal executableQuantity,
        decimal totalCost,
        decimal expectedProfit,
        string engine = "MultiOutcomeGroup")
    {
        var position = new PaperPosition
        {
            PositionId = BuildPositionId(opportunity),
            OpenedAtUtc = DateTime.UtcNow,
            Engine = engine,
            Strategy = opportunity.Strategy,
            GroupKey = opportunity.GroupKey,
            Quantity = executableQuantity,
            TotalCost = totalCost,
            CostPerBasket = opportunity.CostPerShare,
            GuaranteedPayout = executableQuantity * opportunity.GuaranteedPayoutPerShare,
            EdgePerShare = opportunity.EdgePerShare,
            ExpectedProfit = expectedProfit,
            GrossEdgeAtOpen = opportunity.EdgePerShare,
            NetEdgeAtOpen = opportunity.EdgePerShare,
            LockedCapital = totalCost,
            ActiveProfile = "Conservative",
            Source = engine,
            Status = PaperPositionStatus.Open,
            Legs = opportunity.Legs
                .Select(x => new PaperPositionLeg(
                    MarketId: x.MarketId,
                    Question: x.Question,
                    Outcome: x.Outcome,
                    Price: x.Price,
                    Quantity: executableQuantity,
                    Notional: executableQuantity * x.Price
                ))
                .ToList()
        };

        lock (_lock)
        {
            if (IsBlockedNoLock(position.PositionId, out var reason))
            {
                Console.WriteLine(reason);
                return null;
            }

            _openPositions[position.PositionId] = position;
            AppendCsv(position);
        }

        return position;
    }

    public PaperPosition? AddTwoLegArbPosition(
    ArbOpportunity opportunity,
    decimal executableQuantity,
    decimal totalCost,
    decimal expectedProfit,
    decimal guaranteedPayoutPerShare,
    string engine)
    {
        var position = new PaperPosition
        {
            PositionId = BuildTwoLegPositionId(opportunity, engine),
            OpenedAtUtc = DateTime.UtcNow,
            Engine = engine,
            Strategy = "TWO_LEG_ARB",
            GroupKey = BuildTwoLegGroupKey(opportunity),
            Quantity = executableQuantity,
            TotalCost = totalCost,
            GuaranteedPayout = executableQuantity * guaranteedPayoutPerShare,
            EdgePerShare = opportunity.GrossEdgePerShare,
            ExpectedProfit = expectedProfit,
            Status = PaperPositionStatus.Open,
            Legs = new List<PaperPositionLeg>
        {
            new(
                MarketId: opportunity.Leg1.MarketId,
                Question: opportunity.Leg1.Question,
                Outcome: opportunity.Leg1.Outcome,
                Price: opportunity.Leg1.Price,
                Quantity: executableQuantity,
                Notional: executableQuantity * opportunity.Leg1.Price
            ),
            new(
                MarketId: opportunity.Leg2.MarketId,
                Question: opportunity.Leg2.Question,
                Outcome: opportunity.Leg2.Outcome,
                Price: opportunity.Leg2.Price,
                Quantity: executableQuantity,
                Notional: executableQuantity * opportunity.Leg2.Price
            )
        }
        };

        lock (_lock)
        {
            if (IsBlockedNoLock(position.PositionId, out var reason))
            {
                Console.WriteLine(reason);
                return null;
            }

            _openPositions[position.PositionId] = position;
            AppendCsv(position);
        }

        return position;
    }

    private static string BuildTwoLegPositionId(
        ArbOpportunity opportunity,
        string engine)
    {
        return string.Join("_",
                engine,
                opportunity.Leg1.MarketId,
                opportunity.Leg1.Outcome,
                opportunity.Leg2.MarketId,
                opportunity.Leg2.Outcome
            )
            .Replace(" ", "_")
            .Replace("|", "_")
            .Replace(":", "_");
    }

    private static string BuildTwoLegGroupKey(ArbOpportunity opportunity)
    {
        if (opportunity.Leg1.MarketId == opportunity.Leg2.MarketId)
            return $"single-market:{opportunity.Leg1.MarketId}";

        return $"cross-market:{opportunity.Leg1.MarketId}:{opportunity.Leg2.MarketId}";
    }

    public List<PaperPosition> GetOpenPositions()
    {
        lock (_lock)
        {
            return _openPositions.Values.ToList();
        }
    }

    public bool ClosePosition(
        string positionId,
        decimal realizedPayout,
        out PaperPosition? closedPosition)
    {
        lock (_lock)
        {
            if (!_openPositions.TryGetValue(positionId, out var position))
            {
                closedPosition = null;
                return false;
            }

            position.ClosedAtUtc = DateTime.UtcNow;
            position.RealizedPayout = realizedPayout;
            position.RealizedProfit = realizedPayout - position.TotalCost;
            position.Status = PaperPositionStatus.Closed;

            _openPositions.Remove(positionId);
            _closedPositions.Add(position);

            _basketCooldownUntil[position.PositionId] = DateTime.UtcNow.Add(_basketCooldown);

            AppendCsv(position);

            closedPosition = position;
            return true;
        }
    }

    public void PrintOpenPositions(int top = 10)
    {
        List<PaperPosition> positions;

        lock (_lock)
        {
            positions = _openPositions.Values
                .OrderByDescending(x => x.ExpectedProfit)
                .Take(top)
                .ToList();
        }

        Console.WriteLine();
        Console.WriteLine("========== OPEN PAPER POSITIONS ==========");

        if (positions.Count == 0)
        {
            Console.WriteLine("No open paper positions.");
            Console.WriteLine("==========================================");
            Console.WriteLine();
            return;
        }

        foreach (var p in positions)
        {
            Console.WriteLine("----------------------------------------");
            Console.WriteLine($"ID: {p.PositionId}");
            Console.WriteLine($"Strategy: {p.Strategy}");
            Console.WriteLine($"Group: {p.GroupKey}");
            Console.WriteLine($"Qty: {p.Quantity:0.####}");
            Console.WriteLine($"Cost: {p.TotalCost:0.####}");
            Console.WriteLine($"Guaranteed payout: {p.GuaranteedPayout:0.####}");
            Console.WriteLine($"Expected profit: {p.ExpectedProfit:0.####}");
            Console.WriteLine($"Edge/share: {p.EdgePerShare:0.####}");
            Console.WriteLine($"Legs: {p.Legs.Count}");
        }

        Console.WriteLine("==========================================");
        Console.WriteLine();
    }

    public void PrintSessionStatistics(
        OpportunityMonitor? monitor = null,
        decimal feeRatePerLeg = 0.001m,
        int topOpportunities = 5)
    {
        List<PaperPosition> openPositions;
        List<PaperPosition> closedPositions;

        lock (_lock)
        {
            openPositions = _openPositions.Values.ToList();
            closedPositions = _closedPositions.ToList();
        }

        var today = DateTime.UtcNow.Date;
        var todaysOpened = openPositions.Count(x => x.OpenedAtUtc.Date == today);
        var todaysClosed = closedPositions.Count(x => (x.ClosedAtUtc ?? DateTime.MinValue).Date == today);

        var sessionRealizedPnl = closedPositions.Sum(x => x.RealizedProfit ?? 0m);
        var dailyRealizedPnl = closedPositions
            .Where(x => (x.ClosedAtUtc ?? DateTime.MinValue).Date == today)
            .Sum(x => x.RealizedProfit ?? 0m);

        var winningClosed = closedPositions.Count(x => (x.RealizedProfit ?? 0m) > 0m);
        var winRate = closedPositions.Count == 0
            ? 0m
            : (decimal)winningClosed / closedPositions.Count * 100m;

        var totalFeesEstimate = (openPositions.Sum(x => x.TotalCost) + closedPositions.Sum(x => x.TotalCost))
            * feeRatePerLeg;

        var avgEdgeOpen = openPositions.Count == 0 ? 0m : openPositions.Average(x => x.EdgePerShare);
        var avgEdgeClosed = closedPositions.Count == 0 ? 0m : closedPositions.Average(x => x.EdgePerShare);

        Console.WriteLine();
        Console.WriteLine("========== DAILY / SESSION STATS ==========");
        Console.WriteLine($"Opened (session): {openPositions.Count}");
        Console.WriteLine($"Opened (today UTC): {todaysOpened}");
        Console.WriteLine($"Closed (session): {closedPositions.Count}");
        Console.WriteLine($"Closed (today UTC): {todaysClosed}");
        Console.WriteLine($"Realized PnL (session): {sessionRealizedPnl:0.####}");
        Console.WriteLine($"Realized PnL (today UTC): {dailyRealizedPnl:0.####}");
        Console.WriteLine($"Win rate (session): {winRate:0.##}%");
        Console.WriteLine($"Total fees est. (@{feeRatePerLeg * 100m:0.###}%): {totalFeesEstimate:0.####}");
        Console.WriteLine($"Average edge/share open: {avgEdgeOpen:0.####}");
        Console.WriteLine($"Average edge/share closed: {avgEdgeClosed:0.####}");

        if (monitor != null)
        {
            var best = monitor.GetTopCycleRecords(topOpportunities, executableOnly: true);
            Console.WriteLine();
            Console.WriteLine($"Top opportunities (cycle, executable, top {topOpportunities}):");

            if (best.Count == 0)
            {
                Console.WriteLine("- none");
            }
            else
            {
                foreach (var item in best)
                {
                    Console.WriteLine(
                        $"- {item.Engine}/{item.Strategy} | edge {item.EdgePerShare:0.####} | exp {item.ExpectedProfit:0.####} | qty {item.QuantityAvailable:0.####}"
                    );
                }
            }
        }

        Console.WriteLine("===========================================");
        Console.WriteLine();
    }

    private static string BuildPositionId(BasketArbOpportunity opportunity)
    {
        var legsKey = string.Join("_",
            opportunity.Legs
                .OrderBy(x => x.MarketId)
                .Select(x => $"{x.MarketId}-{x.Outcome}")
        );

        return $"{opportunity.Strategy}_{opportunity.GroupKey}_{legsKey}"
            .Replace(" ", "_")
            .Replace("|", "_")
            .Replace(":", "_");
    }

    private void EnsureCsvHeader()
    {
        var directory = Path.GetDirectoryName(_csvPath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(_csvPath))
            return;

        var header =
            "timestampUtc,positionId,status,engine,strategy,groupKey,quantity,totalCost,guaranteedPayout,edgePerShare,expectedProfit,realizedPayout,realizedProfit,legs";

        File.WriteAllText(_csvPath, header + Environment.NewLine);
    }

    private void AppendCsv(PaperPosition position)
    {
        File.AppendAllText(_csvPath, ToCsvLine(position) + Environment.NewLine);
    }

    private static string ToCsvLine(PaperPosition p)
    {
        var timestamp = p.Status == PaperPositionStatus.Open
            ? p.OpenedAtUtc
            : p.ClosedAtUtc ?? DateTime.UtcNow;

        var legs = string.Join(" ; ", p.Legs.Select(x =>
            $"{x.Outcome} @ {x.Price} x {x.Quantity} | {x.Question}"
        ));

        return string.Join(",",
            Csv(timestamp.ToString("O", CultureInfo.InvariantCulture)),
            Csv(p.PositionId),
            Csv(p.Status.ToString()),
            Csv(p.Engine),
            Csv(p.Strategy),
            Csv(p.GroupKey),
            Csv(p.Quantity),
            Csv(p.TotalCost),
            Csv(p.GuaranteedPayout),
            Csv(p.EdgePerShare),
            Csv(p.ExpectedProfit),
            Csv(p.RealizedPayout),
            Csv(p.RealizedProfit),
            Csv(legs)
        );
    }

    private static string Csv(decimal? value)
    {
        return Csv(value.HasValue
            ? value.Value.ToString("0.########", CultureInfo.InvariantCulture)
            : "");
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
}
