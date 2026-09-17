namespace AlmChatBot.Api.Models;

public sealed class AlmReportFiltersDto
{
    public IReadOnlyList<string> Dates { get; init; } = [];
    public string? DefaultDate { get; init; }
    public IReadOnlyList<string> Approaches { get; init; } = [];
    public IReadOnlyList<string> Currencies { get; init; } = [];
    public IReadOnlyList<string> Pools { get; init; } = [];
    public IReadOnlyList<string> BalanceTypes { get; init; } = [];
    public IReadOnlyList<AlmTenorInfoDto> Tenors { get; init; } = [];
}

public sealed class AlmTenorInfoDto
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Group { get; init; } = string.Empty;
}

public sealed class AlmCashflowRequest
{
    public string? Date { get; init; }
    public string Approach { get; init; } = "Liquidity";
    public string? Ccy { get; init; }
    public IReadOnlyList<string>? Pools { get; init; }
    public string BalanceType { get; init; } = "TOTAL";
    public bool CoreDeposits { get; init; }
    public int Depth { get; init; } = 2;
}

public sealed class AlmCashflowReportDto
{
    public string ReportingDate { get; init; } = string.Empty;
    public string Approach { get; init; } = string.Empty;
    public string BalanceType { get; init; } = string.Empty;
    public string? Ccy { get; init; }
    public IReadOnlyList<string> Pools { get; init; } = [];
    public bool CoreDeposits { get; init; }
    public int Depth { get; init; }
    public IReadOnlyList<AlmTenorInfoDto> Tenors { get; init; } = [];
    public AlmCashflowKpisDto Kpis { get; init; } = new();
    public IReadOnlyList<AlmCashflowNodeDto> Tree { get; init; } = [];
    public AlmCashflowChartDto Chart { get; init; } = new();
}

public sealed class AlmCashflowKpisDto
{
    public decimal Assets { get; init; }
    public decimal Liabilities { get; init; }
    public decimal OffBalance { get; init; }
    public decimal OnBalanceGap { get; init; }
    public decimal TotalGap { get; init; }
    public decimal ShortTenorGap { get; init; }
    public decimal DerivativesNet { get; init; }
}

public sealed class AlmCashflowNodeDto
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Kind { get; init; } = "line";
    public int Level { get; init; }
    public int SortId { get; init; }
    public decimal Total { get; init; }
    public IReadOnlyList<decimal> Amounts { get; init; } = [];
    public IReadOnlyList<AlmCashflowNodeDto> Children { get; init; } = [];
}

public sealed class AlmCashflowChartDto
{
    public IReadOnlyList<decimal> Assets { get; init; } = [];
    public IReadOnlyList<decimal> Liabilities { get; init; } = [];
    public IReadOnlyList<decimal> DerivativesNet { get; init; } = [];
    public IReadOnlyList<decimal> Gap { get; init; } = [];
    public IReadOnlyList<decimal> CumulativeGap { get; init; } = [];
}

public sealed class AlmDurationReportDto
{
    public string ReportingDate { get; init; } = string.Empty;
    public IReadOnlyList<AlmDurationNodeDto> Tree { get; init; } = [];
}

public sealed class AlmDurationNodeDto
{
    public string Id { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Kind { get; init; } = "line";
    public int Level { get; init; }
    public decimal ToplamBakiye { get; init; }
    public decimal? ToplamPv01Try { get; init; }
    public decimal? AgirlikliModDuration { get; init; }
    public decimal? AgirlikliMacDuration { get; init; }
    public decimal? AgirlikliYtm { get; init; }
    public decimal? AgirlikliConvexity { get; init; }
    public decimal? AgirlikliKalanOmur { get; init; }
    public decimal? AgirlikliGostergeGetiri { get; init; }
    public IReadOnlyList<AlmDurationNodeDto> Children { get; init; } = [];
}

public sealed class CashflowLeafRow
{
    public string?[] Headers { get; init; } = new string?[7];
    public string AlmCoaCode { get; init; } = string.Empty;
    public string CcyCode { get; init; } = string.Empty;
    public int SortId { get; init; }
    public decimal[] Amounts { get; init; } = new decimal[31];
}

public sealed class CoreDepositRateRow
{
    public string AlmCoaCode { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public string ReportBucket { get; init; } = string.Empty;
    public decimal Rate { get; init; }
}

public sealed class DurationLeafRow
{
    public string? Header1 { get; init; }
    public string? Header2 { get; init; }
    public int SortId { get; init; }
    public decimal ToplamBakiye { get; init; }
    public decimal ToplamPv01Try { get; init; }
    public decimal WeightedMod { get; init; }
    public decimal WeightedMac { get; init; }
    public decimal WeightedYtm { get; init; }
    public decimal WeightedConvexity { get; init; }
    public decimal WeightedLife { get; init; }
    public decimal WeightedComparable { get; init; }
}
