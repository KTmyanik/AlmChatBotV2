using AlmChatBot.Api.Services;

namespace AlmChatBot.Api.Tests;

public sealed class AlmSqlPlannerTests
{
    private readonly AlmSqlPlanner _sut = new();

    [Fact]
    public void Plans_trading_book_as_header3_alim_satim()
    {
        var plan = _sut.TryPlan("Alım-satım portföyünün son rapor bakiyesi nedir?");

        Assert.NotNull(plan);
        Assert.Contains("Header3", plan.Sql);
        Assert.Contains("N'Alim-Satim'", plan.Sql);
        Assert.Contains("[ALM].[InternalReports]", plan.Sql);
        Assert.DoesNotContain("InternalDurationReports", plan.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PV01", plan.Sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plans_npl_as_nonperforming_loans()
    {
        var plan = _sut.TryPlan("NPL kaç");

        Assert.NotNull(plan);
        Assert.Contains("N'TAKİPTEKİ ALACAKLAR'", plan.Sql);
        Assert.Contains("[ALM].[InternalReports]", plan.Sql);
    }

    [Fact]
    public void Plans_htm_tlref_securities()
    {
        var plan = _sut.TryPlan("vadeye kadar elde tutulacak TLREF");

        Assert.NotNull(plan);
        Assert.Contains("N'Vadeye Kadar Elde Tutulacak'", plan.Sql);
        Assert.Contains("Header3", plan.Sql);
        Assert.Contains("LIKE N'%TLREF%'", plan.Sql);
        Assert.Contains("[ALM].[InternalReports]", plan.Sql);
    }

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

    [Fact]
    public void General_item_returns_headline_total_and_follow_ups()
    {
        var interpreted = _sut.Interpret("Kredilerin son rapor bakiyesi nedir?");

        Assert.NotNull(interpreted.Sql);
        Assert.DoesNotContain("ir.CCY_CODE", interpreted.Sql!.Sql);
        Assert.DoesNotContain("ir.POOL_TYPE", interpreted.Sql.Sql);
        Assert.Contains("Ana kırılımın toplamı", interpreted.Sql.Explanation);
        Assert.Contains(interpreted.FollowUps, f => f.Label == "Döviz koduna göre");
        Assert.Contains(interpreted.FollowUps, f => f.Label == "Havuz tipine göre");
        Assert.Contains(interpreted.FollowUps, f => f.Label.Contains("TP", StringComparison.Ordinal));
        Assert.Contains(interpreted.FollowUps, f => f.Label == "Alt kalemlere göre");

        var ccyPlan = _sut.TryPlan(interpreted.FollowUps.First(f => f.Label == "Döviz koduna göre").Question);
        Assert.NotNull(ccyPlan);
        Assert.Contains("ir.CCY_CODE", ccyPlan!.Sql);
        Assert.Contains("N'KREDİLER'", ccyPlan.Sql);
    }

    [Fact]
    public void Splits_loans_by_currency_when_requested()
    {
        var plan = _sut.TryPlan("Krediler döviz koduna göre toplam bakiye nedir?");

        Assert.NotNull(plan);
        Assert.Contains("ir.CCY_CODE", plan.Sql);
        Assert.DoesNotContain("ir.POOL_TYPE", plan.Sql);
        Assert.Contains("dövize göre kırıldı", string.Join(' ', plan.Assumptions));
    }

    [Fact]
    public void Splits_deposits_by_pool_when_requested()
    {
        var plan = _sut.TryPlan("Mevduat havuz tipine göre toplam bakiye nedir?");

        Assert.NotNull(plan);
        Assert.Contains("ir.POOL_TYPE", plan.Sql);
        Assert.Contains("N'MEVDUAT'", plan.Sql);
    }

    [Fact]
    public void Splits_deposits_by_tp_yp_prefix()
    {
        var plan = _sut.TryPlan("Mevduat TP YP ayrımına göre toplam bakiye nedir?");

        Assert.NotNull(plan);
        Assert.Contains("LIKE N'TP/%'", plan.Sql);
        Assert.Contains("AS TpYp", plan.Sql);
        Assert.Contains("N'MEVDUAT'", plan.Sql);
    }

    [Fact]
    public void Plans_assets_as_header1_total()
    {
        var interpreted = _sut.Interpret("Varlık bakiyesi nedir?");

        Assert.NotNull(interpreted.Sql);
        Assert.Contains("Header1", interpreted.Sql!.Sql);
        Assert.Contains("N'VARLIKLAR'", interpreted.Sql.Sql);
        Assert.DoesNotContain("ir.CCY_CODE", interpreted.Sql.Sql);
        Assert.Contains(interpreted.FollowUps, f => f.Label == "KREDİLER");
        Assert.Contains(interpreted.FollowUps, f => f.Label == "NAKİT VE NAKİT BENZERLERİ");
    }

    [Fact]
    public void Splits_assets_into_header2_children()
    {
        var plan = _sut.TryPlan("Varlık alt kalemlere göre toplam bakiye nedir?");

        Assert.NotNull(plan);
        Assert.Contains("N'VARLIKLAR'", plan.Sql);
        Assert.Contains("Header2 AS AltKalem", plan.Sql);
        Assert.Contains("map.Header2 IS NOT NULL", plan.Sql);
    }
}
