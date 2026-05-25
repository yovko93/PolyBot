using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TradingBot.Models;

public class Market
{
    [JsonProperty("id")]
    public string id { get; set; } = "";

    [JsonProperty("question")]
    public string question { get; set; } = "";

    [JsonProperty("outcomes")]
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string> outcomes { get; set; } = new();

    [JsonProperty("clobTokenIds")]
    [JsonConverter(typeof(StringListJsonConverter))]
    public List<string> clobTokenIds { get; set; } = new();

    [JsonProperty("outcomePrices")]
    public JToken? outcomePrices { get; set; }
    [JsonProperty("conditionId")]
    public string? conditionId { get; set; }
    [JsonProperty("active")]
    public bool? active { get; set; }
    [JsonProperty("closed")]
    public bool? closed { get; set; }
    [JsonProperty("archived")]
    public bool? archived { get; set; }
    [JsonProperty("accepting_orders")]
    public bool? accepting_orders { get; set; }
    [JsonProperty("acceptingOrders")]
    public bool? acceptingOrders { get; set; }
    [JsonProperty("enableOrderBook")]
    public bool? enableOrderBook { get; set; }
    [JsonProperty("endDate")]
    public string? endDate { get; set; }
    [JsonProperty("endDateIso")]
    public string? endDateIso { get; set; }
    [JsonProperty("liquidity")]
    public decimal? liquidity { get; set; }
    [JsonProperty("volume24hr")]
    public decimal? volume24hr { get; set; }
    [JsonIgnore] public decimal liquidityNum => liquidity ?? 0m;
    [JsonIgnore] public decimal volume24hrNum => volume24hr ?? 0m;

    public List<Outcome> GetParsedOutcomes()
    {
        var result = new List<Outcome>();

        // outcomes вече е List<string>, не JToken
        var names = outcomes ?? new List<string>();

        // outcomePrices още може да е JToken/stringified JSON array
        var prices = ParseToken(outcomePrices);

        for (int i = 0; i < names.Count && i < prices.Count; i++)
        {
            if (decimal.TryParse(
                    prices[i],
                    NumberStyles.Any,
                    CultureInfo.InvariantCulture,
                    out var price))
            {
                result.Add(new Outcome
                {
                    Name = names[i],
                    Price = price
                });
            }
        }

        return result;
    }

    private static List<string> ParseToken(JToken? token)
    {
        var list = new List<string>();

        if (token == null)
            return list;

        // CASE 1: нормален JSON array
        if (token.Type == JTokenType.Array)
        {
            return token
                .Select(x => x.ToString().Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        // CASE 2: string, който съдържа JSON array
        if (token.Type == JTokenType.String)
        {
            var str = token.ToString().Trim();

            if (string.IsNullOrWhiteSpace(str))
                return list;

            if (str.StartsWith("["))
            {
                try
                {
                    var arr = JArray.Parse(str);

                    return arr
                        .Select(x => x.ToString().Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .ToList();
                }
                catch
                {
                    // fallback към CSV parsing
                }
            }

            return str
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().Trim('"', '[', ']'))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        return list;
    }
}

public class Outcome
{
    public string Name { get; set; } = "";
    public decimal Price { get; set; }
}
