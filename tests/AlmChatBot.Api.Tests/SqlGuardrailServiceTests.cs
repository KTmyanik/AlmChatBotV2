using AlmChatBot.Api.Exceptions;
using AlmChatBot.Api.Services;

namespace AlmChatBot.Api.Tests;

public sealed class SqlGuardrailServiceTests
{
    private readonly SqlGuardrailService _sut = new();

    [Fact]
    public void Qualifies_unqualified_alm_tables_with_schema()
    {
        var result = _sut.ValidateAndRewrite("SELECT ALMCOACODE FROM InternalReports WHERE CCY_CODE = N'TRY'");

        Assert.Contains("ALM.InternalReports", result.SafeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FROM InternalReports", result.SafeSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Injects_top_200_on_plain_select()
    {
        var result = _sut.ValidateAndRewrite("SELECT ALMCOACODE FROM ALM.InternalReports WHERE CCY_CODE = N'TRY'");

        Assert.True(result.TopWasInjected);
        Assert.Contains("TOP", result.SafeSql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("200", result.SafeSql);
        Assert.Contains("InternalReports", result.SafeSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_insert()
    {
        var ex = Assert.Throws<SqlGuardrailException>(() =>
            _sut.ValidateAndRewrite("INSERT INTO ALM.InternalReports (ROW_ID) VALUES (1)"));

        Assert.Contains("SELECT", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_unknown_table()
    {
        Assert.Throws<SqlGuardrailException>(() =>
            _sut.ValidateAndRewrite("SELECT name FROM sys.tables"));
    }

    [Fact]
    public void Rejects_select_into()
    {
        Assert.Throws<SqlGuardrailException>(() =>
            _sut.ValidateAndRewrite("SELECT ALMCOACODE INTO dbo.Temp FROM ALM.InternalReports"));
    }

    [Fact]
    public void Allows_header_join_on_almcoacode()
    {
        const string sql = """
            SELECT map.Header2, SUM(ir.DAY_1) AS Day1
            FROM ALM.InternalReports AS ir
            INNER JOIN ALM.InternalReportMap AS map ON ir.ALMCOACODE = map.AlmCoaCode
            WHERE map.Header2 = N'TÜREV FİNANSAL ARAÇLAR'
              AND ir.ALMCOACODE IS NOT NULL
            GROUP BY map.Header2
            """;

        var result = _sut.ValidateAndRewrite(sql);
        Assert.Contains("AlmCoaCode", result.SafeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RowId", result.SafeSql, StringComparison.OrdinalIgnoreCase);
    }
}
