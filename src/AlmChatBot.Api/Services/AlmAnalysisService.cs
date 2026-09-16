using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AlmChatBot.Api.Services;

public interface IAlmAnalysisService
{
    Task<string> AnalyzeAsync(
        string question,
        IReadOnlyList<Dictionary<string, object?>> resultRows,
        CancellationToken cancellationToken);
}

public sealed class AlmAnalysisService(
    IAlmSqlPlanner planner,
    ISqlGuardrailService guardrail,
    IAlmDbService db,
    IQwenClientService qwen,
    ILogger<AlmAnalysisService> logger) : IAlmAnalysisService
{
    public async Task<string> AnalyzeAsync(
        string question,
        IReadOnlyList<Dictionary<string, object?>> resultRows,
        CancellationToken cancellationToken)
    {
        var interpreted = planner.Interpret(question);
        if (string.IsNullOrWhiteSpace(interpreted.HeaderValue))
        {
            return "Kalem kural motorunca tanınmadığı için kıyaslı analiz üretilemedi. Soruyu netleştirip yeniden Sor, ardından Analiz’e basın.";
        }

        if (interpreted.Metric is not null)
        {
            return DurationNote(interpreted, resultRows);
        }

        var date = interpreted.ReportingDate
                   ?? ReadString(resultRows, "REPORTING_DATE")
                   ?? await MaxDateAsync(cancellationToken);

        var facts = await LoadFactsAsync(interpreted, resultRows, date, cancellationToken);
        var draft = AlmAnalystNarrative.Render(facts);

        if (qwen.UsesLocalOllama)
        {
            return draft;
        }

        try
        {
            var polished = await qwen.PolishAnalysisAsync(question, draft, cancellationToken);
            return LooksUsable(polished) ? polished : draft;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "LLM analiz metni alınamadı, kural tabanlı yorum kullanılacak.");
            return draft;
        }
    }

    private async Task<AlmAnalysisFacts> LoadFactsAsync(
        QuestionInterpretation query,
        IReadOnlyList<Dictionary<string, object?>> resultRows,
        string date,
        CancellationToken cancellationToken)
    {
        var subjectRows = await QueryAsync(SubjectTotalSql(query, date), cancellationToken);
        var poolRows = await QueryAsync(PoolSplitSql(query, date), cancellationToken);
        var subjectFromGrid = SumColumn(subjectRows, "Tutar");
        if (subjectFromGrid == 0)
        {
            subjectFromGrid = SumColumn(resultRows, "Tutar");
        }

        var katilma = SumColumn(poolRows, "Tutar", "POOL_TYPE", "KATILMA");
        var ozkaynak = SumColumn(poolRows, "Tutar", "POOL_TYPE", "OZKAYNAK");
        if (katilma == 0 && ozkaynak == 0)
        {
            katilma = query.Pool == "KATILMA" ? subjectFromGrid : 0;
            ozkaynak = query.Pool == "OZKAYNAK" ? subjectFromGrid : 0;
        }

        var slice = await QueryAsync(PeerSql(query, date), cancellationToken);
        var children = IsObs(query.HeaderValue)
            ? await QueryAsync(ObsChildrenSql(query, date), cancellationToken)
            : [];
        var shortRows = query.Bucket is null
            ? await QueryAsync(SubjectHorizonSql(query, date), cancellationToken)
            : [];
        var priorDate = await PriorDateAsync(date, cancellationToken);
        decimal? prior = null;
        if (priorDate is not null)
        {
            var priorRows = await QueryAsync(SubjectTotalSql(query, priorDate), cancellationToken);
            prior = SumColumn(priorRows, "Tutar");
        }

        return new AlmAnalysisFacts
        {
            Kalem = query.HeaderValue,
            Date = date,
            PriorDate = priorDate,
            Approach = query.Approach,
            Ccy = query.Ccy,
            BalanceType = query.BalanceType,
            AllTenors = query.Bucket is null,
            SubjectTotal = subjectFromGrid,
            SubjectShort = SumColumn(shortRows, "KisaVade"),
            Katilma = katilma,
            Ozkaynak = ozkaynak,
            Assets = SumColumn(slice, "Tutar", "Kalem", "VARLIKLAR"),
            Liabilities = SumColumn(slice, "Tutar", "Kalem", "YÜKÜMLÜLÜKLER"),
            OffBalance = SumColumn(slice, "Tutar", "Kalem", "BİLANÇO DIŞI İŞLEMLER"),
            PriorSubject = prior,
            Children = children
                .Select(r => (ReadString(r, "Kalem") ?? "?", ToDecimal(r.GetValueOrDefault("Tutar"))))
                .Where(x => x.Item1 != "?")
                .ToList()
        };
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        var guarded = guardrail.ValidateAndRewrite(sql);
        return await db.QueryAsync(guarded.SafeSql, cancellationToken);
    }

    private async Task<string> MaxDateAsync(CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            "SELECT MAX(REPORTING_DATE) AS D FROM [ALM].[InternalReports]",
            cancellationToken);
        return ReadString(rows, "D") ?? DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private async Task<string?> PriorDateAsync(string date, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(
            $"""
             SELECT MAX(REPORTING_DATE) AS D
             FROM [ALM].[InternalReports]
             WHERE REPORTING_DATE < '{date}'
             """,
            cancellationToken);
        return ReadString(rows, "D");
    }

    private static string CubeFilters(QuestionInterpretation query, string date)
    {
        var where = new List<string>
        {
            $"ir.REPORTING_DATE = '{date}'",
            $"ir.BALANCE_TYPE = N'{AlmSqlPlanner.SqlLiteral(query.BalanceType)}'",
            "ir.ALMCOACODE IS NOT NULL",
            "LTRIM(RTRIM(ir.ALMCOACODE)) <> N''"
        };
        if (query.Approach is not null)
        {
            where.Add($"ir.APPROACH_CODE = N'{AlmSqlPlanner.SqlLiteral(query.Approach)}'");
        }

        if (query.Ccy is not null)
        {
            where.Add($"ir.CCY_CODE = N'{AlmSqlPlanner.SqlLiteral(query.Ccy)}'");
        }

        if (query.Pool is not null)
        {
            where.Add($"ir.POOL_TYPE = N'{AlmSqlPlanner.SqlLiteral(query.Pool)}'");
        }

        return string.Join(" AND ", where);
    }

    private static string PeerSql(QuestionInterpretation query, string date) =>
        $"""
         SELECT map.Header1 AS Kalem, SUM({AlmSqlPlanner.TenorSumSql()}) AS Tutar
         FROM [ALM].[InternalReports] AS ir
         INNER JOIN [ALM].[InternalReportMap] AS map ON ir.ALMCOACODE = map.AlmCoaCode
         WHERE {CubeFilters(query, date)}
         GROUP BY map.Header1
         """;

    private static string ObsChildrenSql(QuestionInterpretation query, string date) =>
        $"""
         SELECT map.Header2 AS Kalem, SUM({AlmSqlPlanner.TenorSumSql()}) AS Tutar
         FROM [ALM].[InternalReports] AS ir
         INNER JOIN [ALM].[InternalReportMap] AS map ON ir.ALMCOACODE = map.AlmCoaCode
         WHERE {CubeFilters(query, date)}
           AND {AlmSqlPlanner.HeaderMatchSql("Header1", "BİLANÇO DIŞI İŞLEMLER")}
           AND map.Header2 IS NOT NULL
           AND LTRIM(RTRIM(map.Header2)) <> N''
         GROUP BY map.Header2
         """;

    private static string PoolSplitSql(QuestionInterpretation query, string date) =>
        $"""
         SELECT ir.POOL_TYPE, SUM({AlmSqlPlanner.TenorSumSql()}) AS Tutar
         FROM [ALM].[InternalReports] AS ir
         INNER JOIN [ALM].[InternalReportMap] AS map ON ir.ALMCOACODE = map.AlmCoaCode
         WHERE {CubeFilters(query, date)}
           AND {AlmSqlPlanner.HeaderMatchSql(query.HeaderColumn, query.HeaderValue)}
         GROUP BY ir.POOL_TYPE
         """;

    private static string SubjectHorizonSql(QuestionInterpretation query, string date) =>
        $"""
         SELECT SUM({AlmSqlPlanner.ShortTenorSumSql()}) AS KisaVade
         FROM [ALM].[InternalReports] AS ir
         INNER JOIN [ALM].[InternalReportMap] AS map ON ir.ALMCOACODE = map.AlmCoaCode
         WHERE {CubeFilters(query, date)}
           AND {AlmSqlPlanner.HeaderMatchSql(query.HeaderColumn, query.HeaderValue)}
         """;

    private static string SubjectTotalSql(QuestionInterpretation query, string date) =>
        $"""
         SELECT SUM({AlmSqlPlanner.TenorSumSql()}) AS Tutar
         FROM [ALM].[InternalReports] AS ir
         INNER JOIN [ALM].[InternalReportMap] AS map ON ir.ALMCOACODE = map.AlmCoaCode
         WHERE {CubeFilters(query, date)}
           AND {AlmSqlPlanner.HeaderMatchSql(query.HeaderColumn, query.HeaderValue)}
         """;

    private static string DurationNote(QuestionInterpretation query, IReadOnlyList<Dictionary<string, object?>> rows)
    {
        var value = FirstDecimal(rows, query.Metric ?? "MODIFIED_DURATION");
        var bal = FirstDecimal(rows, "OutstandingBalance");
        if (value is null)
        {
            return "Duration sonucu okunamadı.";
        }

        var band = value.Value switch
        {
            < 1 => "çok kısa; faiz/kâr payı duyarlılığı düşük, yeniden fiyatlama yakındır.",
            < 3 => "kısa-orta bandında; likidite ve marj yönetimi açısından yönetilebilir.",
            < 6 => "orta vade; faiz şoklarında değer değişimi belirginleşir.",
            _ => "uzun; değer ve kâr payı riski yüksek, hedge ihtiyacı artar."
        };
        return $"{query.HeaderValue} için bakiye ağırlıklı {query.Metric} = {value.Value.ToString("N4", CultureInfo.GetCultureInfo("tr-TR"))} yıl (bakiye {bal?.ToString("N0", CultureInfo.GetCultureInfo("tr-TR")) ?? "n/a"}). {band} Karşılaştırma için aynı tarihte mevduat ve menkul kıymet duration’ı ayrıca sorulmalıdır.";
    }

    private static bool IsObs(string header) =>
        header.Contains("BİLANÇO DIŞI", StringComparison.OrdinalIgnoreCase)
        || header.Contains("TÜREV FİNANSAL ARAÇLAR", StringComparison.OrdinalIgnoreCase);

    private static decimal SumColumn(
        IReadOnlyList<Dictionary<string, object?>> rows,
        string amountKey,
        string? filterKey = null,
        string? filterValue = null)
    {
        decimal sum = 0;
        foreach (var row in rows)
        {
            if (filterKey is not null)
            {
                var label = ReadString(row, filterKey);
                if (label is null || !label.Equals(filterValue, StringComparison.OrdinalIgnoreCase)
                    && !FoldEq(label, filterValue))
                {
                    continue;
                }
            }

            sum += ToDecimal(row.GetValueOrDefault(amountKey));
        }

        return sum;
    }

    private static bool FoldEq(string a, string? b)
    {
        if (b is null)
        {
            return false;
        }

        return FoldKey(a) == FoldKey(b) || FoldKey(a).Contains(FoldKey(b)) || FoldKey(b).Contains(FoldKey(a));
    }

    private static string FoldKey(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var chars = normalized.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        return string.Concat(chars).Replace('ı', 'i').Replace('İ', 'I').ToUpperInvariant();
    }

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0,
        decimal d => d,
        double x => (decimal)x,
        float f => (decimal)f,
        int i => i,
        long l => l,
        JsonElement el => el.ValueKind switch
        {
            JsonValueKind.Number => el.TryGetDecimal(out var n) ? n : (decimal)el.GetDouble(),
            JsonValueKind.String => ToDecimal(el.GetString()),
            _ => 0
        },
        string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) => p,
        string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.GetCultureInfo("tr-TR"), out var tr) => tr,
        _ => 0
    };

    private static decimal? FirstDecimal(IReadOnlyList<Dictionary<string, object?>> rows, string key)
    {
        foreach (var row in rows)
        {
            if (row.TryGetValue(key, out var value) && value is not null)
            {
                return ToDecimal(value);
            }
        }

        return null;
    }

    private static string? ReadString(IReadOnlyList<Dictionary<string, object?>> rows, string key) =>
        rows.Select(r => ReadString(r, key)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    private static string? ReadString(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static bool LooksUsable(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 80)
        {
            return false;
        }

        if (text.Contains("bahwa", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Tablo中")
            || text.Any(c => c is >= '\u4e00' and <= '\u9fff'))
        {
            return false;
        }

        return true;
    }
}
