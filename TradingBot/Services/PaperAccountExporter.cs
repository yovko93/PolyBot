using System.Text.Json;
using TradingBot.Engines;
using TradingBot.Models;

namespace TradingBot.Services;

public sealed record PaperAccountStatus(
    decimal Cash,
    decimal Locked,
    decimal Equity,
    decimal RealizedPnl,
    int OpenPositions,
    int ClosedPositions,
    decimal TotalExposure,
    IReadOnlyDictionary<string, int> PositionsByStrategy,
    int HourlyOpenCount,
    DateTime? LastOpenAt,
    IReadOnlyDictionary<string, int> BlockedCountsByReason);

public static class PaperAccountExporter
{
    public static PaperAccountStatus BuildAccount(PaperTradingEngine paper, PaperPositionBook book, IReadOnlyDictionary<string, int>? blockedCounts = null)
    {
        var open = book.GetOpenPositions();
        return new PaperAccountStatus(
            paper.Balance,
            paper.LockedCapital,
            paper.Equity,
            paper.RealizedPnl,
            open.Count,
            book.ClosedPositions.Count,
            open.Sum(p => p.TotalCost),
            open.GroupBy(p => p.Strategy, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
            open.Count(p => p.OpenedAtUtc >= DateTime.UtcNow.AddHours(-1)),
            open.Count == 0 ? null : open.Max(p => p.OpenedAtUtc),
            blockedCounts ?? new Dictionary<string, int>());
    }

    public static void ExportLatest(string exportsRoot, PaperTradingEngine paper, PaperPositionBook book, IEnumerable<object>? executions = null, IReadOnlyDictionary<string, int>? blockedCounts = null)
    {
        Directory.CreateDirectory(exportsRoot);
        var jsonOptions = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(exportsRoot, "paper-account-latest.json"), JsonSerializer.Serialize(BuildAccount(paper, book, blockedCounts), jsonOptions));
        File.WriteAllText(Path.Combine(exportsRoot, "paper-positions-latest.json"), JsonSerializer.Serialize(book.OpenPositions.Concat(book.ClosedPositions).Select(ToDto), jsonOptions));
        File.WriteAllText(Path.Combine(exportsRoot, "paper-executions-latest.json"), JsonSerializer.Serialize(executions ?? Array.Empty<object>(), jsonOptions));
        File.WriteAllText(Path.Combine(exportsRoot, "paper-settlements-latest.json"), JsonSerializer.Serialize(book.Settlements, jsonOptions));
    }

    public static object ToDto(PaperPosition p) => new
    {
        p.PositionId,
        p.GroupKey,
        p.Strategy,
        p.Engine,
        p.OpenedAtUtc,
        status = p.Status.ToString(),
        p.Quantity,
        p.TotalCost,
        p.CostPerBasket,
        p.GuaranteedPayout,
        p.ExpectedProfit,
        p.LockedCapital,
        p.RealizedPayout,
        p.RealizedProfit,
        p.ClosedAtUtc,
        p.OpenedFromSimulatedFills,
        p.FillSimulationId,
        legs = p.Legs
    };
}
