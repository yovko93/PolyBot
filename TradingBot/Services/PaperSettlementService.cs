using TradingBot.Models;
using TradingBot.Engines;

namespace TradingBot.Services;

public class PaperSettlementService
{
    private readonly PaperTradingEngine _paper;
    private readonly PaperPositionBook _positionBook;

    public PaperSettlementService(
        PaperTradingEngine paper,
        PaperPositionBook positionBook)
    {
        _paper = paper;
        _positionBook = positionBook;
    }

    public bool SettlePositionAtGuaranteedPayout(string positionId)
    {
        var position = _positionBook.OpenPositions
            .FirstOrDefault(x => x.PositionId == positionId);

        if (position == null)
        {
            Console.WriteLine($"[SETTLEMENT SKIP] Open position not found. ID={positionId}");
            return false;
        }

        return _paper.SettlePosition(
            position.PositionId,
            realizedPayout: position.GuaranteedPayout
        );
    }

    public bool SettlePositionWithCustomPayout(
        string positionId,
        decimal realizedPayout)
    {
        var position = _positionBook.OpenPositions
            .FirstOrDefault(x => x.PositionId == positionId);

        if (position == null)
        {
            Console.WriteLine($"[SETTLEMENT SKIP] Open position not found. ID={positionId}");
            return false;
        }

        return _paper.SettlePosition(
            position.PositionId,
            realizedPayout
        );
    }

    public int SettleGroupAtGuaranteedPayout(string groupKey)
    {
        var positions = _positionBook.OpenPositions
            .Where(x => x.GroupKey == groupKey)
            .ToList();

        if (positions.Count == 0)
        {
            Console.WriteLine($"[SETTLEMENT SKIP] No open positions for group: {groupKey}");
            return 0;
        }

        var settled = 0;

        foreach (var position in positions)
        {
            var ok = _paper.SettlePosition(
                position.PositionId,
                realizedPayout: position.GuaranteedPayout
            );

            if (ok)
                settled++;
        }

        Console.WriteLine(
            $"[SETTLEMENT GROUP DONE] Group={groupKey}, Settled={settled}/{positions.Count}"
        );

        return settled;
    }

    public int SettleAllOpenAtGuaranteedPayout()
    {
        var positions = _positionBook.OpenPositions.ToList();

        if (positions.Count == 0)
        {
            Console.WriteLine("[SETTLEMENT SKIP] No open paper positions.");
            return 0;
        }

        var settled = 0;

        foreach (var position in positions)
        {
            var ok = _paper.SettlePosition(
                position.PositionId,
                realizedPayout: position.GuaranteedPayout
            );

            if (ok)
                settled++;
        }

        Console.WriteLine(
            $"[SETTLEMENT ALL DONE] Settled={settled}/{positions.Count}"
        );

        return settled;
    }

    public void PrintSettlementCandidates()
    {
        var positions = _positionBook.OpenPositions
            .OrderByDescending(x => x.ExpectedProfit)
            .ToList();

        Console.WriteLine();
        Console.WriteLine("========== SETTLEMENT CANDIDATES ==========");

        if (positions.Count == 0)
        {
            Console.WriteLine("No open positions to settle.");
            Console.WriteLine("===========================================");
            Console.WriteLine();
            return;
        }

        foreach (var p in positions)
        {
            Console.WriteLine("----------------------------------------");
            Console.WriteLine($"ID: {p.PositionId}");
            Console.WriteLine($"Group: {p.GroupKey}");
            Console.WriteLine($"Strategy: {p.Strategy}");
            Console.WriteLine($"Qty: {p.Quantity:0.####}");
            Console.WriteLine($"Cost: {p.TotalCost:0.####}");
            Console.WriteLine($"Guaranteed payout: {p.GuaranteedPayout:0.####}");
            Console.WriteLine($"Expected profit: {p.ExpectedProfit:0.####}");
        }

        Console.WriteLine("===========================================");
        Console.WriteLine();
    }
}