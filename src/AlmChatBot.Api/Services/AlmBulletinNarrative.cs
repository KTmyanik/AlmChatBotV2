using System.Globalization;
using System.Text;

namespace AlmChatBot.Api.Services;

public sealed class AlmBulletinDto
{
    public string Date { get; init; } = "";
    public string? DurationDate { get; init; }
    public List<string> Situation { get; init; } = [];
    public List<string> Risks { get; init; } = [];
}

public sealed class AlmBulletinFacts
{
    public string IrDate { get; init; } = "";
    public string? DrDate { get; init; }
    public string? PriorDate { get; init; }
    public decimal TryAssets { get; init; }
    public decimal TryLiabilities { get; init; }
    public decimal TryObs { get; init; }
    public decimal TryNet => TryAssets + TryLiabilities + TryObs;
    public decimal TryShortAssets { get; init; }
    public decimal TryShortLiabilities { get; init; }
    public decimal TryShortObs { get; init; }
    public decimal TryShortNet => TryShortAssets + TryShortLiabilities + TryShortObs;
    public decimal TryObsKatilma { get; init; }
    public decimal TryObsOzkaynak { get; init; }
    public decimal PriorTryNet { get; init; }
    public List<(string Ccy, decimal Net)> FxNets { get; init; } = [];
    public List<(string Name, decimal Amount)> ObsChildren { get; init; } = [];
    public decimal LoanDuration { get; init; }
    public decimal LoanBalance { get; init; }
    public decimal DepositDuration { get; init; }
    public decimal DepositBalance { get; init; }
    public decimal SecuritiesDuration { get; init; }
    public decimal SecuritiesBalance { get; init; }
    public decimal AssetDuration { get; init; }
    public decimal LiabilityDuration { get; init; }
    public decimal AssetPv01 { get; init; }
    public decimal LiabilityPv01 { get; init; }
}

