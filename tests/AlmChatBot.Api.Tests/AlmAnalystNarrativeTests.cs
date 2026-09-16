using AlmChatBot.Api.Services;

namespace AlmChatBot.Api.Tests;

public sealed class AlmAnalystNarrativeTests
{
    [Fact]
    public void Interprets_obs_equity_pool_outflow_as_funding_concentration()
    {
        var text = AlmAnalystNarrative.Render(new AlmAnalysisFacts
        {
            Kalem = "BİLANÇO DIŞI İŞLEMLER",
            Date = "2026-06-30",
            PriorDate = "2026-05-31",
            Approach = "Liquidity",
            Ccy = "TRY",
            AllTenors = true,
            SubjectTotal = -55_603_422_634.90m,
            SubjectShort = -1_200_000_000m,
            Katilma = 0,
            Ozkaynak = -55_603_422_634.90m,
            Assets = 400_000_000_000m,
            Liabilities = -350_000_000_000m,
            OffBalance = -55_603_422_634.90m,
            PriorSubject = -40_000_000_000m,
            Children =
            [
                ("TÜREV FİNANSAL ARAÇLAR", -50_000_000_000m),
                ("GARANTİ VE KEFALETLER", -5_000_000_000m),
                ("TAAHHÜTLER", -603_422_634.90m)
            ]
        });

        Assert.Contains("net çıkış", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("özkaynak", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("katılma", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("TÜREV", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("önceki", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("bahwa", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ilk satır", text, StringComparison.OrdinalIgnoreCase);
    }
}
