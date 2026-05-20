namespace TradingBot.Models;

public record BookQuote(
    decimal Price,
    decimal Size
);

public record BinaryOrderBookSnapshot(
    string MarketId,
    string Question,
    string YesTokenId,
    string NoTokenId,
    BookQuote? YesBid,
    BookQuote? YesAsk,
    BookQuote? NoBid,
    BookQuote? NoAsk
);