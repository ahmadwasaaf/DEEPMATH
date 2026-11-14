using System.Text;

namespace BatchStudio.Core.Banking;

/// <summary>
/// A well-known bank: its names and the card colours (its brand colours, simplified). Only colours and names: no logos.
/// </summary>
public sealed record BankBrand(
    string Key, string NameEn, string NameAr, string Monogram, string Country,
    string From, string To, string Accent, string AccentText);

/// <summary>
/// Recognises the well-known banks of Egypt and Saudi Arabia from the name the user typed (Arabic or English, any
/// spelling: "الأهلي", "البنك الاهلى المصرى", "NBE", "National Bank of Egypt" ...), so their cards get the bank's colours.
/// </summary>
public static class BankBrands
{
    private sealed record Rule(BankBrand Brand, string[] Phrases, string[] Tokens, string[] Not);

    private static readonly Rule[] Rules =
    [
        // Egypt
        new(new("nbe", "National Bank of Egypt", "البنك الأهلي المصري", "NBE", "EG", "#004D25", "#00813D", "#FDDB23", "#003A1C"),
            ["الاهليالمصري", "البنكالاهلي", "الاهلي", "nationalbankofegypt", "ahly", "alahly", "elahly"], ["nbe"], ["سعودي", "saudi", "snb", "kuwait", "كويت", "قطر", "qatar", "عمان", "oman"]),
        new(new("aaib", "Arab African International Bank", "البنك العربي الأفريقي الدولي", "AAIB", "EG", "#0B2346", "#17427A", "#E2A93B", "#0B2346"),
            ["العربيالافريقي", "الافريقيالدولي", "arabafrican", "arabafrica"], ["aaib"], []),
        new(new("misr", "Banque Misr", "بنك مصر", "BM", "EG", "#5E0F1C", "#9A1F33", "#D4AF5A", "#4A0B16"),
            ["بنكمصر", "banquemisr", "bankmisr", "banquemasr", "bankmasr", "misrbank"], ["bm"], ["ايران", "iran", "خارجي"]),
        new(new("cairo", "Banque du Caire", "بنك القاهرة", "BdC", "EG", "#2B2A29", "#4A4744", "#C99729", "#2B2A29"),
            ["بنكالقاهره", "بنكالقاهرة", "banqueducaire", "bankofcairo", "banquecaire", "bankducaire"], ["bdc"], []),
        new(new("alex", "Bank of Alexandria", "بنك الإسكندرية", "alex", "EG", "#00502A", "#008C45", "#F7941D", "#00502A"),
            ["بنكالاسكندريه", "بنكالاسكندرية", "اسكندريه", "bankofalexandria", "alexbank", "alexandria"], ["alex"], []),
        // Saudi Arabia
        new(new("rajhi", "Al Rajhi Bank", "مصرف الراجحي", "AR", "SA", "#123B86", "#1E5AB7", "#7FB2FF", "#0E2F6B"),
            ["الراجحي", "alrajhi", "rajhi"], [], []),
        new(new("albilad", "Bank Albilad", "بنك البلاد", "B", "SA", "#7A3410", "#E0661E", "#FFD08A", "#5E270B"),
            ["بنكالبلاد", "مصرفالبلاد", "البلاد", "albilad", "bilad"], [], []),
        new(new("sab", "Saudi Awwal Bank", "البنك السعودي الأول", "SAB", "SA", "#16161A", "#3A3A44", "#E2001A", "#16161A"),
            ["البنكالسعوديالاول", "السعوديالاول", "البنكالاول", "saudiawwal", "saudifirst", "alawwal", "awwal", "sabb"], ["sab", "ساب"], []),
        new(new("jazira", "Bank AlJazira", "بنك الجزيرة", "BAJ", "SA", "#003556", "#007CB7", "#B7CCD8", "#003556"),
            ["بنكالجزيره", "بنكالجزيرة", "الجزيره", "الجزيرة", "aljazira", "jazira", "jazeera"], ["baj"], []),
        new(new("bsf", "Banque Saudi Fransi", "البنك السعودي الفرنسي", "BSF", "SA", "#063F33", "#0E6B55", "#9BD3A5", "#063F33"),
            ["السعوديالفرنسي", "الفرنسي", "saudifransi", "fransi", "banquesaudi"], ["bsf"], []),
    ];

    public static IReadOnlyList<BankBrand> All { get; } = Rules.Select(r => r.Brand).ToList();

    /// <summary>The well-known bank whose name this is, or null.</summary>
    public static BankBrand? Match(string? bankName)
    {
        if (string.IsNullOrWhiteSpace(bankName)) return null;
        var norm = Normalize(bankName);
        var compact = norm.Replace(" ", "");
        var tokens = norm.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var r in Rules)
        {
            if (r.Not.Any(n => compact.Contains(Normalize(n).Replace(" ", "")))) continue;
            if (r.Tokens.Any(t => tokens.Contains(t))) return r.Brand;
            if (r.Phrases.Any(p => compact.Contains(Normalize(p).Replace(" ", "")))) return r.Brand;
        }
        return null;
    }

    /// <summary>Lower case; Arabic letters folded (أإآ → ا, ة → ه, ى → ي, no diacritics or tatweel); punctuation → spaces.</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch0 in s.Trim().ToLowerInvariant())
        {
            var ch = ch0 switch
            {
                'أ' or 'إ' or 'آ' or 'ٱ' => 'ا',
                'ة' => 'ه',
                'ى' => 'ي',
                'ؤ' => 'و',
                'ئ' => 'ي',
                'é' or 'è' => 'e',
                _ => ch0,
            };
            if (ch is >= 'ً' and <= 'ْ' || ch == 'ـ') continue;           // diacritics, tatweel
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
