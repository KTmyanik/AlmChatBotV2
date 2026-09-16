using AlmChatBot.Api.Services;

namespace AlmChatBot.Api.Tests;

public sealed class AlmBulletinNarrativeTests
{
    [Fact]
    public void Flags_short_gap_equity_obs_and_duration_mismatch()
    {
        var dto = AlmBulletinNarrative.Render(new AlmBulletinFacts
        {
            IrDate = "2026-06-30",
            DrDate = "2026-06-30",
            PriorDate = "2026-05-31",
            TryAssets = 800_000_000_000m,
            TryLiabilities = -900_000_000_000m,
            TryObs = -55_000_000_000m,
            TryShortAssets = 10_000_000_000m,
            TryShortLiabilities = -40_000_000_000m,
            TryShortObs = -30_000_000_000m,
            TryObsKatilma = 0,
            TryObsOzkaynak = -55_000_000_000m,
            PriorTryNet = -100_000_000_000m,
            ObsChildren = [("TÜREV FİNANSAL ARAÇLAR", -54_000_000_000m), ("TAAHHÜTLER", -1_000_000_000m)],
            LoanDuration = 4.2m,
            LoanBalance = 500_000_000_000m,
            DepositDuration = 1.1m,
            DepositBalance = 600_000_000_000m,
            AssetDuration = 3.8m,
            LiabilityDuration = 1.4m
        });

        Assert.NotEmpty(dto.Situation);
        Assert.Contains(dto.Situation, s => s.Contains("InternalDurationReports", StringComparison.Ordinal));
        Assert.Contains(dto.Risks, s => s.Contains("1–30", StringComparison.Ordinal) || s.Contains("1-30", StringComparison.Ordinal));
        Assert.Contains(dto.Risks, s => s.Contains("özkaynak", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(dto.Risks, s => s.Contains("duration", StringComparison.OrdinalIgnoreCase)
                                         || s.Contains("Süre uyumsuzluğu", StringComparison.Ordinal));
    }
}
