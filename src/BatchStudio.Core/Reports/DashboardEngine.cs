using BatchStudio.Core.Data;
using BatchStudio.Core.Invoices;

namespace BatchStudio.Core.Reports;

public sealed record RankRow(int Rank, string Name, string Type, decimal Amount, int Count, string Category);

public sealed class DashboardResult
{
    public decimal Total { get; init; }
    public decimal SuppliersTotal { get; init; }
    public decimal ClientsTotal { get; init; }
    public int SupplierCount { get; init; }
    public int ClientCount { get; init; }
    public int Invoices { get; init; }
    /// <summary>All companies of the period, in ranking order.</summary>
    public required List<RankRow> Ranking { get; init; }
    public IEnumerable<RankRow> Top20 => Ranking.Take(20);
}

public sealed class DashboardFilter
{
    public string Period { get; set; } = HistoryPeriods.AllTime;
    /// <summary>ALL, SUPPLIERS or CLIENTS (Dashboard G4).</summary>
    public string Mode { get; set; } = "ALL";
    /// <summary>TOP or LESS (Dashboard G5).</summary>
    public string Ranking { get; set; } = "TOP";
    public DayOfWeek WeekStart { get; set; } = DayOfWeek.Saturday;
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public DateTime Today { get; set; } = DateTime.Today;

    public bool InPeriod(DateTime? date)
    {
        var w = HistoryPeriods.Window(Period, Today, WeekStart, From, To);
        if (w == null) return true;
        return date is { } d && d.Date >= w.Value.From && d.Date <= w.Value.To;
    }
}

/// <summary>Port of DashboardV8.RefreshDashboard: totals and company ranking for a period.</summary>
public static class DashboardEngine
{
    /// <summary>"NAME (2)" -> "NAME" (duplicate company rows in V28 get a numeric suffix).</summary>
    public static string BaseName(string raw)
    {
        var s = (raw ?? "").Trim();
        int p = s.LastIndexOf(" (", StringComparison.Ordinal);
        if (p > 0 && s.EndsWith(')') && int.TryParse(s[(p + 2)..^1], out var n) && n >= 2) s = s[..p];
        return s;
    }

    public static bool SameCompany(string a, string b) =>
        string.Equals(BaseName(a), BaseName(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Category per "TYPE|name" from Suppliers (Category column) and Clients (Client Category).</summary>
    public static Dictionary<string, string> Categories(TableData? suppliers, TableData? clients)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (suppliers != null)
            foreach (var p in InvoiceService.Suppliers(suppliers)) map.TryAdd("SUPPLIERS|" + p.Name.Trim(), p.CategoryText ?? "");
        if (clients != null)
            foreach (var p in InvoiceService.Clients(clients)) map.TryAdd("CLIENTS|" + p.Name.Trim(), p.CategoryText ?? "");
        return map;
    }

    public static DashboardResult Compute(IEnumerable<HistoryRecord> history, IReadOnlyDictionary<string, string> categories, DashboardFilter f)
    {
        var mode = f.Mode is "SUPPLIERS" or "CLIENTS" ? f.Mode : "ALL";
        var groups = new Dictionary<string, (string Name, string Type, decimal Amount, int Count)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        decimal all = 0, sup = 0, cli = 0;
        int invoices = 0;
        foreach (var r in history)
        {
            if (string.IsNullOrWhiteSpace(r.RecordId)) continue;
            var tp = r.PartyType.Trim().ToUpperInvariant();
            var nm = r.Name.Trim();
            if (tp is not ("SUPPLIERS" or "CLIENTS") || nm.Length == 0) continue;
            if (!f.InPeriod(r.Date)) continue;
            if (mode != "ALL" && mode != tp) continue;
            var amt = r.Amount ?? 0;
            var key = tp + "|" + nm;
            if (!groups.TryGetValue(key, out var g)) { g = (nm, tp, 0, 0); order.Add(key); }
            groups[key] = (g.Name, g.Type, g.Amount + amt, g.Count + 1);
            invoices++;
            all += amt;
            if (tp == "SUPPLIERS") sup += amt; else cli += amt;
        }
        bool less = string.Equals(f.Ranking, "LESS", StringComparison.OrdinalIgnoreCase);
        var ranked = order.Select(k => groups[k]);
        ranked = less ? ranked.OrderBy(g => g.Amount).ThenBy(g => g.Count) : ranked.OrderByDescending(g => g.Amount).ThenByDescending(g => g.Count);
        var list = ranked.Select((g, i) => new RankRow(i + 1, g.Name, g.Type, g.Amount, g.Count,
            categories.TryGetValue(g.Type + "|" + g.Name, out var c) ? c : "")).ToList();
        return new DashboardResult
        {
            Total = all, SuppliersTotal = sup, ClientsTotal = cli, Invoices = invoices,
            SupplierCount = groups.Values.Count(g => g.Type == "SUPPLIERS"),
            ClientCount = groups.Values.Count(g => g.Type == "CLIENTS"),
            Ranking = list,
        };
    }
}
