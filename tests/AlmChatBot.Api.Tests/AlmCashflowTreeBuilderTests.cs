using AlmChatBot.Api.Models;
using AlmChatBot.Api.Services;

namespace AlmChatBot.Api.Tests;

public sealed class AlmCashflowTreeBuilderTests
{
    [Fact]
    public void Rolls_header2_into_header1_and_computes_gaps()
    {
        var leaves = new List<CashflowLeafRow>
        {
            Leaf(["VARLIKLAR", "KREDİLER"], 100, 20),
            Leaf(["VARLIKLAR", "MENKUL KIYMETLER"], 50, 5),
            Leaf(["YÜKÜMLÜLÜKLER", "MEVDUAT"], -80, -40),
            Leaf(["BİLANÇO DIŞI İŞLEMLER", "TAAHHÜTLER"], -10, -2),
            Leaf(["VARLIKLAR", "TÜREV FİNANSAL VARLIKLAR"], 8, 8),
            Leaf(["YÜKÜMLÜLÜKLER", "TÜREV FİNANSAL YÜKÜMLÜLÜKLER"], -3, -3)
        };

        var report = AlmCashflowTreeBuilder.Build(leaves, new AlmCashflowRequest { Depth = 2, Approach = "Liquidity" }, "2026-06-30");

        Assert.Equal(191m, report.Kpis.Assets);
        Assert.Equal(-126m, report.Kpis.Liabilities);
        Assert.Equal(-12m, report.Kpis.OffBalance);
        Assert.Equal(65m, report.Kpis.OnBalanceGap);
        Assert.Equal(53m, report.Kpis.TotalGap);
        Assert.Equal(10m, report.Kpis.DerivativesNet);
        Assert.Equal(158m, report.Chart.Assets[0]);
        Assert.Equal(53m, report.Chart.CumulativeGap[^1]);
        Assert.Contains(report.Tree, n => n.Children.Any(c => c.Label == "KREDİLER"));
    }

    [Fact]
    public void Redistributes_core_deposits_using_bucket_rates()
    {
        var leaf = Leaf(["YÜKÜMLÜLÜKLER", "MEVDUAT"], 100, 0, coa: "TP/P/MEV/C/G/TRY", ccy: "TRY");
        leaf.Amounts[2] = 50;
        AlmCashflowTreeBuilder.ApplyCoreDeposits(
            [leaf],
            [
                new CoreDepositRateRow { AlmCoaCode = "TP/P/MEV/C/G/TRY", Currency = "TRY", ReportBucket = "DAY_1", Rate = 0.4m },
                new CoreDepositRateRow { AlmCoaCode = "TP/P/MEV/C/G/TRY", Currency = "TRY", ReportBucket = "YEAR_20_PLUS", Rate = 0.6m }
            ]);

        Assert.Equal(60m, leaf.Amounts[0]);
        Assert.Equal(0m, leaf.Amounts[1]);
        Assert.Equal(90m, leaf.Amounts[^1]);
        Assert.Equal(150m, leaf.Amounts.Sum());
    }

    [Fact]
    public void Duration_parent_reweights_by_balance()
    {
        var tree = AlmCashflowTreeBuilder.BuildDuration(
        [
            new DurationLeafRow
            {
                Header1 = "VARLIKLAR",
                Header2 = "KREDİLER",
                ToplamBakiye = 80,
                WeightedMod = 2,
                ToplamPv01Try = 10
            },
            new DurationLeafRow
            {
                Header1 = "VARLIKLAR",
                Header2 = "MENKUL KIYMETLER",
                ToplamBakiye = 20,
                WeightedMod = 6,
                ToplamPv01Try = 4
            }
        ]);

        var assets = Assert.Single(tree);
        Assert.Equal(100m, assets.ToplamBakiye);
        Assert.Equal(14m, assets.ToplamPv01Try);
        Assert.Equal(2.8m, assets.AgirlikliModDuration);
    }

    private static CashflowLeafRow Leaf(string[] headers, decimal day1, decimal day2, string coa = "X", string ccy = "TRY")
    {
        var amounts = new decimal[AlmTenorCatalog.Count];
        amounts[0] = day1;
        amounts[1] = day2;
        var all = new string?[7];
        for (var i = 0; i < headers.Length; i++)
        {
            all[i] = headers[i];
        }

        return new CashflowLeafRow
        {
            Headers = all,
            AlmCoaCode = coa,
            CcyCode = ccy,
            SortId = 1,
            Amounts = amounts
        };
    }
}
