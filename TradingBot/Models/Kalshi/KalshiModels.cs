using System.Text.Json.Serialization;

namespace TradingBot.Models.Kalshi;

public record KalshiMarketResponse([property: JsonPropertyName("markets")] List<KalshiMarket> Markets);
public record KalshiMarket([property: JsonPropertyName("ticker")] string Ticker, [property: JsonPropertyName("title")] string Title, [property: JsonPropertyName("status")] string Status);
public record KalshiOrderbookResponse([property: JsonPropertyName("orderbook")] KalshiOrderbook Orderbook);
public record KalshiOrderbook([property: JsonPropertyName("yes") ] List<KalshiLevel>? Yes, [property: JsonPropertyName("no")] List<KalshiLevel>? No);
public record KalshiLevel([property: JsonPropertyName("price")] decimal Price, [property: JsonPropertyName("quantity")] decimal Quantity);
