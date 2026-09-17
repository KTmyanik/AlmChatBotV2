using AlmChatBot.Api.Models;

namespace AlmChatBot.Api.Services;

public static class AlmCashflowTreeBuilder
{
    public const string Assets = "VARLIKLAR";
    public const string Liabilities = "YÜKÜMLÜLÜKLER";
    public const string OffBalance = "BİLANÇO DIŞI İŞLEMLER";
    public const string DerivativesNet = "TÜREV FİNANSAL ARAÇLAR (NET)";
    public const string OnBalanceGap = "BİL. İÇİ NET AÇIK/FAZLA";
    public const string TotalGap = "TOPLAM NET AÇIK/FAZLA";

    private static readonly string[] Header1Order =
    [
        Assets, Liabilities, OffBalance, DerivativesNet, OnBalanceGap, TotalGap
    ];

    private static readonly string[] DerivativeHeaders =
    [
        "TÜREV FİNANSAL VARLIKLAR",
        "TÜREV FİNANSAL YÜKÜMLÜLÜKLER",
        "TÜREV FİNANSAL ARAÇLAR"
    ];

    public static void ApplyCoreDeposits(IReadOnlyList<CashflowLeafRow> leaves, IReadOnlyList<CoreDepositRateRow> rates)
    {
        if (rates.Count == 0)
        {
            return;
        }

        var lookup = rates.ToLookup(r => RateKey(r.AlmCoaCode, r.Currency), StringComparer.OrdinalIgnoreCase);

        var buckets = AlmSqlPlanner.TenorColumns;
        foreach (var leaf in leaves)
        {
            var set = lookup[RateKey(leaf.AlmCoaCode, leaf.CcyCode)].ToList();
            if (set.Count == 0)
            {
                continue;
            }

            var total = leaf.Amounts.Sum();
            var byBucket = set
                .GroupBy(x => x.ReportBucket, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Rate), StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < buckets.Length; i++)
            {
                leaf.Amounts[i] = total * (byBucket.TryGetValue(buckets[i], out var rate) ? rate : 0m);
            }
        }
    }

    public static AlmCashflowReportDto Build(
        IReadOnlyList<CashflowLeafRow> leaves,
        AlmCashflowRequest request,
        string reportingDate)
    {
        var depth = Math.Clamp(request.Depth <= 0 ? 2 : request.Depth, 1, 7);
        var tree = BuildHeaderTree(leaves, depth);
        tree = InsertComputed(tree, leaves);

        var assets = Find(tree, Assets);
        var liabilities = Find(tree, Liabilities);
        var obs = Find(tree, OffBalance);
        var deriv = Find(tree, DerivativesNet);
        var onGap = Find(tree, OnBalanceGap);
        var totalGap = Find(tree, TotalGap);

        var shortCount = AlmSqlPlanner.ShortTenorColumns.Length;
        decimal ShortSum(AlmCashflowNodeDto? node) =>
            node is null ? 0 : node.Amounts.Take(shortCount).Sum();

        var gapAmounts = Align(totalGap?.Amounts);
        var cumulative = new decimal[gapAmounts.Count];
        decimal running = 0;
        for (var i = 0; i < gapAmounts.Count; i++)
        {
            running += gapAmounts[i];
            cumulative[i] = running;
        }

        return new AlmCashflowReportDto
        {
            ReportingDate = reportingDate,
            Approach = request.Approach,
            BalanceType = request.BalanceType,
            Ccy = string.IsNullOrWhiteSpace(request.Ccy) ? null : request.Ccy,
            Pools = request.Pools ?? [],
            CoreDeposits = request.CoreDeposits,
            Depth = depth,
            Tenors = AlmTenorCatalog.Infos,
            Kpis = new AlmCashflowKpisDto
            {
                Assets = assets?.Total ?? 0,
                Liabilities = liabilities?.Total ?? 0,
                OffBalance = obs?.Total ?? 0,
                OnBalanceGap = onGap?.Total ?? 0,
                TotalGap = totalGap?.Total ?? 0,
                ShortTenorGap = ShortSum(assets) + ShortSum(liabilities) + ShortSum(obs),
                DerivativesNet = deriv?.Total ?? 0
            },
            Tree = tree,
            Chart = new AlmCashflowChartDto
            {
                Assets = Align(assets?.Amounts),
                Liabilities = Align(liabilities?.Amounts),
                DerivativesNet = Align(deriv?.Amounts),
                Gap = gapAmounts,
                CumulativeGap = cumulative
            }
        };
    }

    public static IReadOnlyList<AlmDurationNodeDto> BuildDuration(IReadOnlyList<DurationLeafRow> leaves)
    {
        var groups = leaves
            .Where(l => !string.IsNullOrWhiteSpace(l.Header1))
            .GroupBy(l => l.Header1!.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => HeaderOrder(g.Key, 0))
            .ThenBy(g => g.Min(x => x.SortId));

        var tree = new List<AlmDurationNodeDto>();
        var i = 0;
        foreach (var group in groups)
        {
            var children = group
                .Where(x => !string.IsNullOrWhiteSpace(x.Header2))
                .GroupBy(x => x.Header2!.Trim(), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Min(x => x.SortId))
                .Select((g, j) => ToDurationNode($"d{i}.{j}", g.Key, 1, "line", g.ToList()))
                .ToList();

            var parentLeaves = children.Count == 0 ? group.ToList() : null;
            var node = parentLeaves is null
                ? RollDuration($"d{i}", group.Key, 0, "section", children)
                : ToDurationNode($"d{i}", group.Key, 0, "section", parentLeaves);
            tree.Add(node);
            i++;
        }

        return tree;
    }

    private static List<AlmCashflowNodeDto> BuildHeaderTree(IReadOnlyList<CashflowLeafRow> leaves, int depth)
    {
        return GroupLevel(leaves, 0, depth, "n");
    }

    private static List<AlmCashflowNodeDto> GroupLevel(
        IReadOnlyList<CashflowLeafRow> leaves,
        int level,
        int depth,
        string prefix)
    {
        var groups = leaves
            .Select(l => (Leaf: l, Label: HeaderAt(l, level)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Label))
            .GroupBy(x => x.Label!, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => HeaderOrder(g.Key, level))
            .ThenBy(g => g.Min(x => x.Leaf.SortId));

        var nodes = new List<AlmCashflowNodeDto>();
        var index = 0;
        foreach (var group in groups)
        {
            var id = $"{prefix}.{index}";
            var rows = group.Select(x => x.Leaf).ToList();
            List<AlmCashflowNodeDto> children = [];
            if (level + 1 < depth)
            {
                children = GroupLevel(rows, level + 1, depth, id);
            }

            nodes.Add(new AlmCashflowNodeDto
            {
                Id = id,
                Label = group.Key,
                Kind = level == 0 ? "section" : "line",
                Level = level,
                SortId = rows.Min(x => x.SortId),
                Amounts = SumAmounts(rows),
                Total = SumAmounts(rows).Sum(),
                Children = children
            });
            index++;
        }

        return nodes;
    }

    private static List<AlmCashflowNodeDto> InsertComputed(
        List<AlmCashflowNodeDto> tree,
        IReadOnlyList<CashflowLeafRow> leaves)
    {
        var assets = Find(tree, Assets);
        var liabilities = Find(tree, Liabilities);
        var obs = Find(tree, OffBalance);
        var deriv = DerivativeAmounts(leaves);

        var onGapAmounts = Add(assets?.Amounts, liabilities?.Amounts);
        var totalGapAmounts = Add(onGapAmounts, obs?.Amounts);

        var result = tree
            .Where(n => !IsComputedHeader(n.Label))
            .OrderBy(n => HeaderOrder(n.Label, 0))
            .ThenBy(n => n.SortId)
            .ToList();

        result.Add(Node("c.der", DerivativesNet, "computed", deriv));
        result.Add(Node("c.on", OnBalanceGap, "computed", onGapAmounts));
        result.Add(Node("c.tot", TotalGap, "computed", totalGapAmounts));
        return result;
    }

    private static AlmCashflowNodeDto Node(string id, string label, string kind, IReadOnlyList<decimal> amounts) =>
        new()
        {
            Id = id,
            Label = label,
            Kind = kind,
            Level = 0,
            Amounts = amounts,
            Total = amounts.Sum()
        };

    private static AlmCashflowNodeDto? Find(IEnumerable<AlmCashflowNodeDto> nodes, string label) =>
        nodes.FirstOrDefault(n => FoldEq(n.Label, label));

    private static decimal[] DerivativeAmounts(IEnumerable<CashflowLeafRow> leaves)
    {
        var acc = new decimal[AlmTenorCatalog.Count];
        foreach (var leaf in leaves)
        {
            var header2 = leaf.Headers.Length > 1 ? leaf.Headers[1] : null;
            if (header2 is not null && DerivativeHeaders.Any(label => FoldEq(header2, label)))
            {
                AddInPlace(acc, leaf.Amounts);
            }
        }

        return acc;
    }

    private static decimal[] SumAmounts(IEnumerable<CashflowLeafRow> rows)
    {
        var acc = new decimal[AlmTenorCatalog.Count];
        foreach (var row in rows)
        {
            AddInPlace(acc, row.Amounts);
        }

        return acc;
    }

    private static IReadOnlyList<decimal> Add(IReadOnlyList<decimal>? left, IReadOnlyList<decimal>? right)
    {
        var acc = new decimal[AlmTenorCatalog.Count];
        AddInPlace(acc, left);
        AddInPlace(acc, right);
        return acc;
    }

    private static void AddInPlace(decimal[] acc, IReadOnlyList<decimal>? values)
    {
        if (values is null)
        {
            return;
        }

        var n = Math.Min(acc.Length, values.Count);
        for (var i = 0; i < n; i++)
        {
            acc[i] += values[i];
        }
    }

    private static IReadOnlyList<decimal> Align(IReadOnlyList<decimal>? values)
    {
        if (values is null)
        {
            return new decimal[AlmTenorCatalog.Count];
        }

        if (values.Count == AlmTenorCatalog.Count)
        {
            return values;
        }

        var acc = new decimal[AlmTenorCatalog.Count];
        AddInPlace(acc, values);
        return acc;
    }

    private static string? HeaderAt(CashflowLeafRow leaf, int level) =>
        level >= 0 && level < leaf.Headers.Length ? NullIfEmpty(leaf.Headers[level]) : null;

    private static string? NullIfEmpty(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }

    private static int HeaderOrder(string label, int level)
    {
        if (level != 0)
        {
            return 50;
        }

        for (var i = 0; i < Header1Order.Length; i++)
        {
            if (FoldEq(label, Header1Order[i]))
            {
                return i;
            }
        }

        return 50;
    }

    private static bool IsComputedHeader(string label) =>
        FoldEq(label, DerivativesNet) || FoldEq(label, OnBalanceGap) || FoldEq(label, TotalGap);

    public static bool FoldEq(string left, string right) =>
        string.Equals(FoldKey(left), FoldKey(right), StringComparison.OrdinalIgnoreCase);

    private static string FoldKey(string value) =>
        AlmSqlPlanner.ToAsciiHeader(value).ToUpperInvariant();

    private static string RateKey(string coa, string ccy) => $"{coa}\u001f{ccy}";

    private static AlmDurationNodeDto ToDurationNode(string id, string label, int level, string kind, IReadOnlyList<DurationLeafRow> rows)
    {
        var bakiye = rows.Sum(r => r.ToplamBakiye);
        return new AlmDurationNodeDto
        {
            Id = id,
            Label = label,
            Kind = kind,
            Level = level,
            ToplamBakiye = bakiye,
            ToplamPv01Try = rows.Sum(r => r.ToplamPv01Try),
            AgirlikliModDuration = Weighted(rows, r => r.WeightedMod, bakiye),
            AgirlikliMacDuration = Weighted(rows, r => r.WeightedMac, bakiye),
            AgirlikliYtm = Weighted(rows, r => r.WeightedYtm, bakiye),
            AgirlikliConvexity = Weighted(rows, r => r.WeightedConvexity, bakiye),
            AgirlikliKalanOmur = Weighted(rows, r => r.WeightedLife, bakiye),
            AgirlikliGostergeGetiri = Weighted(rows, r => r.WeightedComparable, bakiye)
        };
    }

    private static AlmDurationNodeDto RollDuration(
        string id,
        string label,
        int level,
        string kind,
        IReadOnlyList<AlmDurationNodeDto> children)
    {
        var bakiye = children.Sum(c => c.ToplamBakiye);
        return new AlmDurationNodeDto
        {
            Id = id,
            Label = label,
            Kind = kind,
            Level = level,
            ToplamBakiye = bakiye,
            ToplamPv01Try = children.Sum(c => c.ToplamPv01Try ?? 0),
            AgirlikliModDuration = WeightedNodes(children, c => c.AgirlikliModDuration, bakiye),
            AgirlikliMacDuration = WeightedNodes(children, c => c.AgirlikliMacDuration, bakiye),
            AgirlikliYtm = WeightedNodes(children, c => c.AgirlikliYtm, bakiye),
            AgirlikliConvexity = WeightedNodes(children, c => c.AgirlikliConvexity, bakiye),
            AgirlikliKalanOmur = WeightedNodes(children, c => c.AgirlikliKalanOmur, bakiye),
            AgirlikliGostergeGetiri = WeightedNodes(children, c => c.AgirlikliGostergeGetiri, bakiye),
            Children = children
        };
    }

    private static decimal? Weighted(IReadOnlyList<DurationLeafRow> rows, Func<DurationLeafRow, decimal> metric, decimal bakiye)
    {
        if (bakiye == 0)
        {
            return null;
        }

        return rows.Sum(r => metric(r) * r.ToplamBakiye) / bakiye;
    }

    private static decimal? WeightedNodes(IReadOnlyList<AlmDurationNodeDto> nodes, Func<AlmDurationNodeDto, decimal?> metric, decimal bakiye)
    {
        if (bakiye == 0)
        {
            return null;
        }

        return nodes.Sum(n => (metric(n) ?? 0) * n.ToplamBakiye) / bakiye;
    }
}
