using System.Text.Json;
using System.Text.RegularExpressions;
using AlmChatBot.Api.Models;

namespace AlmChatBot.Api.Services;

public static class AlmLlmSqlParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex SqlFieldRx = new(
        "\"Sql\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    public static bool TryParse(string content, out LlmSqlResponse response)
    {
        try
        {
            response = Parse(content);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            response = new LlmSqlResponse();
            return false;
        }
    }

    public static LlmSqlResponse Parse(string content)
    {
        var json = ExtractJsonObject(content);
        try
        {
            var parsed = JsonSerializer.Deserialize<LlmSqlResponse>(json, JsonOptions);
            if (parsed is not null && !string.IsNullOrWhiteSpace(parsed.Sql))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
            // Gemma sıklıkla Explanation stringini keser; Sql alanı duruyorsa onu kurtar.
        }

        var sql = ReadSqlField(json) ?? ReadSqlField(content);
        if (string.IsNullOrWhiteSpace(sql))
        {
            throw new InvalidOperationException("LLM SQL JSON yanıtı boş veya kesik.");
        }

        return new LlmSqlResponse
        {
            Sql = sql,
            Explanation = "SQL kesik JSON'dan kurtarıldı.",
            Assumptions = ["Model JSON'u tam kapatmadı; Explanation yok sayıldı."]
        };
    }

    public static string ExtractJsonObject(string content)
    {
        var trimmed = StripFence(StripThink(content));
        var start = trimmed.IndexOf('{');
        if (start < 0)
        {
            return trimmed;
        }

        var end = trimmed.LastIndexOf('}');
        return end > start ? trimmed[start..(end + 1)] : trimmed[start..];
    }

    private static string? ReadSqlField(string text)
    {
        var match = SqlFieldRx.Match(text);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<string>("\"" + match.Groups[1].Value + "\"");
        }
        catch (JsonException)
        {
            return Regex.Unescape(match.Groups[1].Value);
        }
    }

    private static string StripThink(string content)
    {
        var stripped = Regex.Replace(
            content,
            "<think>[\\s\\S]*?</think>",
            string.Empty,
            RegexOptions.IgnoreCase);
        return stripped.Trim();
    }

    private static string StripFence(string content)
    {
        var trimmed = content.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNl = trimmed.IndexOf('\n');
        if (firstNl < 0)
        {
            return trimmed;
        }

        trimmed = trimmed[(firstNl + 1)..];
        var fence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return fence >= 0 ? trimmed[..fence].Trim() : trimmed.Trim();
    }
}
