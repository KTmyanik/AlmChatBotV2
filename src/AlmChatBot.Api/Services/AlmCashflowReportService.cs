using System.Globalization;
using AlmChatBot.Api.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AlmChatBot.Api.Services;

public interface IAlmCashflowReportService
{
    Task<AlmReportFiltersDto> FiltersAsync(CancellationToken cancellationToken);

    Task<AlmCashflowReportDto> CashflowAsync(AlmCashflowRequest request, CancellationToken cancellationToken);

    Task<AlmDurationReportDto> DurationAsync(string? date, CancellationToken cancellationToken);
}

public sealed class AlmCashflowReportService(
    IAlmDbService db,
    IMemoryCache cache,
    ILogger<AlmCashflowReportService> logger) : IAlmCashflowReportService
{
    private static readonly HashSet<string> Approaches = new(StringComparer.OrdinalIgnoreCase) { "Liquidity", "Rate" };
    private static readonly HashSet<string> Currencies = new(StringComparer.OrdinalIgnoreCase) { "TRY", "USD", "EUR", "XAU", "XAG", "DGR" };
    private static readonly HashSet<string> Pools = new(StringComparer.OrdinalIgnoreCase) { "KATILMA", "OZKAYNAK" };
    private static readonly HashSet<string> BalanceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "TOTAL", "PRINCIPALRECEIVED", "PRINCIPALPAID", "INTERESTRECEIVED", "INTERESTPAID"
    };

    public async Task<AlmReportFiltersDto> FiltersAsync(CancellationToken cancellationToken)
    {
        return await cache.GetOrCreateAsync("alm-report-filters", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            var dates = await DistinctAsync(
                "SELECT DISTINCT CONVERT(varchar(10), REPORTING_DATE, 23) AS V FROM [ALM].[InternalReports] ORDER BY 1 DESC",
                cancellationToken);
            var ccys = await DistinctAsync(
                "SELECT DISTINCT CCY_CODE AS V FROM [ALM].[InternalReports] WHERE CCY_CODE IS NOT NULL ORDER BY 1",
                cancellationToken);
            var pools = await DistinctAsync(
                "SELECT DISTINCT POOL_TYPE AS V FROM [ALM].[InternalReports] WHERE POOL_TYPE IS NOT NULL ORDER BY 1",
                cancellationToken);
            var balances = await DistinctAsync(
                "SELECT DISTINCT BALANCE_TYPE AS V FROM [ALM].[InternalReports] WHERE BALANCE_TYPE IS NOT NULL ORDER BY 1",
                cancellationToken);
            var approaches = await DistinctAsync(
                "SELECT DISTINCT APPROACH_CODE AS V FROM [ALM].[InternalReports] WHERE APPROACH_CODE IS NOT NULL ORDER BY 1",
                cancellationToken);

            return new AlmReportFiltersDto
            {
                Dates = dates,
                DefaultDate = dates.FirstOrDefault(),
                Approaches = approaches.Count > 0 ? approaches : ["Liquidity", "Rate"],
                Currencies = ccys,
                Pools = pools,
                BalanceTypes = balances.Count > 0 ? balances : ["TOTAL"],
                Tenors = AlmTenorCatalog.Infos
            };
        }) ?? new AlmReportFiltersDto { Tenors = AlmTenorCatalog.Infos };
    }

    public async Task<AlmCashflowReportDto> CashflowAsync(AlmCashflowRequest request, CancellationToken cancellationToken)
    {
        var approach = Normalize(request.Approach, Approaches, "Liquidity");
        var balance = Normalize(request.BalanceType, BalanceTypes, "TOTAL");
        var ccy = NormalizeOptional(request.Ccy, Currencies);
        var pools = (request.Pools ?? [])
            .Select(p => NormalizeOptional(p, Pools))
            .Where(p => p is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var date = await ResolveDateAsync(request.Date, cancellationToken);
        var tenorSelect = string.Join(",\n              ",
            AlmSqlPlanner.TenorColumns.Select(c => $"SUM(ISNULL(ir.{c}, 0)) AS {c}"));

        var sql = $"""
            SELECT
              map.Header1, map.Header2, map.Header3, map.Header4, map.Header5, map.Header6, map.Header7,
              ir.ALMCOACODE AS AlmCoaCode,
              ir.CCY_CODE AS CcyCode,
              MIN(map.RowId) AS SortId,
              {tenorSelect}
            FROM [ALM].[InternalReports] AS ir
            INNER JOIN [ALM].[InternalReportMap] AS map
              ON ir.ALMCOACODE = map.AlmCoaCode
            WHERE ir.REPORTING_DATE = @ReportingDate
              AND ir.APPROACH_CODE = @Approach
              AND ir.BALANCE_TYPE = @BalanceType
              AND ir.ALMCOACODE IS NOT NULL
              AND LTRIM(RTRIM(ir.ALMCOACODE)) <> N''
              {(ccy is null ? "" : "AND ir.CCY_CODE = @Ccy")}
              {PoolSql(pools)}
            GROUP BY map.Header1, map.Header2, map.Header3, map.Header4, map.Header5, map.Header6, map.Header7,
                     ir.ALMCOACODE, ir.CCY_CODE
            """;

        logger.LogInformation("ALM nakit akış raporu çekiliyor. Date={Date} Approach={Approach}", date, approach);
        var rows = await db.QueryAsync(sql, new
        {
            ReportingDate = date,
            Approach = approach,
            BalanceType = balance,
            Ccy = ccy,
            Pool = pools.Count == 1 ? pools[0] : null
        }, cancellationToken);

        var leaves = rows.Select(ToLeaf).ToList();
        if (request.CoreDeposits)
        {
            var rates = await LoadRatesAsync(cancellationToken);
            AlmCashflowTreeBuilder.ApplyCoreDeposits(leaves, rates);
        }

        var normalized = new AlmCashflowRequest
        {
            Date = date,
            Approach = approach,
            Ccy = ccy,
            Pools = pools,
            BalanceType = balance,
            CoreDeposits = request.CoreDeposits,
            Depth = request.Depth
        };

        return AlmCashflowTreeBuilder.Build(leaves, normalized, date);
    }

    public async Task<AlmDurationReportDto> DurationAsync(string? date, CancellationToken cancellationToken)
    {
        var reportingDate = await ResolveDurationDateAsync(date, cancellationToken);
        var sql = """
            SELECT
              map.Header1,
              map.Header2,
              MIN(map.RowId) AS SortId,
              SUM(dr.OUTSTANDING_BALANCE) AS Toplam_Bakiye,
              SUM(dr.PV01_REPORTING_CCY) AS Toplam_PV01_TRY,
              SUM(dr.MODIFIED_DURATION * dr.OUTSTANDING_BALANCE) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS Agirlikli_Mod_Duration,
              SUM(dr.MACAULAY_DURATION * dr.OUTSTANDING_BALANCE) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS Agirlikli_Mac_Duration,
              SUM(dr.YIELD_TO_MATURITY * dr.OUTSTANDING_BALANCE) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS Agirlikli_YTM,
              SUM(dr.CONVEXITY * dr.OUTSTANDING_BALANCE) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS Agirlikli_Convexity,
              SUM(dr.REMAINING_LIFE * dr.OUTSTANDING_BALANCE) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS Agirlikli_Kalan_Omur,
              SUM(dr.COMPARABLE_YIELD * dr.OUTSTANDING_BALANCE) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS Agirlikli_Gosterge_Getiri
            FROM [ALM].[InternalDurationReports] AS dr
            INNER JOIN [ALM].[InternalReportMap] AS map
              ON dr.ALMCOACODE = map.AlmCoaCode
            WHERE dr.REPORTING_DATE = @ReportingDate
              AND dr.ALMCOACODE IS NOT NULL
              AND LTRIM(RTRIM(dr.ALMCOACODE)) <> N''
            GROUP BY map.Header1, map.Header2
            """;

        var rows = await db.QueryAsync(sql, new { ReportingDate = reportingDate }, cancellationToken);
        var leaves = rows.Select(ToDurationLeaf).ToList();
        return new AlmDurationReportDto
        {
            ReportingDate = reportingDate,
            Tree = AlmCashflowTreeBuilder.BuildDuration(leaves)
        };
    }

    private async Task<IReadOnlyList<CoreDepositRateRow>> LoadRatesAsync(CancellationToken cancellationToken)
    {
        return await cache.GetOrCreateAsync("alm-core-deposit-rates", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(15);
            var rows = await db.QueryAsync(
                """
                SELECT ALMCoaCode, Currency, ReportBucket, Rate
                FROM [ALM].[CoreDepositRates]
                """,
                cancellationToken);
            return rows.Select(r => new CoreDepositRateRow
            {
                AlmCoaCode = Read(r, "ALMCoaCode") ?? string.Empty,
                Currency = Read(r, "Currency") ?? string.Empty,
                ReportBucket = Read(r, "ReportBucket") ?? string.Empty,
                Rate = Dec(r, "Rate")
            }).ToList();
        }) ?? [];
    }

    private async Task<string> ResolveDateAsync(string? date, CancellationToken cancellationToken)
    {
        if (TryParseDate(date, out var parsed))
        {
            return parsed;
        }

        var filters = await FiltersAsync(cancellationToken);
        return filters.DefaultDate ?? throw new InvalidOperationException("InternalReports tarihi okunamadı.");
    }

    private async Task<string> ResolveDurationDateAsync(string? date, CancellationToken cancellationToken)
    {
        if (TryParseDate(date, out var parsed))
        {
            return parsed;
        }

        var rows = await db.QueryAsync(
            "SELECT CONVERT(varchar(10), MAX(REPORTING_DATE), 23) AS V FROM [ALM].[InternalDurationReports]",
            cancellationToken);
        return Read(rows[0], "V") ?? throw new InvalidOperationException("InternalDurationReports tarihi okunamadı.");
    }

    private async Task<IReadOnlyList<string>> DistinctAsync(string sql, CancellationToken cancellationToken)
    {
        var rows = await db.QueryAsync(sql, cancellationToken);
        return rows.Select(r => Read(r, "V")).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
    }

    private static string PoolSql(IReadOnlyList<string> pools)
    {
        if (pools.Count is 0 or 2)
        {
            return string.Empty;
        }

        return "AND ir.POOL_TYPE = @Pool";
    }

    private static CashflowLeafRow ToLeaf(Dictionary<string, object?> row)
    {
        var amounts = new decimal[AlmTenorCatalog.Count];
        for (var i = 0; i < AlmSqlPlanner.TenorColumns.Length; i++)
        {
            amounts[i] = Dec(row, AlmSqlPlanner.TenorColumns[i]);
        }

        return new CashflowLeafRow
        {
            Headers =
            [
                Read(row, "Header1"),
                Read(row, "Header2"),
                Read(row, "Header3"),
                Read(row, "Header4"),
                Read(row, "Header5"),
                Read(row, "Header6"),
                Read(row, "Header7")
            ],
            AlmCoaCode = Read(row, "AlmCoaCode") ?? string.Empty,
            CcyCode = Read(row, "CcyCode") ?? string.Empty,
            SortId = (int)Dec(row, "SortId"),
            Amounts = amounts
        };
    }

    private static DurationLeafRow ToDurationLeaf(Dictionary<string, object?> row) =>
        new()
        {
            Header1 = Read(row, "Header1"),
            Header2 = Read(row, "Header2"),
            SortId = (int)Dec(row, "SortId"),
            ToplamBakiye = Dec(row, "Toplam_Bakiye"),
            ToplamPv01Try = Dec(row, "Toplam_PV01_TRY"),
            WeightedMod = Dec(row, "Agirlikli_Mod_Duration"),
            WeightedMac = Dec(row, "Agirlikli_Mac_Duration"),
            WeightedYtm = Dec(row, "Agirlikli_YTM"),
            WeightedConvexity = Dec(row, "Agirlikli_Convexity"),
            WeightedLife = Dec(row, "Agirlikli_Kalan_Omur"),
            WeightedComparable = Dec(row, "Agirlikli_Gosterge_Getiri")
        };

    private static string Normalize(string? value, HashSet<string> allow, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        var match = allow.FirstOrDefault(x => x.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? fallback;
    }

    private static string? NormalizeOptional(string? value, HashSet<string> allow)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("All", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return allow.FirstOrDefault(x => x.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParseDate(string? value, out string iso)
    {
        iso = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            iso = parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            return true;
        }

        return false;
    }

    private static string? Read(Dictionary<string, object?> row, string key) =>
        row.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;

    private static decimal Dec(Dictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null)
        {
            return 0;
        }

        return value switch
        {
            decimal d => d,
            double x => (decimal)x,
            float f => (decimal)f,
            int i => i,
            long l => l,
            short s => s,
            byte b => b,
            _ => decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Any, CultureInfo.InvariantCulture, out var n) ? n : 0
        };
    }
}
