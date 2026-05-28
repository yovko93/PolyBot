using TradingBot.Engines;
using TradingBot.Models;
using TradingBot.Services;
using Xunit;

namespace TradingBot.Tests;

public class PaperBasketAccountingTests
{
    [Fact]
    public void RegisterExternalBasketOpen_UpdatesCashLockedAndEquity_AndIsIdempotent()
    {
        var book = new PaperPositionBook(Path.GetTempFileName());
        var engine = new PaperTradingEngine(positionBook: book);
        var opp = new BasketArbOpportunity(
            "winner:2026 colombian presidential election|kind:person",
            "BUY_ALL_NO_MUTUALLY_EXCLUSIVE",
            new List<BasketArbLeg>
            {
                new("m1","t1","q1","NO",0.5m,200m),
                new("m2","t2","q2","NO",0.7m,200m),
                new("m3","t3","q3","NO",0.789m,200m)
            },
            125.69130216189039718451483157m,
            1.989m,
            1m,
            0.0055m,
            0.6913021618903971845148315736m);

        var position = book.AddBasketPosition(opp, opp.Quantity, 250m, 0.6913021618903971845148315736m, "VerifiedMultiOutcome");
        Assert.NotNull(position);

        var applied = engine.RegisterExternalBasketOpen(position!, 250m, 0.6913021618903971845148315736m);
        Assert.True(applied);
        Assert.Equal(750m, engine.Balance);
        Assert.Equal(250m, engine.LockedCapital);
        Assert.Equal(1000m, engine.Equity);
        Assert.Equal(0m, engine.RealizedPnl);
        Assert.Single(book.GetOpenPositions());

        var duplicateApply = engine.RegisterExternalBasketOpen(position!, 250m, 0.6913021618903971845148315736m);
        Assert.False(duplicateApply);
        Assert.Equal(750m, engine.Balance);
        Assert.Equal(250m, engine.LockedCapital);
    }
}
