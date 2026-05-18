using System.Globalization;
using System.Text;
using TradingBot.Models;

namespace TradingBot.Services;

public class ExecutionJournal
{
    private readonly object _lock = new();
    private readonly string _csvPath;

    public ExecutionJournal(string csvPath = "data/execution-journal.csv")
    {
        _csvPath = csvPath;
        EnsureCsvHeader();
    }

    public void Record(ExecutionJournalRecord record)
    {
        lock (_lock)
        {
            File.AppendAllText(_csvPath, ToCsvLine(record) + Environment.NewLine);
        }
    }

    private void EnsureCsvHeader()
    {
        var directory = Path.GetDirectoryName(_csvPath);

        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        if (File.Exists(_csvPath))
            return;

        var header =
            "timestampUtc,mode,engine,strategy,key,quantity,totalCost,guaranteedPayout,edgePerShare,expectedProfit,balanceAfter,lockedCapitalAfter,equityAfter,status,legs";

        File.WriteAllText(_csvPath, header + Environment.NewLine);
    }

    private static string ToCsvLine(ExecutionJournalRecord row)
    {
        return string.Join(",",
            Csv(row.TimestampUtc.ToString("O", CultureInfo.InvariantCulture)),
            Csv(row.Mode),
            Csv(row.Engine),
            Csv(row.Strategy),
            Csv(row.Key),
            Csv(row.Quantity),
            Csv(row.TotalCost),
            Csv(row.GuaranteedPayout),
            Csv(row.EdgePerShare),
            Csv(row.ExpectedProfit),
            Csv(row.BalanceAfter),
            Csv(row.LockedCapitalAfter),
            Csv(row.EquityAfter),
            Csv(row.Status),
            Csv(row.Legs)
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
}