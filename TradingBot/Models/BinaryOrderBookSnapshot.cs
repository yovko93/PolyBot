namespace TradingBot.Models;

public record BookQuote(
    decimal Price,
    decimal Size
);

public record BinaryOrderBookSnapshot(
    string MarketId,
    string Question,
    BookQuote? YesAsk,
    BookQuote? NoAsk,
    BookQuote? YesBid,
    BookQuote? NoBid
);