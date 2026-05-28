using TradingBot.Models;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class PaperPositionMtmTests
{
    [Fact]
    public void NewBasketPosition_DefaultsToIncompleteMtm()
    {
        var book = new PaperPositionBook(Path.GetTempFileName());
        var opp = new BasketArbOpportunity(
            "g1",
            "BUY_ALL_NO_MUTUALLY_EXCLUSIVE",
            new List<BasketArbLeg>
            {
                new("m1","t1","q1","NO",0.2m,10m),
                new("m2","t2","q2","NO",0.3m,10m)
            },
            10m,
            0.5m,
            1m,
            0.01m,
            0.1m);

        var p = book.AddBasketPosition(opp, 10m, 5m, 0.1m, "VerifiedMultiOutcome");

        Assert.NotNull(p);
        Assert.Equal("Incomplete", p!.MtmStatus);
        Assert.Equal(2, p.MissingExitPrices);
        Assert.Null(p.CurrentExitValue);
    }
}
