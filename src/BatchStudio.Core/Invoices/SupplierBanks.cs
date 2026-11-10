using System.Globalization;

namespace BatchStudio.Core.Invoices;

/// <summary>One bank account of a supplier: 1 = "Banking Details (full paragraph)", 2.. = the numbered columns.</summary>
public sealed record BankAccount(int Number, string Text);

/// <summary>
/// Several bank accounts per supplier, stored on the supplier's own row of the Suppliers sheet:
/// "Banking Details (full paragraph)" is account 1, "Banking Details (full paragraph) 2", "... 3" are added as needed,
/// and "DEFAULT BANK" holds the number of the account printed on invoices (empty = 1). Columns are found by header.
/// </summary>
public static class SupplierBanks
{
    public const string BaseHeader = "Banking Details (full paragraph)";
    public const string DefaultHeader = "DEFAULT BANK";
    public const int MaxAccounts = 20;

    public static string HeaderFor(int number) => number <= 1 ? BaseHeader : $"{BaseHeader} {number}";

    public static bool IsDefaultHeader(string header) => string.Equals(header.Trim(), DefaultHeader, StringComparison.OrdinalIgnoreCase);

    /// <summary>Account number of a numbered bank header ("... 2" → 2), 0 for any other header (account 1 included).</summary>
    public static int ExtraNumber(string header)
    {
        var h = header.Trim();
        if (!h.StartsWith(BaseHeader + " ", StringComparison.OrdinalIgnoreCase)) return 0;
        return int.TryParse(h[(BaseHeader.Length + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 2 ? n : 0;
    }

    /// <summary>Column of account 1: the exact header, else the first header that mentions "Bank" (not a numbered one).</summary>
    public static int BaseColumn(IReadOnlyList<string> headers)
    {
        for (int i = 0; i < headers.Count; i++)
            if (string.Equals(headers[i].Trim(), BaseHeader, StringComparison.OrdinalIgnoreCase)) return i;
        for (int i = 0; i < headers.Count; i++)
            if (headers[i].Contains("Bank", StringComparison.OrdinalIgnoreCase) && ExtraNumber(headers[i]) == 0 && !IsDefaultHeader(headers[i])) return i;
        return -1;
    }

    /// <summary>True for every header that belongs to the bank accounts (account 1, numbered accounts, DEFAULT BANK).</summary>
    public static bool IsBankHeader(IReadOnlyList<string> headers, int index) =>
        index == BaseColumn(headers) || ExtraNumber(headers[index]) > 0 || IsDefaultHeader(headers[index]);

    /// <summary>(number, column) of every bank column, account 1 first.</summary>
    public static List<(int Number, int Column)> Columns(IReadOnlyList<string> headers)
    {
        var list = new List<(int, int)>();
        int b = BaseColumn(headers);
        if (b >= 0) list.Add((1, b));
        for (int i = 0; i < headers.Count; i++)
            if (ExtraNumber(headers[i]) is var n and > 0 && list.All(x => x.Item1 != n)) list.Add((n, i));
        return list.OrderBy(x => x.Item1).ToList();
    }

    public static int DefaultColumn(IReadOnlyList<string> headers)
    {
        for (int i = 0; i < headers.Count; i++) if (IsDefaultHeader(headers[i])) return i;
        return -1;
    }

    /// <summary>The supplier's accounts that have text (account 1 always listed, even when empty).</summary>
    public static List<BankAccount> Accounts(IReadOnlyList<string> headers, IReadOnlyList<string> row)
    {
        var list = new List<BankAccount>();
        foreach (var (n, c) in Columns(headers))
        {
            var text = c < row.Count ? row[c] ?? "" : "";
            if (n == 1 || text.Trim().Length > 0) list.Add(new BankAccount(n, text));
        }
        if (list.Count == 0 || list[0].Number != 1) list.Insert(0, new BankAccount(1, ""));
        return list;
    }

    /// <summary>Number in a DEFAULT BANK cell (1 when empty or not a number).</summary>
    public static int ParseDefault(string? value) =>
        double.TryParse((value ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d >= 2 && d <= 999 ? (int)d : 1;

    /// <summary>The account printed on invoices: DEFAULT BANK when that account has text, else account 1.</summary>
    public static int DefaultNumber(IReadOnlyList<string> headers, IReadOnlyList<string> row, IReadOnlyList<BankAccount> accounts)
    {
        int c = DefaultColumn(headers);
        int n = c >= 0 && c < row.Count ? ParseDefault(row[c]) : 1;
        return accounts.Any(a => a.Number == n && a.Text.Trim().Length > 0) ? n : 1;
    }

    /// <summary>Same text as far as a bank paragraph goes (line breaks, repeated spaces and case ignored).</summary>
    public static string Normalize(string? s)
    {
        s = (s ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace(' ', ' ').Trim();
        while (s.Contains("  ")) s = s.Replace("  ", " ");
        while (s.Contains("\n\n")) s = s.Replace("\n\n", "\n");
        return s.ToUpperInvariant();
    }
}
