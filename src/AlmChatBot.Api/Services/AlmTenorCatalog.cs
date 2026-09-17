using AlmChatBot.Api.Models;

namespace AlmChatBot.Api.Services;

public static class AlmTenorCatalog
{
    public static int Count => AlmSqlPlanner.TenorColumns.Length;

    public static IReadOnlyList<AlmTenorInfoDto> Infos { get; } = AlmSqlPlanner.TenorColumns
        .Select(id => new AlmTenorInfoDto
        {
            Id = id,
            Label = Label(id),
            Group = Group(id)
        })
        .ToArray();

    public static string Label(string id) => id switch
    {
        "DAY_1" => "Gün 1",
        "DAY_2" => "Gün 2",
        "DAY_3" => "Gün 3",
        "DAY_4" => "Gün 4",
        "DAY_5" => "Gün 5",
        "DAY_6" => "Gün 6",
        "DAY_7" => "Gün 7",
        "DAY_8_15" => "8–15 gün",
        "DAY_16_30" => "16–30 gün",
        "MONTH_1_2" => "1–2 ay",
        "MONTH_2_3" => "2–3 ay",
        "MONTH_3_4" => "3–4 ay",
        "MONTH_4_5" => "4–5 ay",
        "MONTH_5_6" => "5–6 ay",
        "MONTH_6_9" => "6–9 ay",
        "MONTH_9_12" => "9–12 ay",
        "MONTH_12_18" => "12–18 ay",
        "MONTH_18_24" => "18–24 ay",
        "YEAR_2_3" => "2–3 yıl",
        "YEAR_3_4" => "3–4 yıl",
        "YEAR_4_5" => "4–5 yıl",
        "YEAR_5_6" => "5–6 yıl",
        "YEAR_6_7" => "6–7 yıl",
        "YEAR_7_8" => "7–8 yıl",
        "YEAR_8_9" => "8–9 yıl",
        "YEAR_9_10" => "9–10 yıl",
        "YEAR_10_15" => "10–15 yıl",
        "YEAR_15_20" => "15–20 yıl",
        "YEAR_20_PLUS" => "20+ yıl",
        _ => id
    };

    public static string Group(string id)
    {
        if (id.StartsWith("DAY_8", StringComparison.Ordinal) || id.StartsWith("DAY_16", StringComparison.Ordinal))
        {
            return "Hafta";
        }

        if (id.StartsWith("DAY_", StringComparison.Ordinal))
        {
            return "Gün";
        }

        if (id.StartsWith("MONTH_", StringComparison.Ordinal))
        {
            return "Ay";
        }

        return "Yıl";
    }
}
