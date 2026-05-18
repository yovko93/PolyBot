using TradingBot.Models;

namespace TradingBot.Engines;

public class ArbitrageEngine
{
    public bool TryFindArbitrage(Market market, out decimal profit)
    {
        profit = 0;

        var parsed = market.GetParsedOutcomes();

        if (parsed.Count < 2)
            return false;

        var yes = parsed.FirstOrDefault(o => o.Name == "Yes");
        var no = parsed.FirstOrDefault(o => o.Name == "No");

        if (yes == null || no == null)
            return false;

        var sum = yes.Price + no.Price;

        if (sum < 1m)
        {
            profit = 1m - sum;
            return true;
        }

        return false;
    }
}