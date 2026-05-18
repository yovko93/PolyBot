using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace TradingBot.Models;

public class StringListJsonConverter : JsonConverter<List<string>>
{
    public override List<string> ReadJson(
        JsonReader reader,
        Type objectType,
        List<string>? existingValue,
        bool hasExistingValue,
        JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null)
            return new List<string>();

        if (reader.TokenType == JsonToken.StartArray)
        {
            var array = JArray.Load(reader);

            return array
                .Select(x => x?.ToString()?.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToList();
        }

        if (reader.TokenType == JsonToken.String)
        {
            var raw = reader.Value?.ToString();

            if (string.IsNullOrWhiteSpace(raw))
                return new List<string>();

            raw = raw.Trim();

            try
            {
                var parsed = JToken.Parse(raw);

                if (parsed.Type == JTokenType.Array)
                {
                    return parsed
                        .Children()
                        .Select(x => x?.ToString()?.Trim())
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x!)
                        .ToList();
                }
            }
            catch
            {
                // Не е JSON array string. Продължаваме със split fallback.
            }

            return raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim().Trim('"', '[', ']'))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }

        var token = JToken.Load(reader);

        return token
            .Children()
            .Select(x => x?.ToString()?.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();
    }

    public override void WriteJson(
        JsonWriter writer,
        List<string>? value,
        JsonSerializer serializer)
    {
        serializer.Serialize(writer, value ?? new List<string>());
    }
}