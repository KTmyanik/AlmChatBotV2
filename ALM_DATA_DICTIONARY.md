# ALM veri sözlüğü (Text-to-SQL)

Kaynak: `[IFRSStaging].[ALM]` canlı tablo değerleri. Kolon anlamları katalogdandır. Örnek/uydurma kod yazma; aşağıdaki DISTINCT listeleri ve Header eşlemesini kullan.

## Şema ve join (zorunlu)

Veritabanı: `IFRSStaging`. Şema: `ALM`.

| Tablo | Rol |
|---|---|
| `InternalReports` | Nakit akışı / gap; vade kovası kolonları |
| `InternalDurationReports` | Duration, YTM, convexity, PV01 |
| `InternalReportMap` | 7 kademeli hiyerarşi (Header1–Header7) + `AlmCoaCode` |
| `CoreDepositRates` | Çekirdek mevduat dağılım oranları (tarihsiz) |

**Join yalnızca ALMCOACODE iledir. RowId / ROW_ID kullanılmaz.**

```sql
INNER JOIN [ALM].[InternalReportMap] AS map
    ON ir.ALMCOACODE = map.AlmCoaCode
```

Duration için aynı kural: `dr.ALMCOACODE = map.AlmCoaCode`.

CoreDepositRates: `cd.ALMCoaCode = ir.ALMCOACODE AND cd.Currency = ir.CCY_CODE AND cd.ReportBucket = '<kova kolon adı>'` (ör. `DAY_1`).

## Hiyerarşi kuralı

Kullanıcı bir kalem adı sorduğunda `InternalReportMap` içinde Header1–Header7 eşleştir.
`AlmCoaCode` boş olan satır başlıktır; toplanmaz.
Yapraklar: `AlmCoaCode IS NOT NULL AND LTRIM(RTRIM(AlmCoaCode)) <> ''`.
Fact sorgusu: `ALMCOACODE IN (yaprağın kodları)` veya map join + Header filtresi.

Örnek — “TÜREV FİNANSAL ARAÇLAR” (`Header1` = `BİLANÇO DIŞI İŞLEMLER`, `Header2` = `TÜREV FİNANSAL ARAÇLAR`):

- `BD/TFA/IRS`
- `BD/TFA/CCS`
- `BD/TFA/VI`
- `BD/TFA/FXSWAP`

Karıştırma:

| İfade | Header | Kodlar |
|---|---|---|
| TÜREV FİNANSAL ARAÇLAR | BİLANÇO DIŞI / Header2 | yukarıdaki 4 kod |
| TÜREV FİNANSAL VARLIKLAR | VARLIKLAR / Header2 | `TP/A/TFV`, `YP/A/TFV` |
| TÜREV FİNANSAL YÜKÜMLÜLÜKLER | YÜKÜMLÜLÜKLER / Header2 | `TP/P/TFY`, `YP/P/TFY` |
| TÜREV FİNANSAL ARAÇLAR (NET) | ayrı Header1; fact ALMCOACODE’da yok | çocuk kalemlerden hesapla, bu string’i kod sanma |

Aynı şekilde `BİL. İÇİ NET AÇIK/FAZLA` ve `TOPLAM NET AÇIK/FAZLA` map’te başlık adını `AlmCoaCode` gibi taşır; InternalReports fact kodunda yoktur. Çocuk kalemlerden hesaplanır.

Fact satırlarının ~%35’inde `ALMCOACODE` boştur (hiyerarşi başlığı). `SUM` yaparken boş kodları hariç tut; aksi halde çift sayım olur.

## InternalReports — kolonlar

Ana nakit akışı / repricing tablosu.

| Kolon | Anlam |
|---|---|
| PARTITION_KEY | Dönem anahtarı (bigint, `YYYYMMDDHHMM`, ör. 202606300000) |
| REPORTING_DATE | Rapor tarihi |
| ROW_ID | Satır numarası (join anahtarı değil) |
| DESCRIPTION | Kalem açıklaması |
| ALMCOACODE | ALM hesap kodu; map.AlmCoaCode ile eşleşir |
| APPROACH_CODE | Yaklaşım |
| CCY_CODE | Para birimi |
| POOL_TYPE | Havuz |
| BALANCE_TYPE | Bakiye niteliği |
| DAY_1 … DAY_7 | 1.–7. gün kovası |
| DAY_8_15, DAY_16_30 | Haftalık / 16–30 gün |
| MONTH_1_2 … MONTH_18_24 | Ay kovaları |
| YEAR_2_3 … YEAR_20_PLUS | Yıl kovaları |

