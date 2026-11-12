using System.Text.RegularExpressions;
using BatchStudio.Core.Invoices;

namespace BatchStudio.Core.Analysis;

public enum HealthKind
{
    SharedAccount,   // one account on two or more suppliers
    AccountTwice,    // the same account in two banking paragraphs of one supplier
    NoAccount,       // a banking paragraph without account number / IBAN
    NoSwift,         // a banking paragraph without SWIFT / BIC (or IFSC, routing, sort code, BSB)
    BankNameJunk,    // OCR crumbs before the bank's name ("Iaxghty Hang Seng Bank")
    NoBank,          // a supplier without any banking paragraph
    SameName,        // the same name twice (case, punctuation, CO./LTD. aside)
    CloseName,       // very close names
    NoCategory,      // a client / supplier without category
}

/// <summary>One finding: what, which records (IDs), and the detail to show (account no., name ...).</summary>
public sealed record HealthIssue(HealthKind Kind, IReadOnlyList<string> Ids, IReadOnlyList<string> Names, string Detail, int Bank = 0);

/// <summary>
/// The whole Suppliers / Clients list checked at once: the mistakes Analysis tripped on one document at a time
/// (an account saved on the wrong supplier, the same account twice, paragraphs without an account ...).
/// </summary>
public static class HealthCheck
{
    private static readonly Regex SwiftLike = new(@"(?im)^\s*(?:[\w /()]*\s)?(?:swift|bic|ifsc|routing|aba|sort\s*code|bsb|bank\s*identifier|cnaps)\b[^:\n]*[:：]\s*\S");
    private static readonly Regex BankNameLine = new(@"(?im)^\s*(?:beneficiary\s+)?bank(?:\s*name)?\s*[:：]\s*([A-Za-z]{3,12})\s+([A-Z][^\n]*)$");
    private static readonly HashSet<string> RealLeads = new(StringComparer.OrdinalIgnoreCase)
        { "deutsche", "schweizer", "schwab", "landesbank", "kreissparkasse", "nordea", "schroders", "standard", "agricultural", "industrial" };

    private static int Col(IReadOnlyList<string> headers, params string[] names)
    {
        foreach (var n in names)
            for (int i = 0; i < headers.Count; i++)
                if (headers[i].Contains(n, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string At(IReadOnlyList<string> row, int col) => col >= 0 && col < row.Count ? (row[col] ?? "").Trim() : "";

    /// <summary>"Iaxghty" in front of a bank name: an OCR crumb, not a word.</summary>
    public static bool IsJunkWord(string word)
    {
        if (word.Length < 3 || RealLeads.Contains(word) || word.All(char.IsUpper)) return false;
        int vowels = word.Count(c => "aeiouAEIOU".Contains(c));
        return Regex.IsMatch(word, "(?i)[bcdfghjklmnpqrstvwxz]{4,}") || vowels == 0
               || (char.IsUpper(word[0]) && word[1..].All(char.IsLower) && (double)vowels / word.Length < 0.25);
    }

    public static List<HealthIssue> Suppliers(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var issues = new List<HealthIssue>();
        int nameCol = Col(headers, "Supplier Name", "Name");
        if (nameCol < 0) nameCol = 1;
        int catCol = Col(headers, "Category");
        var owners = new Dictionary<string, List<(string Id, string Name, int Bank)>>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            var id = At(row, 0);
            var name = At(row, nameCol);
            if (id.Length == 0 && name.Length == 0) continue;
            var banks = SupplierBanks.Accounts(headers, row).Where(b => b.Text.Trim().Length > 0).ToList();
            if (banks.Count == 0) issues.Add(new(HealthKind.NoBank, [id], [name], ""));
            if (catCol >= 0 && At(row, catCol).Length == 0) issues.Add(new(HealthKind.NoCategory, [id], [name], ""));
            var seenHere = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in banks)
            {
                var ids = AnalysisMatcher.AccountIdsIn(b.Text);
                if (ids.Count == 0) issues.Add(new(HealthKind.NoAccount, [id], [name], "", b.Number));
                if (!SwiftLike.IsMatch(b.Text)) issues.Add(new(HealthKind.NoSwift, [id], [name], "", b.Number));
                var bn = BankNameLine.Match(b.Text);
                if (bn.Success && Regex.IsMatch(bn.Groups[2].Value, @"(?i)\bbank\b") && IsJunkWord(bn.Groups[1].Value))
                    issues.Add(new(HealthKind.BankNameJunk, [id], [name], bn.Groups[1].Value + " " + bn.Groups[2].Value.Trim(), b.Number));
                foreach (var acc in ids)
                {
                    if (seenHere.TryGetValue(acc, out var first))
                        issues.Add(new(HealthKind.AccountTwice, [id], [name], $"{acc} ({first} + {b.Number})", b.Number));
                    else seenHere[acc] = b.Number;
                    if (!owners.TryGetValue(acc, out var list)) owners[acc] = list = [];
                    if (!list.Any(o => o.Id == id)) list.Add((id, name, b.Number));
                }
            }
        }
        foreach (var (acc, list) in owners)
            if (list.Count > 1)
                issues.Add(new(HealthKind.SharedAccount, list.Select(o => o.Id).ToList(), list.Select(o => o.Name).ToList(), acc));
        issues.AddRange(Names(rows, nameCol));
        return Order(issues);
    }

    public static List<HealthIssue> Clients(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        var issues = new List<HealthIssue>();
        int nameCol = Col(headers, "Client Name", "Name");
        if (nameCol < 0) nameCol = 1;
        int catCol = Col(headers, "Category");
        foreach (var row in rows)
        {
            var id = At(row, 0);
            var name = At(row, nameCol);
            if (id.Length == 0 && name.Length == 0) continue;
            if (catCol >= 0 && At(row, catCol).Length == 0) issues.Add(new(HealthKind.NoCategory, [id], [name], ""));
        }
        issues.AddRange(Names(rows, nameCol));
        return Order(issues);
    }

    /// <summary>Same names (after normalising) and very close ones ("TRADING CO" / "TRADE CO").</summary>
    private static IEnumerable<HealthIssue> Names(IReadOnlyList<IReadOnlyList<string>> rows, int nameCol)
    {
        var list = rows.Select(r => (Id: At(r, 0), Name: At(r, nameCol), Key: AnalysisMatcher.NormalizeName(At(r, nameCol))))
                       .Where(x => x.Key.Length > 0).ToList();
        foreach (var g in list.GroupBy(x => x.Key).Where(g => g.Count() > 1))
            yield return new(HealthKind.SameName, g.Select(x => x.Id).ToList(), g.Select(x => x.Name).ToList(), g.First().Name);
        for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
            {
                if (list[i].Key == list[j].Key) continue;
                // quick reject: lengths too different to be ≥ 0.90 alike
                int la = list[i].Key.Length, lb = list[j].Key.Length;
                if (Math.Abs(la - lb) > Math.Max(la, lb) * 0.25) continue;
                if (AnalysisMatcher.Similarity(list[i].Key, list[j].Key) >= AnalysisMatcher.SameNameScore)
                    yield return new(HealthKind.CloseName, [list[i].Id, list[j].Id], [list[i].Name, list[j].Name], "");
            }
    }

    private static List<HealthIssue> Order(List<HealthIssue> issues) =>
        issues.OrderBy(i => (int)i.Kind).ThenBy(i => i.Ids.FirstOrDefault(), StringComparer.OrdinalIgnoreCase).ToList();
}
