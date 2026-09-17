using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AlmChatBot.Api.Models;

namespace AlmChatBot.Api.Services;

public interface IAlmSqlPlanner
{
    LlmSqlResponse? TryPlan(string question);

    QuestionInterpretation Interpret(string question);
}

public sealed class QuestionInterpretation
{
    public LlmSqlResponse? Sql { get; init; }

    public bool NeedsConfirmation { get; init; }

    public string SuggestedQuestion { get; init; } = string.Empty;

    public string InterpretationSummary { get; init; } = string.Empty;

    public List<string> Corrections { get; init; } = [];

    public string HeaderColumn { get; init; } = "Header2";

    public string HeaderValue { get; init; } = string.Empty;

    public string? ReportingDate { get; init; }

    public string? Approach { get; init; }

    public string? Ccy { get; init; }

    public string? Pool { get; init; }

    public string BalanceType { get; init; } = "TOTAL";

    public string? Bucket { get; init; }

    public string? Metric { get; init; }

    public List<FollowUpSuggestionDto> FollowUps { get; init; } = [];
}

internal enum DateScanMode
{
    Snapshot,
    Series,
    Highest,
    Lowest
}

/// <summary>
/// ALM küpü kapalı bir sözlük: kalem + tarih + yaklaşım + döviz + havuz + vade dilimi/metrik.
/// Bu motor SQL'i LLM olmadan derler; eşleşmeyen serbest metin Llama'ya düşer.
/// </summary>
public sealed class AlmSqlPlanner : IAlmSqlPlanner
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static readonly string[] TenorColumns =
    [
        "DAY_1", "DAY_2", "DAY_3", "DAY_4", "DAY_5", "DAY_6", "DAY_7",
        "DAY_8_15", "DAY_16_30",
        "MONTH_1_2", "MONTH_2_3", "MONTH_3_4", "MONTH_4_5", "MONTH_5_6",
        "MONTH_6_9", "MONTH_9_12", "MONTH_12_18", "MONTH_18_24",
        "YEAR_2_3", "YEAR_3_4", "YEAR_4_5", "YEAR_5_6", "YEAR_6_7", "YEAR_7_8",
        "YEAR_8_9", "YEAR_9_10", "YEAR_10_15", "YEAR_15_20", "YEAR_20_PLUS"
    ];

    public static readonly string[] ShortTenorColumns =
    [
        "DAY_1", "DAY_2", "DAY_3", "DAY_4", "DAY_5", "DAY_6", "DAY_7", "DAY_8_15", "DAY_16_30"
    ];

    public static string TenorSumSql(string alias = "ir") =>
        string.Join(" + ", TenorColumns.Select(c => $"ISNULL({alias}.{c}, 0)"));

    public static string ShortTenorSumSql(string alias = "ir") =>
        string.Join(" + ", ShortTenorColumns.Select(c => $"ISNULL({alias}.{c}, 0)"));

    public static string SqlLiteral(string value) => value.Replace("'", "''");

    public static string HeaderEqualsSql(string column, string value) =>
        $"map.{column} COLLATE Latin1_General_CI_AI = N'{SqlLiteral(value)}' COLLATE Latin1_General_CI_AI";

    public static string HeaderMatchSql(string column, string value)
    {
        var ascii = ToAsciiHeader(value);
        if (ascii.Equals(value, StringComparison.OrdinalIgnoreCase))
        {
            return HeaderEqualsSql(column, value);
        }

        return $"({HeaderEqualsSql(column, value)} OR {HeaderEqualsSql(column, ascii)})";
    }

    public static string ToAsciiHeader(string value)
    {
        var chars = value.Normalize(NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        return string.Concat(chars).Replace('ı', 'I').Replace('İ', 'I');
    }

    private static readonly LineItem[] LineItems =
    [
        new("vadeye kadar elde tutulacak tlref", "Header3", "Vadeye Kadar Elde Tutulacak", "TLREF"),
        new("vadeye kadar elde tutulacak", "Header3", "Vadeye Kadar Elde Tutulacak"),
        new("türev finansal araçlar (net)", "Header1", "TÜREV FİNANSAL ARAÇLAR (NET)"),
        new("türev finansal yükümlülük", "Header2", "TÜREV FİNANSAL YÜKÜMLÜLÜKLER"),
        new("türev finansal varlık", "Header2", "TÜREV FİNANSAL VARLIKLAR"),
        new("türev finansal araç", "Header2", "TÜREV FİNANSAL ARAÇLAR"),
        new("bilanço dışı işlem", "Header1", "BİLANÇO DIŞI İŞLEMLER"),
        new("varlıklar", "Header1", "VARLIKLAR"),
        new("varlık", "Header1", "VARLIKLAR"),
        new("yükümlülükler", "Header1", "YÜKÜMLÜLÜKLER"),
        new("yükümlülük", "Header1", "YÜKÜMLÜLÜKLER"),
        new("nakit ve nakit benzer", "Header2", "NAKİT VE NAKİT BENZERLERİ"),
        new("para piyasalarından alacak", "Header2", "PARA PİYASALARINDAN ALACAKLAR"),
        new("para piyasalarına borç", "Header2", "PARA PİYASALARINA BORÇLAR - REPO"),
        new("menkul kıymet", "Header2", "MENKUL KIYMETLER"),
        new("ortaklık yatırım", "Header2", "ORTAKLIK YATIRIMLARI"),
        new("alım-satım", "Header3", "Alim-Satim"),
        new("alim-satim", "Header3", "Alim-Satim"),
        new("alım satım", "Header3", "Alim-Satim"),
        new("npl", "Header2", "TAKİPTEKİ ALACAKLAR"),
        new("takipteki alacak", "Header2", "TAKİPTEKİ ALACAKLAR"),
        new("alınan kredi", "Header2", "ALINAN KREDİLER"),
        new("sermaye benzeri", "Header2", "SERMAYE BENZERİ BORÇLANMA ARAÇLARI"),
        new("beklenen zarar karşılıkları-g", "Header2", "BEKLENEN ZARAR KARŞILIKLARI-G.NAKDİ"),
        new("beklenen zarar karşılıkları", "Header2", "BEKLENEN ZARAR KARŞILIKLARI-NAKDİ"),
        new("garanti ve kefalet", "Header2", "GARANTİ VE KEFALETLER"),
        new("taahhüt", "Header2", "TAAHHÜTLER"),
        new("diğer aktif", "Header2", "DİĞER AKTİF"),
        new("diğer pasif", "Header2", "DİĞER PASİFLER"),
        new("özkaynaklar", "Header2", "ÖZKAYNAKLAR"),
        new("mevduat", "Header2", "MEVDUAT"),
        new("kredi", "Header2", "KREDİLER")
    ];

    private static readonly Dictionary<string, string[]> ChildrenByHeader = new(StringComparer.OrdinalIgnoreCase)
    {
        ["VARLIKLAR"] =
        [
            "NAKİT VE NAKİT BENZERLERİ",
            "PARA PİYASALARINDAN ALACAKLAR",
            "MENKUL KIYMETLER",
            "ORTAKLIK YATIRIMLARI",
            "KREDİLER",
            "BEKLENEN ZARAR KARŞILIKLARI-NAKDİ",
            "TAKİPTEKİ ALACAKLAR",
            "TÜREV FİNANSAL VARLIKLAR",
            "DİĞER AKTİF"
        ],
        ["YÜKÜMLÜLÜKLER"] =
        [
            "MEVDUAT",
            "ALINAN KREDİLER",
            "PARA PİYASALARINA BORÇLAR - REPO",
            "TÜREV FİNANSAL YÜKÜMLÜLÜKLER",
            "SERMAYE BENZERİ BORÇLANMA ARAÇLARI",
            "DİĞER PASİFLER",
            "ÖZKAYNAKLAR",
            "BEKLENEN ZARAR KARŞILIKLARI-G.NAKDİ"
        ],
        ["BİLANÇO DIŞI İŞLEMLER"] =
        [
            "GARANTİ VE KEFALETLER",
            "TAAHHÜTLER",
            "TÜREV FİNANSAL ARAÇLAR"
        ]
    };

    private static readonly (string Needle, string Column)[] BucketHints =
    [
        ("gün 16", "DAY_16_30"), ("gun 16", "DAY_16_30"), ("16-30", "DAY_16_30"), ("16_30", "DAY_16_30"),
        ("gün 8", "DAY_8_15"), ("gun 8", "DAY_8_15"), ("8-15", "DAY_8_15"), ("8_15", "DAY_8_15"),
        ("20 plus", "YEAR_20_PLUS"), ("20+", "YEAR_20_PLUS"), ("year_20", "YEAR_20_PLUS"),
        ("gün 1", "DAY_1"), ("gun 1", "DAY_1"), ("day_1", "DAY_1"), ("day 1", "DAY_1"),
        ("gün 2", "DAY_2"), ("gun 2", "DAY_2"),
        ("gün 3", "DAY_3"), ("gun 3", "DAY_3"),
        ("gün 4", "DAY_4"), ("gun 4", "DAY_4"),
        ("gün 5", "DAY_5"), ("gun 5", "DAY_5"),
        ("gün 6", "DAY_6"), ("gun 6", "DAY_6"),
        ("gün 7", "DAY_7"), ("gun 7", "DAY_7")
    ];

    private static readonly Regex TokenRx = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

    private static readonly string[] StemSuffixes =
    [
        "lerinden", "larından", "lerde", "larda", "lerin", "ların", "leri", "ları",
        "ler", "lar", "inden", "ından", "ında", "inde", "dan", "den", "tan", "ten",
        "nın", "nin", "nun", "nün", "ın", "in", "un", "ün"
    ];

    private static readonly HashSet<string> NoiseTokens = new(StringComparer.Ordinal)
    {
        "toplam", "bakiye", "bakiyesi", "nedir", "tarihinde", "tarihli", "tarih",
        "son", "rapor", "gibi", "ve", "ile", "icin", "için", "olan", "kalem", "kalemi",
        "havuz", "havuzu", "yaklaşım", "yaklasim", "türk", "turk", "lirası", "lirasi",
        "likidite", "liquidity", "katılma", "katilma", "özkaynak", "ozkaynak",
        "try", "tl", "usd", "eur", "xau", "xag", "dgr"
    };

    public LlmSqlResponse? TryPlan(string question) => Interpret(question).Sql;

    public QuestionInterpretation Interpret(string question)
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return new QuestionInterpretation();
        }

        var folded = Fold(question);
        var tokens = Tokenize(folded);
        var corrections = new List<string>();

        var exactItem = MatchLineItem(folded);
        var fuzzyItem = exactItem is null ? MatchLineItemFuzzy(folded, tokens) : null;
        var item = exactItem ?? fuzzyItem?.Item;
        if (item is null)
        {
            return new QuestionInterpretation();
        }

        if (fuzzyItem is not null && fuzzyItem.Distance > 0)
        {
            corrections.Add($"«{fuzzyItem.MatchedText}» → {item.HeaderValue}");
        }

        var metric = MatchDurationMetric(folded);
        var assumptions = new List<string>();
        var bucket = MatchBucket(folded);
        var approach = MatchApproach(folded) ?? MatchApproachFuzzy(tokens, corrections);
        var ccy = MatchCcy(folded) ?? MatchCcyFuzzy(tokens, corrections);
        var pool = MatchPool(folded) ?? MatchPoolFuzzy(tokens, corrections);
        var balance = MatchBalanceType(folded) ?? "TOTAL";
        var compactDate = MatchDate(folded);
        var dateScan = compactDate is null ? MatchDateScan(folded) : DateScanMode.Snapshot;
        var tenorDist = metric is null && bucket is null && AsksTenorDistribution(folded);
        var breakdown = MatchBreakdown(folded, tenorDist);
        if (approach is null)
        {
            approach = "Liquidity";
            assumptions.Add("APPROACH_CODE belirtilmedi; N'Liquidity' alındı.");
        }

        var dateSql = BuildDatePredicate(folded, metric is not null, dateScan, assumptions);
        if (MatchBalanceType(folded) is null)
        {
            assumptions.Add("BALANCE_TYPE belirtilmedi; N'TOTAL' alındı.");
        }

        var groupCcy = ccy is null && (breakdown.HasFlag(Breakdown.Ccy) || tenorDist);
        var groupPool = pool is null && breakdown.HasFlag(Breakdown.Pool);
        var groupTpYp = breakdown.HasFlag(Breakdown.TpYp);
        var childColumn = breakdown.HasFlag(Breakdown.Child) ? NextHeaderColumn(item.HeaderColumn) : null;

        LlmSqlResponse sql;
        if (metric is not null)
        {
            if (ccy is not null || pool is not null)
            {
                assumptions.Add("InternalDurationReports içinde CCY/POOL kolonu yok; bu filtreler uygulanamadı.");
            }

            sql = new LlmSqlResponse
            {
                Sql = BuildDurationSql(item, metric, dateSql, dateScan),
                Explanation = metric == "ALL"
                    ? "SQL kural motoru ile üretildi. Header/portföy duration: PV01 SUM, diğer metrikler bakiye ağırlıklı."
                    : "SQL kural motoru ile üretildi (LLM yok). Duration bakiye ağırlıklı ortalama; PV01 SUM.",
                Assumptions = assumptions
            };
        }
        else
        {
            if (ccy is null)
            {
                assumptions.Add(groupCcy
                    ? "CCY_CODE belirtilmedi; satırlar dövize göre kırıldı (karma SUM yok)."
                    : "CCY_CODE belirtilmedi; ana kalemin toplamı verildi. Dövizler toplanır; döviz koduna göre kırılabilir.");
            }

            if (pool is null && tenorDist)
            {
                assumptions.Add("Vade dağılımında havuzlar toplandı; dövizler ayrı tutuldu.");
            }
            else if (pool is null)
            {
                assumptions.Add(groupPool
                    ? "POOL_TYPE belirtilmedi; satırlar havuza göre kırıldı."
                    : "POOL_TYPE belirtilmedi; havuzlar toplandı. Havuz tipine göre kırılabilir.");
            }

            if (groupTpYp)
            {
                assumptions.Add("ALMCOACODE TP/ ve YP/ önekine göre yerel/yabancı para kırıldı.");
            }

            if (childColumn is not null)
            {
                assumptions.Add($"{item.HeaderValue} alt kalemleri {childColumn} üzerinden kırıldı.");
            }

            var bucketNote = tenorDist
                ? "Vade dilimleri satır olarak açıldı (DAY_1 … YEAR_20_PLUS)."
                : bucket is null
                    ? "Belirtilen vade dilimi yok; tüm vade dilimleri toplandı."
                    : $"{bucket} vade dilimi seçildi.";
            var totalNote = groupCcy || groupPool || groupTpYp || childColumn is not null || tenorDist
                ? bucketNote
                : $"Ana kırılımın toplamı verildi. {bucketNote}";

            sql = new LlmSqlResponse
            {
                Sql = BuildGapSql(
                    item, dateSql, approach, ccy, pool, balance, bucket, dateScan, tenorDist,
                    groupCcy, groupPool, groupTpYp, childColumn),
                Explanation = $"SQL kural motoru ile üretildi (LLM yok). {totalNote}",
                Assumptions = assumptions
            };
        }

        var suggested = BuildSuggestedQuestion(item, approach, ccy, pool, compactDate, metric, bucket, dateScan, tenorDist);
        var summary = BuildSummary(item, approach, ccy, pool, compactDate, balance, metric, dateScan, tenorDist);
        var followUps = BuildFollowUps(item, approach, ccy, pool, compactDate, dateScan, breakdown, tenorDist, metric);

        return new QuestionInterpretation
        {
            Sql = sql,
            NeedsConfirmation = corrections.Count > 0,
            SuggestedQuestion = suggested,
            InterpretationSummary = summary,
            Corrections = corrections,
            FollowUps = followUps,
            HeaderColumn = item.HeaderColumn,
            HeaderValue = item.HeaderValue,
            ReportingDate = compactDate,
            Approach = approach,
            Ccy = ccy,
            Pool = pool,
            BalanceType = balance,
            Bucket = bucket,
            Metric = metric
        };
    }

    private static string BuildGapSql(
        LineItem item,
        string datePredicate,
        string? approach,
        string? ccy,
        string? pool,
        string balance,
        string? bucket,
        DateScanMode dateScan,
        bool tenorDistribution = false,
        bool groupCcy = false,
        bool groupPool = false,
        bool groupTpYp = false,
        string? childColumn = null)
    {
        var amount = bucket is null
            ? string.Join(" + ", TenorColumns.Select(c => $"ISNULL(ir.{c}, 0)"))
            : $"ISNULL(ir.{bucket}, 0)";

        var select = new List<string>
        {
            $"map.{item.HeaderColumn} AS Kalem",
            "ir.REPORTING_DATE"
        };
        var group = new List<string>
        {
            $"map.{item.HeaderColumn}",
            "ir.REPORTING_DATE"
        };

        AddDim(select, group, approach, "ir.APPROACH_CODE");
        if (groupCcy)
        {
            select.Add("ir.CCY_CODE");
            group.Add("ir.CCY_CODE");
        }

        if (groupPool)
        {
            select.Add("ir.POOL_TYPE");
            group.Add("ir.POOL_TYPE");
        }

        if (groupTpYp)
        {
            const string tpYpExpr = """
                CASE
                  WHEN ir.ALMCOACODE LIKE N'TP/%' THEN N'TP'
                  WHEN ir.ALMCOACODE LIKE N'YP/%' THEN N'YP'
                  ELSE N'DİĞER'
                END
                """;
            select.Add($"{tpYpExpr} AS TpYp");
            group.Add(tpYpExpr);
        }

        if (!string.IsNullOrEmpty(childColumn))
        {
            select.Add($"map.{childColumn} AS AltKalem");
            group.Add($"map.{childColumn}");
        }

        select.Add("ir.BALANCE_TYPE");
        group.Add("ir.BALANCE_TYPE");
        if (tenorDistribution)
        {
            select.Add("tenor.VadeDilimi");
            select.Add("tenor.Sira");
            group.Add("tenor.VadeDilimi");
            group.Add("tenor.Sira");
            select.Add("SUM(tenor.Tutar) AS Tutar");
        }
        else
        {
            select.Add($"SUM({amount}) AS Tutar");
        }

        var where = new List<string>();
        if (!string.IsNullOrEmpty(datePredicate))
        {
            where.Add(datePredicate);
        }

        where.Add($"ir.BALANCE_TYPE = N'{balance}'");
        where.Add("ir.ALMCOACODE IS NOT NULL");
        where.Add("LTRIM(RTRIM(ir.ALMCOACODE)) <> N''");
        where.Add(HeaderEquals(item.HeaderColumn, item.HeaderValue));
        if (item.CoaContains is not null)
        {
            select.Insert(1, "ir.ALMCOACODE");
            group.Insert(1, "ir.ALMCOACODE");
            where.Add($"ir.ALMCOACODE LIKE N'%{Escape(item.CoaContains)}%'");
        }
        if (!string.IsNullOrEmpty(childColumn))
        {
            where.Add($"map.{childColumn} IS NOT NULL");
            where.Add($"LTRIM(RTRIM(map.{childColumn})) <> N''");
        }
        if (approach is not null) where.Add($"ir.APPROACH_CODE = N'{approach}'");
        if (ccy is not null) where.Add($"ir.CCY_CODE = N'{ccy}'");
        if (pool is not null) where.Add($"ir.POOL_TYPE = N'{pool}'");

        var apply = tenorDistribution
            ? $"""

            CROSS APPLY (VALUES
              {string.Join($",{Environment.NewLine}              ", TenorColumns.Select((c, i) => $"(N'{c}', ISNULL(ir.{c}, 0), {i + 1})"))}
            ) AS tenor(VadeDilimi, Tutar, Sira)
            """
            : "";

        var sql = $"""
            SELECT
              {string.Join($",{Environment.NewLine}              ", select)}
            FROM [ALM].[InternalReports] AS ir
            INNER JOIN [ALM].[InternalReportMap] AS map
              ON ir.ALMCOACODE = map.AlmCoaCode{apply}
            WHERE {string.Join($"{Environment.NewLine}              AND ", where)}
            GROUP BY {string.Join(", ", group)}
            """;

        var orderBy = tenorDistribution
            ? TenorDistributionOrderBy(approach, groupCcy)
            : GapOrderBy(dateScan, approach, groupCcy, groupPool, groupTpYp, childColumn);
        return orderBy is null ? sql : sql + $"{Environment.NewLine}            ORDER BY {orderBy}";
    }

    private static string TenorDistributionOrderBy(string? approach, bool groupCcy)
    {
        var parts = new List<string> { "tenor.Sira" };
        if (approach is null) parts.Add("ir.APPROACH_CODE");
        if (groupCcy) parts.Add("ir.CCY_CODE");
        return string.Join(", ", parts);
    }

    private static string BuildDurationSql(LineItem item, string metricColumn, string datePredicate, DateScanMode dateScan)
    {
        var where = new List<string>();
        if (!string.IsNullOrEmpty(datePredicate))
        {
            where.Add(datePredicate);
        }

        where.Add("dr.ALMCOACODE IS NOT NULL");
        where.Add("LTRIM(RTRIM(dr.ALMCOACODE)) <> N''");
        where.Add($"map.{item.HeaderColumn} COLLATE Latin1_General_CI_AI = N'{Escape(item.HeaderValue)}' COLLATE Latin1_General_CI_AI");
        if (item.CoaContains is not null)
        {
            where.Add($"dr.ALMCOACODE LIKE N'%{Escape(item.CoaContains)}%'");
        }

        var measureAlias = metricColumn == "ALL" ? "Toplam_Bakiye" : DurationAlias(metricColumn);
        var sql = $"""
            SELECT
              map.{item.HeaderColumn} AS Kalem,
              dr.REPORTING_DATE,
              {DurationSelectList(metricColumn)}
            FROM [ALM].[InternalDurationReports] AS dr
            INNER JOIN [ALM].[InternalReportMap] AS map
              ON dr.ALMCOACODE = map.AlmCoaCode
            WHERE {string.Join($"{Environment.NewLine}              AND ", where)}
            GROUP BY map.{item.HeaderColumn}, dr.REPORTING_DATE
            """;

        var orderBy = dateScan switch
        {
            DateScanMode.Highest => $"{measureAlias} DESC, dr.REPORTING_DATE DESC",
            DateScanMode.Lowest => $"{measureAlias} ASC, dr.REPORTING_DATE DESC",
            DateScanMode.Series => "dr.REPORTING_DATE",
            _ => null
        };
        return orderBy is null ? sql : sql + $"{Environment.NewLine}            ORDER BY {orderBy}";
    }

    private static readonly string[] AllDurationMetrics =
    [
        "PV01_REPORTING_CCY",
        "MODIFIED_DURATION",
        "MACAULAY_DURATION",
        "YIELD_TO_MATURITY",
        "CONVEXITY",
        "REMAINING_LIFE",
        "COMPARABLE_YIELD"
    ];

    private static string DurationSelectList(string metricColumn)
    {
        var parts = new List<string> { "SUM(dr.OUTSTANDING_BALANCE) AS Toplam_Bakiye" };
        if (metricColumn == "ALL")
        {
            parts.AddRange(AllDurationMetrics.Select(DurationExpr));
        }
        else
        {
            parts.Add(DurationExpr(metricColumn));
        }

        return string.Join($",{Environment.NewLine}              ", parts);
    }

    private static string DurationExpr(string column) =>
        column.StartsWith("PV01_", StringComparison.OrdinalIgnoreCase)
            ? $"SUM(dr.{column}) AS {DurationAlias(column)}"
            : $"SUM(dr.{column} * dr.OUTSTANDING_BALANCE) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS {DurationAlias(column)}";

    private static string DurationAlias(string column) => column switch
    {
        "PV01_REPORTING_CCY" => "Toplam_PV01_TRY",
        "PV01_DEAL_CCY" => "Toplam_PV01_DealCcy",
        "MODIFIED_DURATION" => "Agirlikli_Mod_Duration",
        "MACAULAY_DURATION" => "Agirlikli_Mac_Duration",
        "YIELD_TO_MATURITY" => "Agirlikli_YTM",
        "CONVEXITY" => "Agirlikli_Convexity",
        "REMAINING_LIFE" => "Agirlikli_Kalan_Omur",
        "COMPARABLE_YIELD" => "Agirlikli_Gosterge_Getiri",
        _ => column
    };

    private static void AddDim(List<string> select, List<string> group, string? filter, string column)
    {
        if (filter is not null)
        {
            return;
        }

        select.Add(column);
        group.Add(column);
    }

    private static string? GapOrderBy(
        DateScanMode scan,
        string? approach,
        bool groupCcy,
        bool groupPool,
        bool groupTpYp,
        string? childColumn)
    {
        if (scan is DateScanMode.Highest)
        {
            return "Tutar DESC, ir.REPORTING_DATE DESC";
        }

        if (scan is DateScanMode.Lowest)
        {
            return "Tutar ASC, ir.REPORTING_DATE DESC";
        }

        if (scan is DateScanMode.Series)
        {
            var parts = new List<string> { "ir.REPORTING_DATE" };
            if (approach is null) parts.Add("ir.APPROACH_CODE");
            if (groupCcy) parts.Add("ir.CCY_CODE");
            if (groupPool) parts.Add("ir.POOL_TYPE");
            return string.Join(", ", parts);
        }

        if (groupCcy || groupPool || groupTpYp || !string.IsNullOrEmpty(childColumn))
        {
            return "Tutar DESC";
        }

        return null;
    }

    private static string BuildDatePredicate(string folded, bool duration, DateScanMode scan, List<string> assumptions)
    {
        var table = duration ? "[ALM].[InternalDurationReports]" : "[ALM].[InternalReports]";
        var alias = duration ? "dr" : "ir";
        var explicitDate = MatchDate(folded);
        if (explicitDate is not null)
        {
            return $"{alias}.REPORTING_DATE = '{explicitDate}'";
        }

        if (scan is DateScanMode.Highest or DateScanMode.Lowest or DateScanMode.Series)
        {
            assumptions.Add("Tarih taraması: tüm REPORTING_DATE değerleri karşılaştırıldı; MAX tarih filtresi yok.");
            return "";
        }

        assumptions.Add("Tarih belirtilmedi; MAX(REPORTING_DATE) kullanıldı.");
        return $"{alias}.REPORTING_DATE = (SELECT MAX(REPORTING_DATE) FROM {table})";
    }

    private static DateScanMode MatchDateScan(string folded)
    {
        if (HasLatestSnapshotHint(folded) && !AsksWhichDate(folded) && !AsksDateCompare(folded))
        {
            return DateScanMode.Snapshot;
        }

        if (AsksLowest(folded))
        {
            return DateScanMode.Lowest;
        }

        if (AsksHighest(folded))
        {
            return DateScanMode.Highest;
        }

        if (AsksWhichDate(folded) || AsksDateCompare(folded))
        {
            return DateScanMode.Series;
        }

        return DateScanMode.Snapshot;
    }

    private static bool HasLatestSnapshotHint(string folded) =>
        folded.Contains("son rapor", StringComparison.Ordinal)
        || folded.Contains("son tarih", StringComparison.Ordinal)
        || folded.Contains("güncel tarih", StringComparison.Ordinal)
        || folded.Contains("guncel tarih", StringComparison.Ordinal);

    private static bool AsksHighest(string folded) =>
        ContainsAny(folded, "en yüksek", "en yuksek", "en büyük", "en buyuk", "en fazla", "maksimum", "maximum");

    private static bool AsksLowest(string folded) =>
        ContainsAny(folded, "en düşük", "en dusuk", "en az", "en küçük", "en kucuk", "minimum");

    private static bool AsksWhichDate(string folded) =>
        ContainsAny(folded, "hangi tarih", "olduğu tarih", "oldugu tarih", "hangi dönem", "hangi donem", "hangi rapor");

    private static bool AsksTenorDistribution(string folded) =>
        ContainsAny(
            folded,
            "vade dilim", "vade dağılım", "vade dagilim",
            "dilimlerine göre", "dilimlerine gore", "dilimlere göre", "dilimlere gore",
            "vade kırılım", "vade kirilim", "tenor");

    [Flags]
    private enum Breakdown
    {
        None = 0,
        Ccy = 1,
        Pool = 2,
        TpYp = 4,
        Child = 8
    }

    private static Breakdown MatchBreakdown(string folded, bool tenorDistribution)
    {
        var flags = Breakdown.None;
        if (ContainsAny(
                folded,
                "döviz kod", "doviz kod", "dövize göre", "dovize gore", "döviz baz", "doviz baz",
                "ccy", "para birim", "kurlara göre", "kurlara gore"))
        {
            flags |= Breakdown.Ccy;
        }

        if (ContainsAny(
                folded,
                "havuz tip", "havuza göre", "havuza gore", "havuz baz",
                "katılma ve özkaynak", "katilma ve ozkaynak", "özkaynak ve katılma", "ozkaynak ve katilma"))
        {
            flags |= Breakdown.Pool;
        }

        if (ContainsAny(
                folded,
                "tp/yp", "tp yp", "tp ve yp", "yp ve tp", "tp-yp",
                "yerel para", "yabancı para", "yabanci para"))
        {
            flags |= Breakdown.TpYp;
        }

        if (!tenorDistribution
            && ContainsAny(
                folded,
                "alt kalem", "kalemlere göre", "kalemlere gore", "kalem baz",
                "header2", "header3", "header4"))
        {
            flags |= Breakdown.Child;
        }

        return flags;
    }

    private static string? NextHeaderColumn(string column) => column switch
    {
        "Header1" => "Header2",
        "Header2" => "Header3",
        "Header3" => "Header4",
        "Header4" => "Header5",
        "Header5" => "Header6",
        "Header6" => "Header7",
        _ => null
    };

    private static bool AsksDateCompare(string folded) =>
        ContainsAny(
            folded,
            "tarihler arası", "tarihler arasi", "tarihleri karşılaştır", "tarihleri karsilastir",
            "tarih bazında", "tarih bazinda", "tüm tarihler", "tum tarihler",
            "her tarih", "dönem dönem", "donem donem", "zaman içinde", "zaman icinde");

    private static bool ContainsAny(string folded, params string[] needles) =>
        needles.Any(n => folded.Contains(n, StringComparison.Ordinal));

    private static LineItem? MatchLineItem(string folded) =>
        LineItems.FirstOrDefault(item => folded.Contains(item.Phrase, StringComparison.Ordinal));

    private static string? MatchDurationMetric(string folded)
    {
        if (folded.Contains("modified", StringComparison.Ordinal) || folded.Contains("modifiye", StringComparison.Ordinal))
        {
            return "MODIFIED_DURATION";
        }

        if (folded.Contains("macaulay", StringComparison.Ordinal))
        {
            return "MACAULAY_DURATION";
        }

        if (folded.Contains("konveksite", StringComparison.Ordinal) || folded.Contains("convexity", StringComparison.Ordinal))
        {
            return "CONVEXITY";
        }

        if (ContainsAny(folded, "gösterge getiri", "gosterge getiri", "comparable yield", "karşılaştırmalı getiri", "karsilastirmali getiri"))
        {
            return "COMPARABLE_YIELD";
        }

        if (folded.Contains("ytm", StringComparison.Ordinal)
            || (folded.Contains("yield", StringComparison.Ordinal) && !folded.Contains("comparable", StringComparison.Ordinal)))
        {
            return "YIELD_TO_MATURITY";
        }

        if (folded.Contains("pv01", StringComparison.Ordinal))
        {
            return ContainsAny(folded, "deal", "işlem cinsi", "islem cinsi")
                ? "PV01_DEAL_CCY"
                : "PV01_REPORTING_CCY";
        }

        if (folded.Contains("kalan ömür", StringComparison.Ordinal) || folded.Contains("remaining life", StringComparison.Ordinal))
        {
            return "REMAINING_LIFE";
        }

        if (ContainsAny(folded, "risk metrik", "tüm duration", "tum duration", "duration rapor"))
        {
            return "ALL";
        }

        return null;
    }

    private static string? MatchApproach(string folded)
    {
        if (folded.Contains("likidite", StringComparison.Ordinal) || folded.Contains("liquidity", StringComparison.Ordinal))
        {
            return "Liquidity";
        }

        if (folded.Contains("kar payı", StringComparison.Ordinal)
            || folded.Contains("faiz yaklaş", StringComparison.Ordinal)
            || Regex.IsMatch(folded, @"\brate\b"))
        {
            return "Rate";
        }

        return null;
    }

    private static string? MatchCcy(string folded)
    {
        if (Regex.IsMatch(folded, @"\btry\b") || Regex.IsMatch(folded, @"\btl\b") || folded.Contains("türk lirası", StringComparison.Ordinal))
        {
            return "TRY";
        }

        if (Regex.IsMatch(folded, @"\busd\b") || folded.Contains("dolar", StringComparison.Ordinal))
        {
            return "USD";
        }

        if (Regex.IsMatch(folded, @"\beur\b") || folded.Contains("euro", StringComparison.Ordinal))
        {
            return "EUR";
        }

        if (Regex.IsMatch(folded, @"\bxau\b")) return "XAU";
        if (Regex.IsMatch(folded, @"\bxag\b")) return "XAG";
        if (Regex.IsMatch(folded, @"\bdgr\b")) return "DGR";
        return null;
    }

    private static string? MatchPool(string folded)
    {
        if (folded.Contains("katılma", StringComparison.Ordinal))
        {
            return "KATILMA";
        }

        if (folded.Contains("özkaynak havuz", StringComparison.Ordinal)
            || folded.Contains("ozkaynak", StringComparison.Ordinal)
            || (folded.Contains("özkaynak", StringComparison.Ordinal) && !folded.Contains("özkaynaklar", StringComparison.Ordinal)))
        {
            return "OZKAYNAK";
        }

        return null;
    }

    private static string? MatchBalanceType(string folded)
    {
        if (folded.Contains("anapara alınan", StringComparison.Ordinal) || folded.Contains("principalreceived", StringComparison.Ordinal))
        {
            return "PRINCIPALRECEIVED";
        }

        if (folded.Contains("anapara ödenen", StringComparison.Ordinal) || folded.Contains("principalpaid", StringComparison.Ordinal))
        {
            return "PRINCIPALPAID";
        }

        if (folded.Contains("faiz alınan", StringComparison.Ordinal) || folded.Contains("interestreceived", StringComparison.Ordinal))
        {
            return "INTERESTRECEIVED";
        }

        if (folded.Contains("faiz ödenen", StringComparison.Ordinal) || folded.Contains("interestpaid", StringComparison.Ordinal))
        {
            return "INTERESTPAID";
        }

        if (Regex.IsMatch(folded, @"\btotal\b") || folded.Contains("toplam bakiye tipi", StringComparison.Ordinal))
        {
            return "TOTAL";
        }

        return null;
    }

    private static string? MatchBucket(string folded)
    {
        foreach (var (needle, column) in BucketHints)
        {
            if (folded.Contains(needle, StringComparison.Ordinal))
            {
                return column;
            }
        }

        return null;
    }

    private static string? MatchDate(string folded)
    {
        var iso = Regex.Match(folded, @"\b(20\d{2}-\d{2}-\d{2})\b");
        if (iso.Success)
        {
            return iso.Groups[1].Value;
        }

        var tr = Regex.Match(folded, @"\b(\d{2})\.(\d{2})\.(20\d{2})\b");
        if (tr.Success)
        {
            return $"{tr.Groups[3].Value}-{tr.Groups[2].Value}-{tr.Groups[1].Value}";
        }

        var compact = Regex.Match(folded, @"\b(20\d{6})\b");
        if (compact.Success
            && DateTime.TryParseExact(compact.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return null;
    }

    private static FuzzyLineItem? MatchLineItemFuzzy(string folded, List<string> tokens)
    {
        FuzzyLineItem? best = null;
        foreach (var item in LineItems)
        {
            var phraseTokens = Tokenize(item.Phrase);
            if (phraseTokens.Count == 0)
            {
                continue;
            }

            var windowMatch = BestTokenWindow(tokens, phraseTokens);
            if (windowMatch is not null)
            {
                best = Better(best, new FuzzyLineItem(item, windowMatch.Value.Text, windowMatch.Value.Distance, windowMatch.Value.Score));
            }

            var concatMatch = ConcatSimilarity(folded, item.Phrase);
            if (concatMatch is not null)
            {
                best = Better(best, new FuzzyLineItem(item, concatMatch.Value.Text, concatMatch.Value.Distance, concatMatch.Value.Score));
            }
        }

        if (best is null || best.Score > 0.34)
        {
            return null;
        }

        return best;
    }

    private static (string Text, int Distance, double Score)? BestTokenWindow(List<string> tokens, List<string> phraseTokens)
    {
        (string Text, int Distance, double Score)? best = null;
        if (tokens.Count < phraseTokens.Count)
        {
            return null;
        }

        for (var i = 0; i <= tokens.Count - phraseTokens.Count; i++)
        {
            var distance = 0;
            var length = 0;
            var ok = true;
            for (var j = 0; j < phraseTokens.Count; j++)
            {
                var actual = Stem(tokens[i + j]);
                var expected = Stem(phraseTokens[j]);
                var d = Levenshtein(actual, expected);
                var allow = phraseTokens.Count == 1 ? 1 : Math.Max(1, expected.Length / 3);
                if (d > allow)
                {
                    ok = false;
                    break;
                }

                distance += d;
                length += Math.Max(actual.Length, expected.Length);
            }

            if (!ok)
            {
                continue;
            }

            var score = length == 0 ? 1 : (double)distance / length;
            var text = string.Join(" ", tokens.Skip(i).Take(phraseTokens.Count));
            if (best is null || score < best.Value.Score)
            {
                best = (text, distance, score);
            }
        }

        return best;
    }

    private static (string Text, int Distance, double Score)? ConcatSimilarity(string folded, string phrase)
    {
        var leftover = string.Concat(Tokenize(folded)
            .Where(t => !NoiseTokens.Contains(t) && !Regex.IsMatch(t, @"^\d+$"))
            .Select(Stem));
        var expected = string.Concat(Tokenize(phrase).Select(Stem));
        if (leftover.Length < 5 || expected.Length < 5)
        {
            return null;
        }

        var distance = Levenshtein(leftover, expected);
        var score = (double)distance / Math.Max(leftover.Length, expected.Length);
        if (score > 0.34)
        {
            return null;
        }

        return (leftover, distance, score);
    }

    private static FuzzyLineItem Better(FuzzyLineItem? current, FuzzyLineItem candidate)
    {
        if (current is null)
        {
            return candidate;
        }

        if (candidate.Score < current.Score - 0.02)
        {
            return candidate;
        }

        if (Math.Abs(candidate.Score - current.Score) <= 0.02
            && candidate.Item.Phrase.Length > current.Item.Phrase.Length)
        {
            return candidate;
        }

        return current;
    }

    private static string? MatchApproachFuzzy(List<string> tokens, List<string> corrections)
    {
        foreach (var token in tokens)
        {
            var stem = Stem(token);
            if (Levenshtein(stem, "likidite") <= 2 && stem.Length >= 5)
            {
                corrections.Add($"«{token}» → likidite");
                return "Liquidity";
            }

            if (Levenshtein(stem, "liquidity") <= 2 && stem.Length >= 6)
            {
                corrections.Add($"«{token}» → liquidity");
                return "Liquidity";
            }
        }

        return null;
    }

    private static string? MatchCcyFuzzy(List<string> tokens, List<string> corrections)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = Stem(tokens[i]);
            if (i + 1 < tokens.Count)
            {
                var pair = token + " " + Stem(tokens[i + 1]);
                if (Levenshtein(pair, "türk lirası") <= 3 || Levenshtein(pair, "turk lirasi") <= 3)
                {
                    corrections.Add($"«{tokens[i]} {tokens[i + 1]}» → Türk lirası");
                    return "TRY";
                }
            }

            if (Levenshtein(token, "dolar") <= 1 && token.Length >= 4)
            {
                corrections.Add($"«{tokens[i]}» → dolar");
                return "USD";
            }
        }

        return null;
    }

    private static string? MatchPoolFuzzy(List<string> tokens, List<string> corrections)
    {
        foreach (var token in tokens)
        {
            var stem = Stem(token);
            if (Levenshtein(stem, "katılma") <= 2 && stem.Length >= 5)
            {
                corrections.Add($"«{token}» → katılma");
                return "KATILMA";
            }
        }

        return null;
    }

    private static string BuildSuggestedQuestion(
        LineItem item,
        string? approach,
        string? ccy,
        string? pool,
        string? date,
        string? metric,
        string? bucket,
        DateScanMode dateScan,
        bool tenorDistribution = false)
    {
        var parts = new List<string>();
        if (approach == "Liquidity") parts.Add("likidite");
        if (approach == "Rate") parts.Add("kar payı yaklaşımı");
        if (ccy is not null) parts.Add(ccy);
        if (pool == "KATILMA") parts.Add("katılma havuzu");
        if (pool == "OZKAYNAK") parts.Add("özkaynak havuzu");
        parts.Add(item.HeaderValue);
        parts.Add(DatePhrase(date, dateScan));
        if (metric is not null)
        {
            parts.Add(metric.ToLowerInvariant().Replace('_', ' '));
        }
        else if (tenorDistribution)
        {
            parts.Add("vade dilimlerine göre dağılım");
        }
        else
        {
            parts.Add(bucket is not null ? $"{bucket} bakiyesi" : "toplam bakiye");
        }

        return string.Join(", ", parts) + " nedir?";
    }

    private static string BuildSummary(
        LineItem item,
        string? approach,
        string? ccy,
        string? pool,
        string? date,
        string balance,
        string? metric,
        DateScanMode dateScan,
        bool tenorDistribution = false)
    {
        var bits = new List<string>
        {
            item.HeaderValue,
            DatePhrase(date, dateScan),
            balance
        };
        if (approach is not null) bits.Insert(0, approach);
        if (ccy is not null) bits.Add(ccy);
        if (pool is not null) bits.Add(pool);
        if (metric is not null) bits.Add(metric);
        if (tenorDistribution) bits.Add("vade dilimleri");
        return string.Join(" · ", bits);
    }

    private static List<FollowUpSuggestionDto> BuildFollowUps(
        LineItem item,
        string? approach,
        string? ccy,
        string? pool,
        string? date,
        DateScanMode dateScan,
        Breakdown breakdown,
        bool tenorDistribution,
        string? metric)
    {
        if (metric is not null)
        {
            return [];
        }

        var list = new List<FollowUpSuggestionDto>();
        var stem = FollowUpStem(item, approach, ccy, pool, date, dateScan);

        if (ccy is null && !breakdown.HasFlag(Breakdown.Ccy))
        {
            list.Add(new FollowUpSuggestionDto
            {
                Label = "Döviz koduna göre",
                Question = $"{stem}, döviz koduna göre toplam bakiye nedir?"
            });
        }

        if (pool is null && !breakdown.HasFlag(Breakdown.Pool))
        {
            list.Add(new FollowUpSuggestionDto
            {
                Label = "Havuz tipine göre",
                Question = $"{stem}, havuz tipine göre toplam bakiye nedir?"
            });
        }

        if (!breakdown.HasFlag(Breakdown.TpYp))
        {
            list.Add(new FollowUpSuggestionDto
            {
                Label = "TP / YP ayrımı",
                Question = $"{stem}, TP YP ayrımına göre toplam bakiye nedir?"
            });
        }

        if (!tenorDistribution
            && NextHeaderColumn(item.HeaderColumn) is not null
            && !breakdown.HasFlag(Breakdown.Child))
        {
            list.Add(new FollowUpSuggestionDto
            {
                Label = "Alt kalemlere göre",
                Question = $"{stem}, alt kalemlere göre toplam bakiye nedir?"
            });
        }

        if (!breakdown.HasFlag(Breakdown.Child)
            && ChildrenByHeader.TryGetValue(item.HeaderValue, out var children))
        {
            foreach (var child in children)
            {
                list.Add(new FollowUpSuggestionDto
                {
                    Label = child,
                    Question = $"{ApproachPhrase(approach)}{child}, {DatePhrase(date, dateScan)} toplam bakiyesi nedir?"
                });
            }
        }

        return list;
    }

    private static string FollowUpStem(
        LineItem item,
        string? approach,
        string? ccy,
        string? pool,
        string? date,
        DateScanMode dateScan)
    {
        var parts = new List<string>();
        if (approach == "Liquidity") parts.Add("likidite");
        if (approach == "Rate") parts.Add("kar payı yaklaşımı");
        if (ccy is not null) parts.Add(ccy);
        if (pool == "KATILMA") parts.Add("katılma havuzu");
        if (pool == "OZKAYNAK") parts.Add("özkaynak havuzu");
        parts.Add(item.HeaderValue);
        parts.Add(DatePhrase(date, dateScan));
        return string.Join(", ", parts);
    }

    private static string ApproachPhrase(string? approach) => approach switch
    {
        "Liquidity" => "likidite, ",
        "Rate" => "kar payı yaklaşımı, ",
        _ => string.Empty
    };

    private static string DatePhrase(string? date, DateScanMode dateScan)
    {
        if (date is not null)
        {
            return $"{date} tarihinde";
        }

        return dateScan switch
        {
            DateScanMode.Highest => "tüm rapor tarihlerinde tutarı en yüksek tarih",
            DateScanMode.Lowest => "tüm rapor tarihlerinde tutarı en düşük tarih",
            DateScanMode.Series => "tüm rapor tarihleri",
            _ => "son rapor tarihinde"
        };
    }

    private static List<string> Tokenize(string text) =>
        TokenRx.Matches(text).Select(m => m.Value).ToList();

    private static string Stem(string token)
    {
        foreach (var suffix in StemSuffixes)
        {
            if (token.Length > suffix.Length + 2 && token.EndsWith(suffix, StringComparison.Ordinal))
            {
                return token[..^suffix.Length];
            }
        }

        return token;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a == b)
        {
            return 0;
        }

        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }

    private static string HeaderEquals(string column, string value) =>
        $"map.{column} COLLATE Latin1_General_CI_AI = N'{Escape(value)}' COLLATE Latin1_General_CI_AI";

    private static string Fold(string question) => question.ToLower(Tr);

    private static string Escape(string value) => value.Replace("'", "''");

    private sealed record LineItem(string Phrase, string HeaderColumn, string HeaderValue, string? CoaContains = null);

    private sealed record FuzzyLineItem(LineItem Item, string MatchedText, int Distance, double Score);
}
