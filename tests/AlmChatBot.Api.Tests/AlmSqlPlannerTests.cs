using AlmChatBot.Api.Services;

namespace AlmChatBot.Api.Tests;

public sealed class AlmSqlPlannerTests
{
    private readonly AlmSqlPlanner _sut = new();

    [Fact]
    public void Plans_derivative_total_balance()
    {
        var plan = _sut.TryPlan("Türev finansal araçların son rapor tarihindeki toplam bakiyesi nedir?");

        Assert.NotNull(plan);
        Assert.Contains("TÜREV FİNANSAL ARAÇLAR", plan.Sql);
        Assert.Contains("InternalReports", plan.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MAX(REPORTING_DATE)", plan.Sql);
        Assert.Contains("DAY_1", plan.Sql);
        Assert.Contains("YEAR_20_PLUS", plan.Sql);
        Assert.DoesNotContain("RowId", plan.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COLLATE Latin1_General_CI_AI", plan.Sql);
        Assert.Contains("N'Liquidity'", plan.Sql);
    }

    [Fact]
    public void Defaults_approach_to_liquidity()
    {
        var plan = _sut.TryPlan("krediler tutarının en yüksek olduğu tarih ne");

        Assert.NotNull(plan);
        Assert.Contains("N'Liquidity'", plan.Sql);
        Assert.Contains("APPROACH_CODE belirtilmedi; N'Liquidity' alındı.", plan.Assumptions);
        Assert.DoesNotContain("ir.APPROACH_CODE,", plan.Sql);
    }

    [Fact]
    public void Plans_liquidity_try_katilma_loans_day1()
    {
        var plan = _sut.TryPlan("Likidite, TRY, katılma havuzu, son tarihte kredilerin GÜN 1 bakiyesi nedir?");

        Assert.NotNull(plan);
        Assert.Contains("N'Liquidity'", plan.Sql);
        Assert.Contains("N'TRY'", plan.Sql);
        Assert.Contains("N'KATILMA'", plan.Sql);
        Assert.Contains("N'KREDİLER'", plan.Sql);
        Assert.Contains("ir.DAY_1", plan.Sql);
        Assert.DoesNotContain("YEAR_20_PLUS", plan.Sql);
    }

    [Fact]
    public void Plans_weighted_modified_duration()
    {
        var plan = _sut.TryPlan("Son rapor tarihinde kredilerin modified duration'ı nedir?");

        Assert.NotNull(plan);
        Assert.Contains("InternalDurationReports", plan.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("MODIFIED_DURATION", plan.Sql);
        Assert.Contains("dr.MODIFIED_DURATION * dr.OUTSTANDING_BALANCE", plan.Sql);
        Assert.Contains("NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0)", plan.Sql);
        Assert.Contains("Toplam_Bakiye", plan.Sql);
        Assert.Contains("Agirlikli_Mod_Duration", plan.Sql);
        Assert.Contains("N'KREDİLER'", plan.Sql);
    }

    [Fact]
    public void Plans_pv01_as_sum_not_weighted_average()
    {
        var plan = _sut.TryPlan("Son rapor tarihinde kredilerin pv01'i nedir?");

        Assert.NotNull(plan);
        Assert.Contains("SUM(dr.PV01_REPORTING_CCY) AS Toplam_PV01_TRY", plan.Sql);
        Assert.Contains("Toplam_Bakiye", plan.Sql);
        Assert.DoesNotContain("PV01_REPORTING_CCY * dr.OUTSTANDING_BALANCE", plan.Sql);
    }

    [Fact]
    public void Plans_header_risk_metrics_bundle()
    {
        var plan = _sut.TryPlan("Kredilerin risk metrikleri nedir?");

        Assert.NotNull(plan);
        Assert.Contains("Toplam_PV01_TRY", plan.Sql);
        Assert.Contains("Agirlikli_YTM", plan.Sql);
        Assert.Contains("Agirlikli_Gosterge_Getiri", plan.Sql);
        Assert.Contains("Agirlikli_Kalan_Omur", plan.Sql);
    }

    [Fact]
    public void Asks_confirmation_for_typos_in_off_balance_question()
    {
        var interpreted = _sut.Interpret("toplam likidite bilnço dışı işlmler 20260630 tarihinde türk lirası");

        Assert.True(interpreted.NeedsConfirmation);
        Assert.NotNull(interpreted.Sql);
        Assert.Contains("BİLANÇO DIŞI İŞLEMLER", interpreted.SuggestedQuestion);
        Assert.Contains("2026-06-30", interpreted.SuggestedQuestion);
        Assert.Contains("TRY", interpreted.SuggestedQuestion);
        Assert.Contains("likidite", interpreted.SuggestedQuestion, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("N'Liquidity'", interpreted.Sql!.Sql);
        Assert.Contains("'2026-06-30'", interpreted.Sql.Sql);
        Assert.NotEmpty(interpreted.Corrections);
    }

    [Fact]
    public void Plans_loan_peak_across_reporting_dates()
    {
        var plan = _sut.TryPlan("krediler tutarının en yüksek olduğu tarih ne");

        Assert.NotNull(plan);
        Assert.Contains("N'KREDİLER'", plan.Sql);
        Assert.Contains("GROUP BY", plan.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ir.REPORTING_DATE", plan.Sql);
        Assert.Contains("ORDER BY Tutar DESC", plan.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("REPORTING_DATE = (SELECT MAX(REPORTING_DATE)", plan.Sql);
        Assert.Contains("tüm REPORTING_DATE", string.Join(' ', plan.Assumptions), StringComparison.OrdinalIgnoreCase);

        var guarded = new SqlGuardrailService().ValidateAndRewrite(plan.Sql);
        Assert.Contains("ORDER BY", guarded.SafeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("= (SELECT MAX([REPORTING_DATE])", guarded.SafeSql.Replace(" ", ""), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Still_uses_max_date_for_latest_snapshot()
    {
        var plan = _sut.TryPlan("Türev finansal araçların son rapor tarihindeki toplam bakiyesi nedir?");

        Assert.NotNull(plan);
        Assert.Contains("MAX(REPORTING_DATE)", plan.Sql);
        Assert.DoesNotContain("ORDER BY Tutar DESC", plan.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plans_loan_tenor_distribution()
    {
        var plan = _sut.TryPlan("Vade dilimlerine göre kredi bakiyesi dağılımı nasıl?");

        Assert.NotNull(plan);
        Assert.Contains("N'KREDİLER'", plan.Sql);
        Assert.Contains("CROSS APPLY", plan.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VadeDilimi", plan.Sql);
        Assert.Contains("N'DAY_1'", plan.Sql);
        Assert.Contains("N'YEAR_20_PLUS'", plan.Sql);
        Assert.Contains("N'Liquidity'", plan.Sql);
        var guarded = new SqlGuardrailService().ValidateAndRewrite(plan.Sql);
        Assert.Contains("VadeDilimi", guarded.SafeSql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Returns_null_when_no_line_item()
    {
        Assert.Null(_sut.TryPlan("Merhaba, bugün hava nasıl?"));
    }

    [Fact]
    public void Confirmed_canonical_question_does_not_ask_again()
    {
        var first = _sut.Interpret("toplam likidite bilnço dışı işlmler 20260630 tarihinde türk lirası");
        var second = _sut.Interpret(first.SuggestedQuestion);

        Assert.False(second.NeedsConfirmation);
        Assert.NotNull(second.Sql);
        Assert.Contains("BİLANÇO DIŞI İŞLEMLER", second.Sql!.Sql);
    }
}
