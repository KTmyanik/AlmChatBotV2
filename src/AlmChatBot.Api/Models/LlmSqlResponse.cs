using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlmChatBot.Api.Models;

public sealed class LlmSqlResponse
{
    public string Sql { get; set; } = string.Empty;

    public string Explanation { get; set; } = string.Empty;

    [JsonConverter(typeof(StringOrListConverter))]
    public List<string> Assumptions { get; set; } = [];
}

internal sealed class StringOrListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            return string.IsNullOrWhiteSpace(value) ? [] : [value];
        }

        if (reader.TokenType == JsonTokenType.StartArray)
        {
            var list = new List<string>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                {
                    var item = reader.GetString();
                    if (!string.IsNullOrWhiteSpace(item))
                    {
                        list.Add(item);
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            return list;
        }

        reader.Skip();
        return [];
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var item in value)
        {
            writer.WriteStringValue(item);
        }

        writer.WriteEndArray();
    }
}
