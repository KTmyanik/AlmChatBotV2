using System.Globalization;
using System.Text;

namespace AlmChatBot.Api.Services;

public interface IAlmBulletinService
{
    Task<AlmBulletinDto> BuildAsync(CancellationToken cancellationToken);
}

public sealed class AlmBulletinService(
    ISqlGuardrailService guardrail,
    IAlmDbService db,
    ILogger<AlmBulletinService> logger) : IAlmBulletinService
{
    public async Task<AlmBulletinDto> BuildAsync(CancellationToken cancellationToken)
    {
        var irDate = await ScalarDateAsync(
            "SELECT MAX(REPORTING_DATE) AS D FROM [ALM].[InternalReports]",
            cancellationToken) ?? throw new InvalidOperationException("InternalReports tarihi okunamadı.");

        var drDate = await ScalarDateAsync(
            "SELECT MAX(REPORTING_DATE) AS D FROM [ALM].[InternalDurationReports]",
            cancellationToken);

        var prior = await ScalarDateAsync(
            $"""
             SELECT MAX(REPORTING_DATE) AS D
             FROM [ALM].[InternalReports]
             WHERE REPORTING_DATE < '{irDate}'
             """,
            cancellationToken);

        logger.LogInformation("ALM bülten derleniyor. IR={Ir} DR={Dr}", irDate, drDate);

        var header1 = await QueryAsync(LiquidityHeaderSql(irDate, shortTenor: false), cancellationToken);
        var header1Short = await QueryAsync(LiquidityHeaderSql(irDate, shortTenor: true), cancellationToken);
        var obsKids = await QueryAsync(ObsChildrenSql(irDate), cancellationToken);
        var obsPool = await QueryAsync(ObsPoolSql(irDate), cancellationToken);
        var priorH1 = prior is null ? [] : await QueryAsync(LiquidityHeaderSql(prior, shortTenor: false), cancellationToken);

        IReadOnlyList<Dictionary<string, object?>> durationH2 = [];
        IReadOnlyList<Dictionary<string, object?>> durationH1 = [];
        if (drDate is not null)
        {
            durationH2 = await QueryAsync(DurationHeaderSql(drDate, "Header2"), cancellationToken);
            durationH1 = await QueryAsync(DurationHeaderSql(drDate, "Header1"), cancellationToken);
        }

        var facts = new AlmBulletinFacts
        {
            IrDate = irDate,
            DrDate = drDate,
            PriorDate = prior,
            TryAssets = SumHeader(header1, "VARLIKLAR", "TRY"),
            TryLiabilities = SumHeader(header1, "YÜKÜMLÜLÜKLER", "TRY"),
            TryObs = SumHeader(header1, "BİLANÇO DIŞI İŞLEMLER", "TRY"),
            TryShortAssets = SumHeader(header1Short, "VARLIKLAR", "TRY"),
            TryShortLiabilities = SumHeader(header1Short, "YÜKÜMLÜLÜKLER", "TRY"),
            TryShortObs = SumHeader(header1Short, "BİLANÇO DIŞI İŞLEMLER", "TRY"),
            TryObsKatilma = SumPool(obsPool, "KATILMA"),
            TryObsOzkaynak = SumPool(obsPool, "OZKAYNAK"),
            PriorTryNet = SumHeader(priorH1, "VARLIKLAR", "TRY")
                          + SumHeader(priorH1, "YÜKÜMLÜLÜKLER", "TRY")
                          + SumHeader(priorH1, "BİLANÇO DIŞI İŞLEMLER", "TRY"),
            FxNets = new[] { "USD", "EUR", "XAU", "XAG", "DGR" }
                .Select(ccy => (
                    Ccy: ccy,
                    Net: SumHeader(header1, "VARLIKLAR", ccy)
                         + SumHeader(header1, "YÜKÜMLÜLÜKLER", ccy)
                         + SumHeader(header1, "BİLANÇO DIŞI İŞLEMLER", ccy)))
                .Where(x => x.Net != 0)
                .OrderByDescending(x => Math.Abs(x.Net))
                .ToList(),
            ObsChildren = obsKids
                .Select(r => (Kalem: Read(r, "Kalem") ?? "?", Amount: Dec(r, "Tutar")))
                .Where(x => x.Kalem != "?")
                .ToList(),
            LoanDuration = Dur(durationH2, "KREDİLER", "ModifiedDuration"),
            LoanBalance = Dur(durationH2, "KREDİLER", "Bakiye"),
            DepositDuration = Dur(durationH2, "MEVDUAT", "ModifiedDuration"),
            DepositBalance = Dur(durationH2, "MEVDUAT", "Bakiye"),
            SecuritiesDuration = Dur(durationH2, "MENKUL KIYMETLER", "ModifiedDuration"),
            SecuritiesBalance = Dur(durationH2, "MENKUL KIYMETLER", "Bakiye"),
            AssetDuration = Dur(durationH1, "VARLIKLAR", "ModifiedDuration"),
            LiabilityDuration = Dur(durationH1, "YÜKÜMLÜLÜKLER", "ModifiedDuration"),
            AssetPv01 = Dur(durationH1, "VARLIKLAR", "Pv01"),
            LiabilityPv01 = Dur(durationH1, "YÜKÜMLÜLÜKLER", "Pv01")
        };

        return AlmBulletinNarrative.Render(facts);
    }

    private async Task<IReadOnlyList<Dictionary<string, object?>>> QueryAsync(string sql, CancellationToken cancellationToken)
    {
        var guarded = guardrail.ValidateAndRewrite(sql);
        return await db.QueryAsync(guarded.SafeSql, cancellationToken);
    }

    private async Task<string?> ScalarDateAsync(string sql, CancellationToken cancellationToken)
    {
        var rows = await QueryAsync(sql, cancellationToken);
        return rows.Select(r => Read(r, "D")).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static string IrBase(string date) =>
        $"""
         FROM [ALM].[InternalReports] AS ir
         INNER JOIN [ALM].[InternalReportMap] AS map ON ir.ALMCOACODE = map.AlmCoaCode
         WHERE ir.REPORTING_DATE = '{date}'
           AND ir.APPROACH_CODE = N'Liquidity'
           AND ir.BALANCE_TYPE = N'TOTAL'
           AND ir.ALMCOACODE IS NOT NULL
           AND LTRIM(RTRIM(ir.ALMCOACODE)) <> N''
         """;

    private static string LiquidityHeaderSql(string date, bool shortTenor)
    {
        var amount = shortTenor ? AlmSqlPlanner.ShortTenorSumSql() : AlmSqlPlanner.TenorSumSql();
        return $"""
            SELECT ir.CCY_CODE, map.Header1 AS Kalem, SUM({amount}) AS Tutar
            {IrBase(date)}
            GROUP BY ir.CCY_CODE, map.Header1
            """;
    }

    private static string ObsChildrenSql(string date) =>
        $"""
         SELECT map.Header2 AS Kalem, SUM({AlmSqlPlanner.TenorSumSql()}) AS Tutar
         {IrBase(date)}
           AND ir.CCY_CODE = N'TRY'
           AND {AlmSqlPlanner.HeaderMatchSql("Header1", "BİLANÇO DIŞI İŞLEMLER")}
           AND map.Header2 IS NOT NULL
           AND LTRIM(RTRIM(map.Header2)) <> N''
         GROUP BY map.Header2
         """;

    private static string ObsPoolSql(string date) =>
        $"""
         SELECT ir.POOL_TYPE, SUM({AlmSqlPlanner.TenorSumSql()}) AS Tutar
         {IrBase(date)}
           AND ir.CCY_CODE = N'TRY'
           AND {AlmSqlPlanner.HeaderMatchSql("Header1", "BİLANÇO DIŞI İŞLEMLER")}
         GROUP BY ir.POOL_TYPE
         """;

    private static string DurationHeaderSql(string date, string headerColumn) =>
        $"""
         SELECT map.{headerColumn} AS Kalem,
                SUM(dr.OUTSTANDING_BALANCE) AS Bakiye,
                SUM(dr.OUTSTANDING_BALANCE * dr.MODIFIED_DURATION) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS ModifiedDuration,
                SUM(dr.PV01_REPORTING_CCY) AS Pv01
         FROM [ALM].[InternalDurationReports] AS dr
         INNER JOIN [ALM].[InternalReportMap] AS map ON dr.ALMCOACODE = map.AlmCoaCode
         WHERE dr.REPORTING_DATE = '{date}'
           AND dr.ALMCOACODE IS NOT NULL
           AND LTRIM(RTRIM(dr.ALMCOACODE)) <> N''
           AND map.{headerColumn} IS NOT NULL
         GROUP BY map.{headerColumn}
         """;

    private static decimal SumHeader(IReadOnlyList<Dictionary<string, object?>> rows, string header, string ccy) =>
        rows.Where(r => Eq(Read(r, "CCY_CODE"), ccy) && HeaderEq(Read(r, "Kalem"), header)).Sum(r => Dec(r, "Tutar"));

    private static decimal SumPool(IReadOnlyList<Dictionary<string, object?>> rows, string pool) =>
        rows.Where(r => Eq(Read(r, "POOL_TYPE"), pool)).Sum(r => Dec(r, "Tutar"));

    private static decimal Dur(IReadOnlyList<Dictionary<string, object?>> rows, string header, string key)
    {
        var matches = rows.Where(r => HeaderEq(Read(r, "Kalem"), header)).ToList();
        var exact = matches.FirstOrDefault(r => Fold(Read(r, "Kalem") ?? "") == Fold(header));
        var row = exact ?? matches.FirstOrDefault();
        return row is null ? 0 : Dec(row, key);
    }

    private static bool HeaderEq(string? actual, string expected) =>
        actual is not null && (Fold(actual) == Fold(expected) || Fold(actual).Contains(Fold(expected)));

    private static bool Eq(string? a, string b) =>
        a is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static string Fold(string s)
    {
        var n = s.Normalize(NormalizationForm.FormD);
        var chars = n.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark);
        return string.Concat(chars).Replace('ı', 'i').Replace('İ', 'I').ToUpperInvariant();
    }

    private static string? Read(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? value?.ToString() : null;

    private static decimal Dec(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? ToDecimal(value) : 0;

    private static decimal ToDecimal(object? value) => value switch
    {
        null => 0,
        decimal d => d,
        double x => (decimal)x,
        float f => (decimal)f,
        int i => i,
        long l => l,
        string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) => p,
        string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.GetCultureInfo("tr-TR"), out var tr) => tr,
        _ => 0
    };
}