### DISTINCT (canlı)

- APPROACH_CODE: `Liquidity`, `Rate`
- POOL_TYPE: `KATILMA`, `OZKAYNAK` (Ö harfi yok)
- BALANCE_TYPE: `TOTAL`, `PRINCIPALRECEIVED`, `PRINCIPALPAID`, `INTERESTRECEIVED`, `INTERESTPAID`
- CCY_CODE: `TRY`, `USD`, `EUR`, `XAU`, `XAG`, `DGR` — farklı dövizler toplanmaz
- REPORTING_DATE (örnek kesit): `2026-05-31`, `2026-06-30`

Türkçe eşleme (filtreye yazılacak kod):

- Likidite → `Liquidity`
- Kar payı / faiz yaklaşımı → `Rate`
- Katılma → `KATILMA`
- Özkaynak → `OZKAYNAK`
- Toplam → `TOTAL`
- Anapara alınan / ödenen → `PRINCIPALRECEIVED` / `PRINCIPALPAID`
- Kar/faiz alınan / ödenen → `INTERESTRECEIVED` / `INTERESTPAID`

## Küp filtresi (InternalReports)

Tablo her kesitte tam hiyerarşiyi taşır. Soru belirtmezse:

- `REPORTING_DATE = (SELECT MAX(REPORTING_DATE) FROM [ALM].[InternalReports])`
- `BALANCE_TYPE = N'TOTAL'`
- `APPROACH_CODE = N'Liquidity'` (soruda Kar payı / Rate yoksa)
- `CCY_CODE`, `POOL_TYPE` sorudan doldurulur; yoksa varsayma, Assumptions’a yaz veya soruyu daralt.
- Döviz belirtilmeden `SUM` across CCY_CODE yapma.

## InternalDurationReports — kolonlar

| Kolon | Anlam |
|---|---|
| ROW_ID | Rapor sırası (tarihe göre tekrar eder; PK/join değil) |
| PARTITION_KEY | Dönem anahtarı |
| REPORTING_DATE | Rapor tarihi (daha uzun tarih serisi) |
| DESCRIPTION | Kalem adı |
| ALMCOACODE | map.AlmCoaCode |
| OUTSTANDING_BALANCE | Anapara / risk bakiyesi |
| YIELD_TO_MATURITY | YTM |
| MACAULAY_DURATION | Macaulay duration (yıl) |
| MODIFIED_DURATION | Modified duration |
| CONVEXITY | Konveksite |
| COMPARABLE_YIELD | Karşılaştırmalı getiri |
| PV01_DEAL_CCY | İşlem cinsi PV01 |
| PV01_REPORTING_CCY | Raporlama cinsi PV01 |
| REMAINING_LIFE | Kalan ömür |

Yaprak: `dr.ALMCOACODE IS NOT NULL`. Tarih yoksa `MAX(REPORTING_DATE)` (tarih taraması soruları hariç).

### Portföy ve üst kırılım (Header1..7) agregasyon kuralları

Kullanıcı belirli bir hiyerarşi kırılımında (Header1, Header2, … Header7) veya portföy genelinde risk metriklerini istediğinde aşağıdaki agregasyon kuralları zorunludur. Alias: `InternalDurationReports` → `dr`. `AVG` kullanılmaz. Boş `ALMCOACODE` satırı toplanmaz.

1. **Bakiye (ağırlık)**

```sql
SUM(dr.OUTSTANDING_BALANCE) AS Toplam_Bakiye
```

2. **Doğrudan toplanacak parasal risk kolonları (SUM)**

`PV01` kolonlarının ağırlıklı ortalaması alınmaz; doğrudan `SUM` edilir.

```sql
SUM(dr.PV01_REPORTING_CCY) AS Toplam_PV01_TRY
```

3. **Bakiye ağırlıklı ortalaması alınacak kolonlar**

Sıfıra bölünmeyi engellemek için her zaman `NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0)` kullanılır:

