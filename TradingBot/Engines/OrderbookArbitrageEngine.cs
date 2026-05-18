using TradingBot.Models;

namespace TradingBot.Engines;

public class OrderbookArbitrageEngine
{
    public bool TryFindArb(OrderBook book, out decimal profit)
    {
        profit = 0;

        if (book?.data == null || book.data.Count < 2)
            return false;

        var yes = book.data[0];
        var no = book.data[1];

        if (!decimal.TryParse(yes.best_ask, out var yesAsk)) return false;
        if (!decimal.TryParse(no.best_ask, out var noAsk)) return false;

        var sum = yesAsk + noAsk;

        if (sum < 1m)
        {
            profit = 1m - sum;
            return true;
        }

        return false;
    }
}