using TradingBot.Models.Normalized;
using TradingBot.Options;

namespace TradingBot.Services.CrossExchange;

public class CrossExchangeArbitrageEngine(CrossExchangeOptions options, ExchangeFeesOptions fees)
{
    public IEnumerable<CrossExchangeArbitrageOpportunity> Evaluate(CrossExchangeMarketPair pair, ExchangeOrderbook poly, ExchangeOrderbook kalshi, int openPositions)
    {
        if (!options.Enabled) yield break;
        if (!pair.Enabled || (options.UseOnlyVerifiedMarketPairs && pair.RiskLevel != MarketPairRiskLevel.Verified)) yield break;
        if ((DateTime.UtcNow - poly.TimestampUtc).TotalMilliseconds > options.MaxOrderbookAgeMs || (DateTime.UtcNow - kalshi.TimestampUtc).TotalMilliseconds > options.MaxOrderbookAgeMs) yield break;
        if (openPositions >= options.MaxOpenCrossExchangePositions) yield break;

        foreach (var opp in Build(pair.CanonicalKey, "KALSHI", kalshi.BestYesAsk, kalshi.YesAskQuantity, "YES", "POLYMARKET", poly.BestNoAsk, poly.NoAskQuantity, "NO")) yield return opp;
        foreach (var opp in Build(pair.CanonicalKey, "KALSHI", kalshi.BestNoAsk, kalshi.NoAskQuantity, "NO", "POLYMARKET", poly.BestYesAsk, poly.YesAskQuantity, "YES")) yield return opp;
    }

    private IEnumerable<CrossExchangeArbitrageOpportunity> Build(string key,string ex1, decimal? p1, decimal q1, string s1, string ex2, decimal? p2, decimal q2, string s2)
    {
        if (!p1.HasValue || !p2.HasValue) yield break;
        var qty = Math.Min(q1, q2);
        if (qty <= 0) yield break;
        var gross = p1.Value + p2.Value;
        var estFees = Fee(ex1, p1.Value, qty) + Fee(ex2, p2.Value, qty);
        var slippage = options.SlippageBufferPerShare;
        var net = gross + estFees/Math.Max(qty,1) + slippage;
        var edge = 1m - net;
        var expected = edge * qty;
        var executable = net < 1m && edge >= options.MinCrossExchangeEdge && expected >= options.MinCrossExchangeExpectedProfit && gross * qty <= options.MaxNotionalPerOpportunity;
        yield return new CrossExchangeArbitrageOpportunity(key, "CROSS_EXCHANGE_KALSHI_POLYMARKET", ex1, s1, p1.Value, ex2, s2, p2.Value, gross, estFees, slippage, net, 1m, edge, qty, expected, executable, executable ? "" : "THRESHOLD_OR_RISK", DateTime.UtcNow);
    }

    private decimal Fee(string ex, decimal price, decimal qty)
    {
        var m = ex == "KALSHI" ? fees.Kalshi : fees.Polymarket;
        if (!m.Enabled) return 0;
        return m.FixedFee + qty*m.FeePerShare + (price*qty*m.PercentageFee);
    }
}