```sql
SUM(dr.MODIFIED_DURATION * dr.OUTSTANDING_BALANCE) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS Agirlikli_Mod_Duration
SUM(dr.MACAULAY_DURATION * dr.OUTSTANDING_BALANCE) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS Agirlikli_Mac_Duration
SUM(dr.YIELD_TO_MATURITY * dr.OUTSTANDING_BALANCE) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS Agirlikli_YTM
SUM(dr.CONVEXITY * dr.OUTSTANDING_BALANCE) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS Agirlikli_Convexity
SUM(dr.REMAINING_LIFE * dr.OUTSTANDING_BALANCE) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS Agirlikli_Kalan_Omur
SUM(dr.COMPARABLE_YIELD * dr.OUTSTANDING_BALANCE) / NULLIF(SUM(dr.OUTSTANDING_BALANCE), 0) AS Agirlikli_Gosterge_Getiri
```

`GROUP BY` kırılımı sorudaki Header seviyesine (ve tarihe) göre kurulur.

## InternalReportMap — kolonlar

| Kolon | Anlam |
|---|---|
| RowId | Hiyerarşi sıra (join yok) |
| AlmCoaCode | Yaprak kod veya bazı toplam başlıklarında başlık metni |
| Header1 | En üst bölüm |
| Header2 … Header7 | Alt kırılımlar |

Header1 değerleri: `VARLIKLAR`, `YÜKÜMLÜLÜKLER`, `BİLANÇO DIŞI İŞLEMLER`, `TÜREV FİNANSAL ARAÇLAR (NET)`, `BİL. İÇİ NET AÇIK/FAZLA`, `TOPLAM NET AÇIK/FAZLA`.

Header2 (yaprağı olanlar):

- VARLIKLAR: `NAKİT VE NAKİT BENZERLERİ`, `PARA PİYASALARINDAN ALACAKLAR`, `MENKUL KIYMETLER`, `ORTAKLIK YATIRIMLARI`, `KREDİLER`, `TAKİPTEKİ ALACAKLAR`, `TÜREV FİNANSAL VARLIKLAR`, `DİĞER AKTİF`, `BEKLENEN ZARAR KARŞILIKLARI-NAKDİ`
- YÜKÜMLÜLÜKLER: `MEVDUAT`, `ALINAN KREDİLER`, `PARA PİYASALARINA BORÇLAR - REPO`, `TÜREV FİNANSAL YÜKÜMLÜLÜKLER`, `SERMAYE BENZERİ BORÇLANMA ARAÇLARI`, `DİĞER PASİFLER`, `ÖZKAYNAKLAR`, `BEKLENEN ZARAR KARŞILIKLARI-G.NAKDİ`
- BİLANÇO DIŞI İŞLEMLER: `GARANTİ VE KEFALETLER`, `TAAHHÜTLER`, `TÜREV FİNANSAL ARAÇLAR`

Kod ezberleme: Header filtresi + join. Kodları map’ten seç.

## CoreDepositRates — kolonlar

Tarihsiz dağılım; `Rate` toplamı (ALMCoaCode + Currency) ≈ 1.

| Kolon | Anlam |
|---|---|
| ReportBucket | Vade kovası adı (`DAY_1`, `MONTH_1_2`, … `YEAR_20_PLUS`) |
| Rate | 0–1 dağılım oranı |
| ALMCoaCode | Mevduat yaprak kodu |
| Currency | `TRY`, `USD`, `EUR`, `XAU`, `XAG`, `DGR` |

Canlı ALMCoaCode listesi:

- `TP/P/MEV/C/G/TRY`, `TP/P/MEV/C/T/TRY`
- `YP/P/MEV/C/G/DGR`, `YP/P/MEV/C/G/EUR`, `YP/P/MEV/C/G/USD`, `YP/P/MEV/C/G/XAU`
- `YP/P/MEV/C/T/DGR`, `YP/P/MEV/C/T/EUR`, `YP/P/MEV/C/T/USD`, `YP/P/MEV/C/T/XAU`

## SQL üretim kuralları

- Tek bir `SELECT`. DDL/DML/EXEC yok.
- Nesneler: `[ALM].[InternalReports]`, `[ALM].[InternalDurationReports]`, `[ALM].[InternalReportMap]`, `[ALM].[CoreDepositRates]`.
- Unicode metinlerde `N'...'`.
- `SELECT *` kullanma; Kovaları soruya göre seç.
- TOP (200) eklemeyi sisteme bırakabilirsin; varsa 200’ü aşma.
- JSON dışında bir şey yazma.
