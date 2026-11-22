using BatchStudio.Core.Invoices;

namespace BatchStudio.Core.Analysis;

/// <summary>
/// What "Approve all" writes to the Suppliers sheet: the row of a new supplier, and a new "Banking Details N"
/// column value for a known supplier whose document has another account.
/// </summary>
public static class AnalysisRecords
{
    /// <summary>Category given to new suppliers when the sheet has it (English part of "English | Arabic").</summary>
    public const string DefaultCategory = "General Trading - Import & Export";

    /// <summary>The category text of the Categories sheet for new suppliers (exact English part, else close to it).</summary>
    public static string? CategoryText(IReadOnlyList<CategoryInfo> categories)
    {
        static string En(string t) => t.Split('|')[0].Trim();
        static string Key(string t) => new string(t.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        var want = Key(DefaultCategory);
        return (categories.FirstOrDefault(c => Key(En(c.Text)) == want)
                ?? categories.FirstOrDefault(c => Key(En(c.Text)).StartsWith("GENERALTRADING", StringComparison.Ordinal))
                ?? categories.FirstOrDefault(c => c.Text.Contains("General", StringComparison.OrdinalIgnoreCase)))?.Text;
    }

    /// <summary>
    /// The category closest to what the document says the supplier does ("Industrial fasteners and maintenance components"
    /// → a category with "Fasteners" / "Industrial" in its English part); none close: the default (General Trading).
    /// </summary>
    public static string? CategoryFor(string? activity, IReadOnlyList<CategoryInfo> categories)
    {
        var fallback = CategoryText(categories);
        if (string.IsNullOrWhiteSpace(activity) || categories.Count == 0) return fallback;
        static string Stem(string w)
        {
            w = w.ToLowerInvariant();
            if (w.EndsWith("ies") && w.Length > 5) return w[..^3] + "y";
            if (w.EndsWith("es") && w.Length > 5 && !w.EndsWith("ses")) return w[..^2];
            if (w.EndsWith('s') && w.Length > 4) return w[..^1];
            return w;
        }
        string[] stop = ["and", "the", "for", "with", "other", "general", "trading", "import", "export", "products", "product",
                         "goods", "company", "supply", "supplies", "services", "service", "parts", "part", "item", "items"];
        HashSet<string> Words(string t) => System.Text.RegularExpressions.Regex.Matches(t, "[A-Za-z]{3,}")
            .Select(m => Stem(m.Value)).Where(w => !stop.Contains(w)).ToHashSet();
        var want = Words(activity);
        if (want.Count == 0) return fallback;
        CategoryInfo? best = null;
        int bestScore = 0;
        foreach (var c in categories)
        {
            int score = Words(c.Text.Split('|')[0]).Count(want.Contains);
            if (score > bestScore) { best = c; bestScore = score; }
        }
        return best?.Text ?? fallback;
    }

    private static int Find(IReadOnlyList<string> headers, params string[] names)
    {
        foreach (var n in names)
            for (int i = 0; i < headers.Count; i++)
                if (string.Equals(headers[i].Trim(), n, StringComparison.OrdinalIgnoreCase)) return i;
        foreach (var n in names)
            for (int i = 0; i < headers.Count; i++)
                if (headers[i].Contains(n, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>The values (header order) of a new supplier from an analysed document.</summary>
    public static string[] NewSupplier(IReadOnlyList<string> headers, string id, AnalysisResult r, string? name,
        string? category, string? template)
    {
        var v = new string[headers.Count];
        for (int i = 0; i < v.Length; i++) v[i] = "";
        void Set(int col, string? value) { if (col >= 0 && col < v.Length && !string.IsNullOrWhiteSpace(value)) v[col] = value.Trim(); }
        v[0] = id;
        int nameCol = Find(headers, "Supplier Name (English)", "Supplier Name");
        if (nameCol < 0) nameCol = 1;
        Set(nameCol, string.IsNullOrWhiteSpace(name) ? r.CompanyName : name);
        Set(Find(headers, "Chinese Name", "Native Name"), r.LocalName);
        // the supplier's phone / e-mail go under its address
        // the approved format: "Company Address: ..." (as the details checked with ChatGPT are written)
        var address = r.Address.Trim().Length == 0 || r.Address.TrimStart().StartsWith("Company Address", StringComparison.OrdinalIgnoreCase)
            ? r.Address : "Company Address: " + r.Address.Trim();
        Set(Find(headers, "Company Address", "Address"), r.Contact.Length > 0 ? (address + "\n" + r.Contact.Replace(" · ", "\n")).Trim() : address);
        Set(SupplierBanks.BaseColumn(headers), r.Bank);
        Set(Find(headers, "Category (English | Arabic)", "Category"), category);
        Set(Find(headers, "DEFAULT TEMPLATE", "Template"), template);
        Set(Find(headers, "Company’s Activities", "Company's Activities", "Activities"), r.Activity);
        return v;
    }

    /// <summary>
    /// Number of the account a new banking paragraph goes to on a supplier's row: the first numbered account
    /// (2, 3 ...) whose column is missing or empty. Account 1 when even that is empty.
    /// </summary>
    public static int FreeBankNumber(IReadOnlyList<string> headers, IReadOnlyList<string> row)
    {
        var accounts = SupplierBanks.Accounts(headers, row);
        if (accounts.FirstOrDefault(a => a.Number == 1) is { } first && first.Text.Trim().Length == 0) return 1;
        for (int n = 2; n <= SupplierBanks.MaxAccounts; n++)
            if (!accounts.Any(a => a.Number == n && a.Text.Trim().Length > 0)) return n;
        return -1;
    }
}
