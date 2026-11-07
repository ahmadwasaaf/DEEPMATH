namespace BatchStudio.Core.Invoices;

/// <summary>Period filter of INVOICE HISTORY - same choices as the E5 dropdown of the V28 sheet.</summary>
public static class HistoryPeriods
{
    public const string AllTime = "All Time";
    public const string Today = "Today";
    public const string ThisWeek = "This Week";
    public const string ThisMonth = "This Month";
    public const string LastMonth = "Last Month";
    public const string ThisYear = "This Year";
    public const string CustomRange = "Custom Range";

    public static IReadOnlyList<string> All { get; } = [AllTime, Today, ThisWeek, ThisMonth, LastMonth, ThisYear, CustomRange];

    /// <summary>Report file name for a period: "Today's Report", "This Month Report", …</summary>
    public static string ReportName(string period) =>
        string.Equals(period, Today, StringComparison.OrdinalIgnoreCase) ? "Today's Report" : $"{period} Report";

    public static DayOfWeek ParseWeekStart(string? text)
    {
        var t = (text ?? "").Trim();
        foreach (DayOfWeek d in Enum.GetValues<DayOfWeek>())
            if (t.Length >= 3 && d.ToString().StartsWith(t[..3], StringComparison.OrdinalIgnoreCase)) return d;
        return DayOfWeek.Saturday;
    }

    /// <summary>Inclusive date window of a period, or null for "All Time".</summary>
    public static (DateTime From, DateTime To)? Window(string period, DateTime today, DayOfWeek weekStart,
        DateTime? customFrom = null, DateTime? customTo = null)
    {
        today = today.Date;
        switch (period)
        {
            case Today: return (today, today);
            case ThisWeek:
            {
                int back = ((int)today.DayOfWeek - (int)weekStart + 7) % 7;
                var s = today.AddDays(-back);
                return (s, s.AddDays(6));
            }
            case ThisMonth:
            {
                var s = new DateTime(today.Year, today.Month, 1);
                return (s, s.AddMonths(1).AddDays(-1));
            }
            case LastMonth:
            {
                var s = new DateTime(today.Year, today.Month, 1).AddMonths(-1);
                return (s, s.AddMonths(1).AddDays(-1));
            }
            case ThisYear: return (new DateTime(today.Year, 1, 1), new DateTime(today.Year, 12, 31));
            case CustomRange: return (customFrom?.Date ?? DateTime.MinValue.Date, customTo?.Date ?? DateTime.MaxValue.Date);
            default: return null;
        }
    }
}

public sealed class HistoryQuery
{
    public string Text { get; set; } = "";
    /// <summary>ALL, SUPPLIERS or CLIENTS (the three buttons of the V28 sheet).</summary>
    public string Tab { get; set; } = "ALL";
    public string Period { get; set; } = HistoryPeriods.AllTime;
    public DayOfWeek WeekStart { get; set; } = DayOfWeek.Saturday;
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public DateTime Today { get; set; } = DateTime.Today;

    public bool Matches(HistoryRecord r)
    {
        if (Tab is "SUPPLIERS" or "CLIENTS" && !string.Equals(r.PartyType, Tab, StringComparison.OrdinalIgnoreCase)) return false;
        var window = HistoryPeriods.Window(Period, Today, WeekStart, From, To);
        if (window is { } w)
        {
            if (r.Date is not { } d || d.Date < w.From || d.Date > w.To) return false;
        }
        var q = Normalize(Text);
        if (q.Length == 0) return true;
        var hay = Normalize(string.Join(" ", r.No, r.Name, r.InvoiceNo, r.PartyType, r.Template,
            r.Amount?.ToString("N2", System.Globalization.CultureInfo.InvariantCulture) ?? "",
            r.Amount?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            r.Date?.ToString("dd/MM/yyyy") ?? "", r.Date?.ToString("yyyy-MM-dd") ?? ""));
        return hay.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Arabic-insensitive search: أ/إ/آ = ا, ة = ه, ى = ي, no tashkeel or tatweel (as HistorySearchV41).</summary>
    public static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s.Trim().ToLowerInvariant())
        {
            if (ch is >= 'ً' and <= 'ْ' || ch == 'ـ') continue;
            sb.Append(ch switch
            {
                'أ' or 'إ' or 'آ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                _ => ch,
            });
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ");
    }
}
