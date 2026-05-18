using TradingBot.Models;

namespace TradingBot.Engines;

public class SimulatedResolutionEngine
{
    private readonly TimeSpan _positionLifetime;

    public SimulatedResolutionEngine(TimeSpan? positionLifetime = null)
    {
        // За тест: всяка paper позиция се "разрешава" след 2 минути.
        _positionLifetime = positionLifetime ?? TimeSpan.FromMinutes(2);
    }

    public void Scan(PaperTradingEngine paper)
    {
        var now = DateTime.UtcNow;
        var openPositions = paper.GetOpenPositions();

        foreach (var position in openPositions)
        {
            var age = now - position.OpenedAtUtc;

            if (age < _positionLifetime)
                continue;

            var realizedPayout = CalculateSimulatedPayout(position);

            paper.SettlePosition(position.PositionId, realizedPayout);
        }
    }

    private decimal CalculateSimulatedPayout(PaperPosition position)
    {
        // За paper arbitrage симулация:
        // приемаме, че позицията се затваря на гарантирания payout.
        return position.TotalCost + position.ExpectedProfit;
    }
}