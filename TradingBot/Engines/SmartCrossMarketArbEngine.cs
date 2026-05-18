using TradingBot.Models;
using TradingBot.Services;

namespace TradingBot.Engines;

public class SmartCrossMarketArbEngine
{
    private readonly TextSimilarityService _similarity = new();

    public void Scan(List<Market> markets, PaperTradingEngine paper)
    {
        var parsed = markets
            .Select(m => new
            {
                Market = m,
                Outcomes = m.GetParsedOutcomes()
            })
            .Where(x => x.Outcomes.Count >= 2)
            .ToList();

        for (int i = 0; i < parsed.Count; i++)
        {
            for (int j = i + 1; j < parsed.Count; j++)
            {
                var m1 = parsed[i];
                var m2 = parsed[j];

                var sim = _similarity.JaccardSimilarity(
                    m1.Market.question,
                    m2.Market.question
                );

                // 👉 филтър: само сходни пазари
                if (sim < 0.4)
                    continue;

                var yes1 = m1.Outcomes.First(o => o.Name == "Yes");
                var yes2 = m2.Outcomes.First(o => o.Name == "Yes");

                // 👉 opposite heuristic
                if (!AreOpposites(m1.Market.question, m2.Market.question))
                    continue;

                var sum = yes1.Price + yes2.Price;

                if (sum < 1m)
                {
                    var profit = 1m - sum;

                    Console.WriteLine("🔥 SMART CROSS ARB");
                    Console.WriteLine($"SIM: {sim:F2}");
                    Console.WriteLine(m1.Market.question);
                    Console.WriteLine(m2.Market.question);
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

    private bool AreOpposites(string q1, string q2)
    {
        var negatives = new[] { "not", "no", "lose", "fail", "below" };

        var hasNegative1 = negatives.Any(n => q1.ToLower().Contains(n));
        var hasNegative2 = negatives.Any(n => q2.ToLower().Contains(n));

        // 👉 единият позитивен, другият негативен
        return hasNegative1 != hasNegative2;
    }
}