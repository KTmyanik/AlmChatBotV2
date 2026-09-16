using System.Globalization;
using System.Text;

namespace AlmChatBot.Api.Services;

public sealed class AlmAnalysisFacts
{
    public string Kalem { get; init; } = "";
    public string? Date { get; init; }
    public string? PriorDate { get; init; }
    public string? Approach { get; init; }
    public string? Ccy { get; init; }
    public string BalanceType { get; init; } = "TOTAL";
    public bool AllTenors { get; init; } = true;
    public decimal SubjectTotal { get; init; }
    public decimal SubjectShort { get; init; }
    public decimal Katilma { get; init; }
    public decimal Ozkaynak { get; init; }
    public decimal Assets { get; init; }
    public decimal Liabilities { get; init; }
    public decimal OffBalance { get; init; }
    public decimal? PriorSubject { get; init; }
    public List<(string Name, decimal Amount)> Children { get; init; } = [];
}

public static class AlmAnalystNarrative
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static string Render(AlmAnalysisFacts facts)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Kesit: {facts.Approach ?? "belirtilmedi"} · {facts.Ccy ?? "çoklu döviz"} · {facts.Date ?? "son tarih"} · {facts.BalanceType} · {facts.Kalem}.");
        sb.AppendLine();

        if (facts.AllTenors)
        {
            sb.AppendLine("Bu toplam, bilanço stoku değil; likidite raporundaki bütün vade dilimlerinin sözleşmeye dayalı nakit akışı toplamıdır. Negatif değer net nakit çıkışı (fonlama ihtiyacı), pozitif değer net nakit girişidir. Tek başına kâr/zarar hükmü taşımaz.");
        }
        else
        {
            sb.AppendLine("Sonuç tek bir vade dilimine aittir; tüm vade profilini temsil etmez.");
        }

        sb.AppendLine();
        sb.AppendLine($"Kalemin bu kesitteki toplamı {Tl(facts.SubjectTotal)}.");
        sb.AppendLine(SizeJudgement(facts));
        sb.AppendLine(PoolJudgement(facts));
        if (facts.AllTenors)
        {
            sb.AppendLine(HorizonJudgement(facts));
        }

        var vsBalance = BalanceSheetRelation(facts);
        if (!string.IsNullOrEmpty(vsBalance))
        {
            sb.AppendLine(vsBalance);
        }

        var children = ChildrenJudgement(facts);
        if (!string.IsNullOrEmpty(children))
        {
            sb.AppendLine(children);
        }

        var prior = PriorJudgement(facts);
        if (!string.IsNullOrEmpty(prior))
        {
            sb.AppendLine(prior);
        }

        sb.AppendLine();
        sb.AppendLine("İzleme: iç likidite limiti / iştah ile karşılaştırmadan 'iyi' veya 'kötü' denemez; ancak özkaynak havuzunda biriken net çıkış, katılma havuzunun boş kalması ve kısa vade payı birlikte stres senaryosunda fonlama yoğunlaşması işaretidir. GÜN 1–30 kırılımı ayrıca sorulmalıdır.");
        return sb.ToString().Trim();
    }

    private static string SizeJudgement(AlmAnalysisFacts facts)
    {
        var abs = Math.Abs(facts.SubjectTotal);
        if (abs == 0)
        {
            return "Kalem bu kesitte sıfır; likidite yükü veya katkısı yok. Bu 'iyi' değil, o havuz/dövizde pozisyon taşınmadığı anlamına gelir.";
        }

        var direction = facts.SubjectTotal < 0
            ? "net çıkış (ödeme yönlü nakit akışı) var"
            : "net giriş (tahsilat yönlü nakit akışı) var";

        if (facts.Assets != 0)
        {
            var share = 100m * abs / Math.Abs(facts.Assets);
            var scale = share switch
            {
                >= 25 => "bilanço içi varlık nakit akışına göre yüksek",
                >= 10 => "bilanço içi varlıklara göre belirgin",
                >= 3 => "ılımlı ölçekte",
                _ => "varlık toplamına göre sınırlı"
            };
            return $"Yön: {direction}. Ölçek: aynı kesitteki varlık nakit akışının %{share.ToString("N1", Tr)}'i kadar; {scale}.";
        }

        return $"Yön: {direction}. Varlık toplamı bu kesitte okunamadığı için göreli ölçek hesaplanamadı.";
    }

    private static string PoolJudgement(AlmAnalysisFacts facts)
    {
        var k = facts.Katilma;
        var o = facts.Ozkaynak;
        if (k == 0 && o == 0)
        {
            return "Havuz kırılımı yok veya her iki havuz da sıfır.";
        }

        var total = k + o;
        if (total == 0)
        {
            return $"Katılma {Tl(k)}, özkaynak {Tl(o)}; işaretler zıt olduğu için net {Tl(facts.SubjectTotal)}.";
        }

        var ozkShare = 100m * Math.Abs(o) / (Math.Abs(k) + Math.Abs(o));
        if (Math.Abs(k) < Math.Abs(o) * 0.05m)
        {
            return $"Havuz: katılma {Tl(k)}, özkaynak {Tl(o)}. OBS/kalem nakit akışının neredeyse tamamı (%{ozkShare.ToString("N0", Tr)}) özkaynak havuzunda. Katılma (müşteri fonu) bu kesitte yük taşımıyor; likidite baskısı banka özkaynağında toplanıyor. Bu, havuzlar arası hedge veya türev yerleşimi açısından izlenmeli.";
        }

        if (Math.Abs(o) < Math.Abs(k) * 0.05m)
        {
            return $"Havuz: yük katılma tarafında ({Tl(k)}); özkaynak {Tl(o)} ile ihmal edilebilir.";
        }

        return $"Havuz dağılımı: katılma {Tl(k)} (%{(100m * Math.Abs(k) / (Math.Abs(k) + Math.Abs(o))).ToString("N0", Tr)}), özkaynak {Tl(o)} (%{ozkShare.ToString("N0", Tr)}). Yük her iki havuza da yayılmış.";
    }

    private static string HorizonJudgement(AlmAnalysisFacts facts)
    {
        var abs = Math.Abs(facts.SubjectTotal);
        if (abs == 0)
        {
            return "Kısa vade (1–30 gün) payı hesaplanmadı (toplam sıfır).";
        }

        var shortShare = 100m * Math.Abs(facts.SubjectShort) / abs;
        if (shortShare >= 40)
        {
            return $"Vade: 1–30 gün nakit akışı {Tl(facts.SubjectShort)}; toplamın %{shortShare.ToString("N0", Tr)}'i kısa vadede. Operasyonel likidite (günlük/aylık) baskısı yapısal vadeden daha önde.";
        }

        if (shortShare <= 10)
        {
            return $"Vade: 1–30 gün {Tl(facts.SubjectShort)} (toplamın %{shortShare.ToString("N0", Tr)}'i). Çıkış/giriş ağırlıkla 1 aydan uzun vade dilimlerinde; acil nakit stresi bu toplamdan okunmaz, orta-uzun vade fonlama planı ile izlenir.";
        }

        return $"Vade: 1–30 gün {Tl(facts.SubjectShort)} (toplamın %{shortShare.ToString("N0", Tr)}'i); kısa ve uzun vade birlikte bakılmalı.";
    }

    private static string BalanceSheetRelation(AlmAnalysisFacts facts)
    {
        if (facts.Assets == 0 && facts.Liabilities == 0 && facts.OffBalance == 0)
        {
            return "";
        }

        var parts = new List<string>
        {
            $"Aynı kesitte varlık nakit akışı {Tl(facts.Assets)}, yükümlülük {Tl(facts.Liabilities)}, bilanço dışı {Tl(facts.OffBalance)}."
        };

        if (facts.Assets != 0 && facts.OffBalance != 0)
        {
            var obsVsAssets = 100m * Math.Abs(facts.OffBalance) / Math.Abs(facts.Assets);
            parts.Add($"Bilanço dışı / varlık oranı %{obsVsAssets.ToString("N1", Tr)}.");
            if (obsVsAssets >= 20)
            {
                parts.Add("OBS, bilanço içi varlık akışına göre büyük; türev ve gayrinakdi dönüşüm likidite planının merkezinde olmalı.");
            }
        }

        if (IsOffBalance(facts.Kalem) && facts.OffBalance != 0)
        {
            var share = 100m * Math.Abs(facts.SubjectTotal) / Math.Abs(facts.OffBalance);
            parts.Add($"Sorulan kalem, bilanço dışı toplamın %{share.ToString("N0", Tr)}'ini oluşturuyor.");
        }

        return string.Join(" ", parts);
    }

    private static string ChildrenJudgement(AlmAnalysisFacts facts)
    {
        if (facts.Children.Count == 0)
        {
            return "";
        }

        var ordered = facts.Children.OrderByDescending(x => Math.Abs(x.Amount)).ToList();
        var dominant = ordered[0];
        var den = ordered.Sum(x => Math.Abs(x.Amount));
        var share = den == 0 ? 0 : 100m * Math.Abs(dominant.Amount) / den;
        var list = string.Join("; ", ordered.Select(x => $"{x.Name} {Tl(x.Amount)}"));
        var hint = dominant.Name.Contains("TÜREV", StringComparison.OrdinalIgnoreCase)
            ? "Ağırlık türevdeyse piyasa/hedge nakit akışı (marj, değişim teminatı, vade ödemeleri) öne çıkar."
            : dominant.Name.Contains("GARANTİ", StringComparison.OrdinalIgnoreCase) || dominant.Name.Contains("TAAHHÜT", StringComparison.OrdinalIgnoreCase)
                ? "Ağırlık garanti/taahhütteyse gayrinakdi dönüşüm ve kredi-eşiği likiditesi izlenir."
                : "Alt kalem dağılımı yukarıdaki paylara göre okunmalı.";
        return $"Bilanço dışı kırılım: {list}. En büyük kalem {dominant.Name} (%{share.ToString("N0", Tr)}). {hint}";
    }

    private static string PriorJudgement(AlmAnalysisFacts facts)
    {
        if (facts.PriorSubject is null || string.IsNullOrEmpty(facts.PriorDate))
        {
            return "";
        }

        var prior = facts.PriorSubject.Value;
        var delta = facts.SubjectTotal - prior;
        if (prior == 0)
        {
            return $"Önceki rapor tarihi {facts.PriorDate}: {Tl(prior)}. Bu dönem {Tl(facts.SubjectTotal)}; sıfır tabandan değişim oranı yazılamaz.";
        }

        var pct = 100m * delta / Math.Abs(prior);
        var worseOutflow = facts.SubjectTotal < prior && facts.SubjectTotal < 0;
        var trend = worseOutflow
            ? "net çıkış derinleşti; likidite baskısı önceki döneme göre arttı"
            : delta > 0
                ? "nakit akışı önceki döneme göre giriş yönüne kaydı veya çıkış hafifledi"
                : "çıkış/giriş önceki döneme göre zayıfladı";
        return $"Önceki tarih {facts.PriorDate}: {Tl(prior)}. Değişim {Tl(delta)} (%{pct.ToString("N1", Tr)}). {trend}.";
    }

    private static bool IsOffBalance(string kalem) =>
        kalem.Contains("BİLANÇO DIŞI", StringComparison.OrdinalIgnoreCase)
        || kalem.Contains("BILANCO DISI", StringComparison.OrdinalIgnoreCase)
        || kalem.Contains("TÜREV FİNANSAL ARAÇLAR", StringComparison.OrdinalIgnoreCase);

    private static string Tl(decimal value) =>
        value.ToString("N2", Tr) + " TL";
}