public static class AlmBulletinNarrative
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static AlmBulletinDto Render(AlmBulletinFacts f)
    {
        var situation = new List<string>
        {
            $"Kapsam: InternalReports {f.IrDate}, Liquidity / TOTAL. Duration raporu {f.DrDate ?? "yok"} (InternalDurationReports)."
        };

        situation.Add($"TRY likidite nakit akışı (tüm vade dilimleri): varlık {Tl(f.TryAssets)}, yükümlülük {Tl(f.TryLiabilities)}, bilanço dışı {Tl(f.TryObs)}, net {Tl(f.TryNet)}.");
        situation.Add($"TRY 1–30 gün: varlık {Tl(f.TryShortAssets)}, yükümlülük {Tl(f.TryShortLiabilities)}, OBS {Tl(f.TryShortObs)}, net {Tl(f.TryShortNet)}.");
        situation.Add($"TRY OBS havuz: katılma {Tl(f.TryObsKatilma)}, özkaynak {Tl(f.TryObsOzkaynak)}.");

        if (f.ObsChildren.Count > 0)
        {
            var parts = string.Join("; ", f.ObsChildren.OrderByDescending(x => Math.Abs(x.Amount)).Select(x => $"{x.Name} {Tl(x.Amount)}"));
            situation.Add($"TRY OBS kırılımı: {parts}.");
        }

        if (f.DrDate is not null)
        {
            situation.Add($"Duration (bakiye ağırlıklı modified): krediler {Yr(f.LoanDuration)} (bakiye {Tl(f.LoanBalance)}), mevduat {Yr(f.DepositDuration)} (bakiye {Tl(f.DepositBalance)}), menkul kıymet {Yr(f.SecuritiesDuration)}.");
            situation.Add($"Bilanço duration: varlık {Yr(f.AssetDuration)} vs yükümlülük {Yr(f.LiabilityDuration)}; fark {Yr(f.AssetDuration - f.LiabilityDuration)}. PV01 (raporlama): varlık {f.AssetPv01.ToString("N0", Tr)}, yükümlülük {f.LiabilityPv01.ToString("N0", Tr)}.");
        }

        if (!string.IsNullOrEmpty(f.PriorDate))
        {
            var delta = f.TryNet - f.PriorTryNet;
            situation.Add($"Önceki rapor {f.PriorDate} TRY net {Tl(f.PriorTryNet)}; değişim {Tl(delta)}.");
        }

        if (f.FxNets.Count > 0)
        {
            situation.Add("Diğer döviz net likidite akışı: " + string.Join("; ", f.FxNets.Select(x => $"{x.Ccy} {Tl(x.Net)}")) + ".");
        }

        var risks = new List<string>();
        if (f.TryNet < 0)
        {
            risks.Add($"TRY tüm vade net nakit açığı {Tl(f.TryNet)}. Bu stok değil; sözleşmeye dayalı çıkışın girişten büyük olduğu anlamına gelir.");
        }

        if (f.TryShortNet < 0)
        {
            var share = f.TryNet == 0 ? 0 : 100m * f.TryShortNet / f.TryNet;
            risks.Add($"TRY 1–30 gün net açık {Tl(f.TryShortNet)}. Operasyonel likidite baskısı önde" +
                      (f.TryNet < 0 ? $" (tüm vade açığının %{Math.Abs(share):N0}'i kısa vadede)." : "."));
        }

        if (f.TryAssets != 0 && Math.Abs(f.TryObs) / Math.Abs(f.TryAssets) >= 0.08m)
        {
            risks.Add($"TRY OBS, varlık nakit akışının %{(100m * Math.Abs(f.TryObs) / Math.Abs(f.TryAssets)).ToString("N1", Tr)}'i. Bilanço dışı dönüşüm / türev nakit akışı likidite planının merkezinde.");
        }

        var obsAbs = Math.Abs(f.TryObsKatilma) + Math.Abs(f.TryObsOzkaynak);
        if (obsAbs > 0 && Math.Abs(f.TryObsOzkaynak) / obsAbs >= 0.8m)
        {
            risks.Add("TRY OBS nakit akışı özkaynak havuzunda yoğunlaşmış; katılma (müşteri fonu) bu kesitte yük taşımıyor. Stres altında banka özkaynağı fonlamak zorunda kalır.");
        }

        var tfa = f.ObsChildren.FirstOrDefault(x => x.Name.Contains("TÜREV", StringComparison.OrdinalIgnoreCase)
                                                   || x.Name.Contains("TUREV", StringComparison.OrdinalIgnoreCase));
        if (tfa.Name is not null && obsAbs > 0 && Math.Abs(tfa.Amount) / Math.Max(Math.Abs(f.TryObs), 1) >= 0.7m)
        {
            risks.Add($"OBS içinde türev payı yüksek ({Tl(tfa.Amount)}). Marj, değişim teminatı ve vade ödemeleri kısa vadeli likiditeyi şişirebilir.");
        }

        if (f.DrDate is not null && f.LoanDuration > 0 && f.DepositDuration > 0)
        {
            var gap = f.LoanDuration - f.DepositDuration;
            if (gap >= 1.5m)
            {
                risks.Add($"Süre uyumsuzluğu: kredi modified duration {Yr(f.LoanDuration)}, mevduat {Yr(f.DepositDuration)} (fark {Yr(gap)}). Faiz/kâr payı artışında aktifler pasiften yavaş yeniden fiyatlanır; marj ve ekonomik değer baskısı.");
            }
        }

        if (f.DrDate is not null && f.AssetDuration - f.LiabilityDuration >= 1.2m)
        {
            risks.Add($"Bilanço duration gap {Yr(f.AssetDuration - f.LiabilityDuration)} (varlık {Yr(f.AssetDuration)} > yükümlülük {Yr(f.LiabilityDuration)}). Pozitif gap: faiz düşüşünde değer artışı, yükselişte değer kaybı.");
        }

        if (!string.IsNullOrEmpty(f.PriorDate) && f.TryNet < f.PriorTryNet && f.TryNet < 0)
        {
            risks.Add($"TRY net açık önceki döneme göre derinleşti ({Tl(f.PriorTryNet)} → {Tl(f.TryNet)}).");
        }

        foreach (var fx in f.FxNets.Where(x => Math.Abs(x.Net) >= Math.Abs(f.TryNet) * 0.15m && Math.Abs(x.Net) > 1_000_000m))
        {
            risks.Add($"{fx.Ccy} net likidite akışı {Tl(fx.Net)}; TRY ile aynı ölçekte izlenmeli (kur likiditesi / swap ihtiyacı).");
        }

        if (risks.Count == 0)
        {
            risks.Add("Bu kesitte kural tabanlı eşik aşan bir kırmızı bayrak yok. Yine de kısa vade dilimi, havuz ve duration gap periyodik izlenmeli.");
        }

        return new AlmBulletinDto
        {
            Date = f.IrDate,
            DurationDate = f.DrDate,
            Situation = situation,
            Risks = risks
        };
    }

    private static string Tl(decimal v) => v.ToString("N2", Tr) + " TL";

    private static string Yr(decimal v) => v.ToString("N2", Tr) + " yıl";
}
