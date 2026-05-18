using TradingBot.Models;

namespace TradingBot.Engines;

public class CrossMarketArbitrageEngine
{
    public void Scan(List<Market> markets, PaperTradingEngine paper)
    {
        var parsedMarkets = markets
            .Select(m => new
            {
                Market = m,
                Outcomes = m.GetParsedOutcomes()
            })
            .Where(x => x.Outcomes.Count >= 2)
            .ToList();

        for (int i = 0; i < parsedMarkets.Count; i++)
        {
            for (int j = i + 1; j < parsedMarkets.Count; j++)
            {
                var m1 = parsedMarkets[i];
                var m2 = parsedMarkets[j];

                var yes1 = m1.Outcomes.FirstOrDefault(o => o.Name == "Yes");
                var yes2 = m2.Outcomes.FirstOrDefault(o => o.Name == "Yes");

                if (yes1 == null || yes2 == null)
                    continue;

                var sum = yes1.Price + yes2.Price;

                if (sum < 1m)
                {
                    var profit = 1m - sum;

                    Console.WriteLine("🔥 CROSS ARB FOUND");
                    Console.WriteLine($"{m1.Market.question}");
                    Console.WriteLine($"{m2.Market.question}");
                    Console.WriteLine($"SUM: {sum} | PROFIT: {profit}");

                    paper.ExecuteCrossArb(
                        m1.Market.id,
                        m2.Market.id,
                        yes1.Price,
                        yes2.Price
                    );
                }
            }
        }
    }
}