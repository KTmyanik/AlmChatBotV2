using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlmChatBot.Api.Configuration;
using AlmChatBot.Api.Models;
using Microsoft.Extensions.Options;

namespace AlmChatBot.Api.Services;

public interface IQwenClientService
{
    Task<LlmSqlResponse> GenerateSqlAsync(
        string question,
        string? previousSql,
        string? validationError,
        CancellationToken cancellationToken);

    Task<string> GenerateInsightAsync(
        string question,
        string sql,
        IReadOnlyList<IDictionary<string, object?>> previewRows,
        int totalRowCount,
        CancellationToken cancellationToken);

    Task<string> PolishAnalysisAsync(string question, string draft, CancellationToken cancellationToken);

    bool UsesLocalOllama { get; }
}

public sealed class QwenClientService(
    HttpClient httpClient,
    IOptions<QwenConfig> options,
    IWebHostEnvironment environment,
    ILogger<QwenClientService> logger) : IQwenClientService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly QwenConfig _config = options.Value;
    private readonly string _dictionary = LoadDictionary(environment);

    public async Task<LlmSqlResponse> GenerateSqlAsync(
        string question,
        string? previousSql,
        string? validationError,
        CancellationToken cancellationToken)
    {
        var system = SqlSystemPrompt();

        var user = string.IsNullOrWhiteSpace(validationError)
            ? $"Kullanıcı sorusu:\n{question}"
            : $"""
                Kullanıcı sorusu:
                {question}

                Önceki SQL reddedildi:
                {previousSql}

                Hata:
                {validationError}

                Kurallara uyan yeni bir SELECT üret.
                """;

        var content = await CompleteAsync(system, user, jsonObject: true, cancellationToken);
        var parsed = JsonSerializer.Deserialize<LlmSqlResponse>(ExtractJsonObject(content), JsonOptions)
                     ?? throw new InvalidOperationException("Qwen SQL JSON yanıtı boş.");

        if (string.IsNullOrWhiteSpace(parsed.Sql))
        {
            throw new InvalidOperationException("Qwen SQL alanı boş döndü.");
        }

        return parsed;
    }

    public async Task<string> GenerateInsightAsync(
        string question,
        string sql,
        IReadOnlyList<IDictionary<string, object?>> previewRows,
        int totalRowCount,
        CancellationToken cancellationToken)
    {
        var previewJson = JsonSerializer.Serialize(previewRows, JsonOptions);
        var system = """
            Sen bir ALM / hazine analistisin. Verilen tabloyu Türkçe yorumla.
            Yalnızca bu satırlardaki rakamları kullan; yeni sayı uydurma.
            Tarih, döviz, havuz ve yaklaşım kırılımını belirt. Sıfır veya negatif bakiyeyi açıkça söyle.
            4-8 cümle, düz metin. JSON yazma.
            """;

        var user = $"""
            Soru: {question}
            Çalıştırılan SQL: {sql}
            Toplam satır: {totalRowCount}
            İlk satırlar (en fazla 10): {previewJson}
            """;

        return await CompleteAsync(system, user, jsonObject: false, cancellationToken);
    }

    public Task<string> PolishAnalysisAsync(string question, string draft, CancellationToken cancellationToken)
    {
        var system = """
            Sen kıdemli bir ALM analistisin. Yalnızca Türkçe yaz.
            Verilen taslağı daha akıcı hale getir. Yeni rakam, yeni kalem veya yeni oran ekleme.
            Çince, Endonezce veya İngilizce cümle kurma. Sayıları taslaktaki haliyle bırak.
            """;
        var user = $"Soru: {question}\n\nTaslak:\n{draft}";
        return CompleteAsync(system, user, jsonObject: false, cancellationToken);
    }

    private async Task<string> CompleteAsync(
        string system,
        string user,
        bool jsonObject,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            throw new InvalidOperationException(
                "QwenConfig.ApiKey tanımlı değil. User Secrets veya ortam değişkeni kullanın.");
        }

        var payload = BuildPayload(system, user, jsonObject);
        logger.LogInformation("Qwen çağrısı başladı. Model={Model} NativeOllama={Ollama} Json={Json}",
            _config.ModelName, UseOllamaNative, jsonObject);
        var started = System.Diagnostics.Stopwatch.StartNew();
        var body = await SendChatAsync(payload, jsonObject, cancellationToken);
        logger.LogInformation("Qwen HTTP {Status} {Elapsed}ms", body.Status, started.ElapsedMilliseconds);

        if (jsonObject && IsClientError(body.Status))
        {
            logger.LogWarning("JSON mode desteklenmedi, düz sohbet ile yeniden denenecek: {Body}", TrimForLog(body.Text));
            body = await SendChatAsync(BuildPayload(system, user, jsonObject: false), jsonObject: false, cancellationToken);
        }

        if (body.Status is < 200 or >= 300)
        {
            logger.LogError("Qwen HTTP {Status}: {Body}", body.Status, body.Text);
            throw new HttpRequestException($"Qwen API {body.Status}: {TrimForLog(body.Text)}");
        }

        using var doc = JsonDocument.Parse(body.Text);
        var content = ReadChatContent(doc);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Qwen boş içerik döndü.");
        }

        return StripThink(content);
    }

    private bool UseOllamaNative =>
        _config.BaseUrl.Contains("11434", StringComparison.OrdinalIgnoreCase)
        || _config.BaseUrl.Contains("ollama", StringComparison.OrdinalIgnoreCase);

    public bool UsesLocalOllama => UseOllamaNative;

    private static string? ReadChatContent(JsonDocument doc)
    {
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            return ReadMessageText(choices[0].GetProperty("message"));
        }

        if (root.TryGetProperty("message", out var message))
        {
            return ReadMessageText(message);
        }

        return null;
    }

    private static string? ReadMessageText(JsonElement message)
    {
        foreach (var name in new[] { "content", "reasoning", "reasoning_content" })
        {
            if (message.TryGetProperty(name, out var el))
            {
                var text = el.ValueKind == JsonValueKind.String ? el.GetString() : null;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return null;
    }

    private Dictionary<string, object?> BuildPayload(string system, string user, bool jsonObject)
    {
        if (UseOllamaNative)
        {
            var native = new Dictionary<string, object?>
            {
                ["model"] = _config.ModelName,
                ["stream"] = true,
                ["think"] = false,
                ["keep_alive"] = "10m",
                ["messages"] = new object[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user }
                },
                ["options"] = new { temperature = 0, num_ctx = 4096, num_predict = 384 }
            };

            if (jsonObject)
            {
                native["format"] = "json";
            }

            return native;
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _config.ModelName,
            ["temperature"] = 0,
            ["think"] = false,
            ["reasoning_effort"] = "none",
            ["chat_template_kwargs"] = new { enable_thinking = false },
            ["messages"] = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            }
        };

        if (jsonObject)
        {
            payload["response_format"] = new { type = "json_object" };
        }

        return payload;
    }

    private Uri ResolveEndpoint()
    {
        if (!UseOllamaNative)
        {
            return new Uri("chat/completions", UriKind.Relative);
        }

        var root = _config.BaseUrl.TrimEnd('/');
        if (root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
        {
            root = root[..^3];
        }

        return new Uri(root.TrimEnd('/') + "/api/chat");
    }

    private async Task<(int Status, string Text)> SendChatAsync(
        Dictionary<string, object?> payload,
        bool jsonObject,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ResolveEndpoint())
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(_config.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.ApiKey);
        }

        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            return ((int)response.StatusCode, error);
        }

        if (UseOllamaNative)
        {
            var assembled = await ReadOllamaStreamAsync(response, jsonObject, cancellationToken);
            return ((int)response.StatusCode, assembled);
        }

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return ((int)response.StatusCode, text);
    }

    private async Task<string> ReadOllamaStreamAsync(
        HttpResponseMessage response,
        bool jsonObject,
        CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                return JsonSerializer.Serialize(new { error = error.GetString() });
            }

            if (doc.RootElement.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var chunk)
                && chunk.ValueKind == JsonValueKind.String)
            {
                content.Append(chunk.GetString());
            }

            if (jsonObject && HasCompleteSqlJson(content.ToString()))
            {
                logger.LogInformation("Ollama JSON tamamlandı, akış kesildi. Karakter={Length}", content.Length);
                break;
            }

            if (doc.RootElement.TryGetProperty("done", out var done) && done.GetBoolean())
            {
                break;
            }
        }

        return JsonSerializer.Serialize(new { message = new { content = content.ToString() } });
    }

    private bool HasCompleteSqlJson(string content)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<LlmSqlResponse>(ExtractJsonObject(content), JsonOptions);
            return parsed is not null && !string.IsNullOrWhiteSpace(parsed.Sql);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private string SqlSystemPrompt()
    {
        if (UseOllamaNative)
        {
            return """
                Sen ALM Text-to-SQL üreticisisin. Yalnızca Microsoft SQL Server T-SQL SELECT yaz.
                Yanıt tek JSON: {"Sql":"...","Explanation":"...","Assumptions":["..."]}
                Markdown veya kod çiti yok.

                Şema [ALM]: InternalReports ir, InternalDurationReports dr, InternalReportMap map, CoreDepositRates cd.
                Join yalnızca ir.ALMCOACODE = map.AlmCoaCode (veya dr.ALMCOACODE). RowId kullanma.
                Başlık kalem: Header1-Header7 ile yaprak seç; boş ALMCOACODE toplama.

                Kodlar (uydurma):
                APPROACH_CODE: Liquidity | Rate (Likidite / Kar payı)
                POOL_TYPE: KATILMA | OZKAYNAK
                BALANCE_TYPE: TOTAL | PRINCIPALRECEIVED | PRINCIPALPAID | INTERESTRECEIVED | INTERESTPAID
                CCY_CODE: TRY | USD | EUR | XAU | XAG | DGR — farklı CCY SUM etme
                Tarih yoksa MAX(REPORTING_DATE). 'en yüksek/en düşük olduğu tarih', 'hangi tarih', 'tarihler arası' ise MAX kullanma: tüm REPORTING_DATE GROUP BY, ORDER BY Tutar DESC/ASC. Belirtilmezse BALANCE_TYPE=N'TOTAL'.
                APPROACH belirtilmezse Liquidity. CCY/POOL yoksa uydurma; Assumptions'a yaz.

                Vade dilimleri: DAY_1..DAY_7, DAY_8_15, DAY_16_30, MONTH_*, YEAR_*
                Duration: Header1–7 / portföyde PV01 = SUM(dr.PV01_REPORTING_CCY) AS Toplam_PV01_TRY (ağırlıklı ortalama yok). Bakiye = SUM(OUTSTANDING_BALANCE) AS Toplam_Bakiye. MD/Macaulay/YTM/convexity/kalan ömür/gösterge getiri = SUM(metrik * bakiye) / NULLIF(SUM(bakiye),0). AVG yok.
                Türev araçlar Header2 N'TÜREV FİNANSAL ARAÇLAR' (BD/TFA/IRS,CCS,VI,FXSWAP).
                TFV: TP/A/TFV, YP/A/TFV. TFY: TP/P/TFY, YP/P/TFY.
                SELECT * yok. Unicode N'...'. TOP sistem ekler.
                """;
        }

        return $$"""
            Sen kurumsal bir ALM Text-to-SQL üreticisisin. Yalnızca Microsoft SQL Server T-SQL SELECT yazarsın.
            Yanıtın tek bir JSON nesnesi olmalı: { "Sql": "...", "Explanation": "...", "Assumptions": ["..."] }
            Markdown, açıklama metni veya kod çiti yazma.

            Kurallar:
            - Tek SELECT. INSERT/UPDATE/DELETE/MERGE/DDL/EXEC yok.
            - Şema: [IFRSStaging].[ALM]. Tablolar: InternalReports, InternalDurationReports, InternalReportMap, CoreDepositRates.
            - Join yalnızca ALMCOACODE = AlmCoaCode. RowId kullanma.
            - Başlık sorularında map Header1-Header7 ile yaprak kodlara in; boş ALMCOACODE satırını toplama.
            - Kod değerlerini aşağıdaki sözlükteki DISTINCT listelerden al; uydurma kod yazma.
            - Likidite=Liquidity (varsayılan), Kar Payı=Rate, Özkaynak=OZKAYNAK, Toplam=TOTAL.
            - Dövizleri karma SUM etme. Tarih yoksa MAX(REPORTING_DATE). En yüksek/en düşük olduğu tarih veya tarihler arası karşılaştırma ise MAX filtreleme; GROUP BY REPORTING_DATE.
            - Duration Header1–7 / portföy: PV01 SUM (ağırlıklı ortalama yok); MD/YTM/convexity/kalan ömür/gösterge getiri bakiye ağırlıklı (NULLIF). AVG yok.
            - SELECT * yazma. Unicode için N'...'.

            VERİ SÖZLÜĞÜ:
            {{_dictionary}}
            """;
    }

    private static bool IsClientError(int status) => status is >= 400 and < 500;

    private static string StripThink(string content)
    {
        var stripped = System.Text.RegularExpressions.Regex.Replace(
            content,
            "<think>[\\s\\S]*?</think>",
            string.Empty,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return stripped.Trim();
    }

    private static string ExtractJsonObject(string content)
    {
        var trimmed = StripFence(StripThink(content));
        var start = trimmed.IndexOf('{');
        var end = trimmed.LastIndexOf('}');
        return start >= 0 && end > start ? trimmed[start..(end + 1)] : trimmed;
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

    private static string TrimForLog(string body) =>
        body.Length <= 500 ? body : body[..500];

    private static string LoadDictionary(IWebHostEnvironment environment)
    {
        var candidates = new[]
        {
            Path.Combine(environment.ContentRootPath, "Prompts", "ALM_DATA_DICTIONARY.md"),
            Path.Combine(AppContext.BaseDirectory, "Prompts", "ALM_DATA_DICTIONARY.md"),
            Path.Combine(environment.ContentRootPath, "..", "..", "ALM_DATA_DICTIONARY.md"),
            Path.Combine(environment.ContentRootPath, "ALM_DATA_DICTIONARY.md")
        };

        var path = candidates.FirstOrDefault(File.Exists)
                   ?? throw new FileNotFoundException("ALM_DATA_DICTIONARY.md bulunamadı.");
        return File.ReadAllText(path);
    }
}
