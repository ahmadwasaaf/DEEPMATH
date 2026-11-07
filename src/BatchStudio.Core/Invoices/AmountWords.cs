namespace BatchStudio.Core.Invoices;

public enum InvoiceCurrency { USD, SAR }

/// <summary>
/// Bilingual (Arabic + English) amount in words - a literal port of the WordsUSD / WordsSAR VBA modules,
/// so archived invoices read exactly like the ones produced by BATCH STUDIO V28.
/// </summary>
public static class AmountWords
{
    public static string Format(decimal amount, InvoiceCurrency currency)
    {
        var value = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        bool negative = value < 0;
        value = Math.Abs(value);
        long whole = (long)decimal.Truncate(value);
        long fraction = (long)Math.Round((value - whole) * 100m, 0, MidpointRounding.AwayFromZero);
        if (fraction == 100) { whole++; fraction = 0; }

        bool usd = currency == InvoiceCurrency.USD;
        string ar = ArabicNumber(whole) + " " + ArabicCurrency(whole, true, usd);
        string en = EnglishNumber(whole) + " " + (usd ? (whole == 1 ? "US Dollar" : "US Dollars") : (whole == 1 ? "Saudi Riyal" : "Saudi Riyals"));
        if (fraction > 0)
        {
            ar += " و" + ArabicNumber(fraction) + " " + ArabicCurrency(fraction, false, usd);
            en += " and " + EnglishNumber(fraction) + " " + (usd ? (fraction == 1 ? "Cent" : "Cents") : (fraction == 1 ? "Halala" : "Halalas"));
        }
        if (negative)
        {
            ar = "سالب " + ar;
            en = "Minus " + en;
        }
        var tail = usd ? "Total Invoice Amount" : "Total Amount Including VAT";
        return $"({ar} فقط) {tail}\n({en} Only)";
    }

    private static string ArabicCurrency(long n, bool major, bool usd)
    {
        string[] forms = usd
            ? (major ? ["دولار أمريكي", "دولاران أمريكيان", "دولارات أمريكية", "دولارًا أمريكيًا"] : ["سنت", "سنتان", "سنتات", "سنتًا"])
            : (major ? ["ريال سعودي", "ريالان سعوديان", "ريالات سعودية", "ريالًا سعوديًا"] : ["هللة", "هللتان", "هللات", "هللة"]);
        return n == 1 ? forms[0] : n == 2 ? forms[1] : n is >= 3 and <= 10 ? forms[2] : forms[3];
    }

    private static readonly string[] ArSingular = ["", "ألف", "مليون", "مليار", "تريليون"];
    private static readonly string[] ArDual = ["", "ألفان", "مليونان", "ملياران", "تريليونان"];
    private static readonly string[] ArPlural = ["", "آلاف", "ملايين", "مليارات", "تريليونات"];
    private static readonly string[] ArOnes =
        ["", "واحد", "اثنان", "ثلاثة", "أربعة", "خمسة", "ستة", "سبعة", "ثمانية", "تسعة", "عشرة",
         "أحد عشر", "اثنا عشر", "ثلاثة عشر", "أربعة عشر", "خمسة عشر", "ستة عشر", "سبعة عشر", "ثمانية عشر", "تسعة عشر"];
    private static readonly string[] ArTens = ["", "", "عشرون", "ثلاثون", "أربعون", "خمسون", "ستون", "سبعون", "ثمانون", "تسعون"];
    private static readonly string[] ArHundreds = ["", "مائة", "مائتان", "ثلاثمائة", "أربعمائة", "خمسمائة", "ستمائة", "سبعمائة", "ثمانمائة", "تسعمائة"];

    public static string ArabicNumber(long n)
    {
        if (n == 0) return "صفر";
        string result = "";
        int level = 0;
        while (n > 0)
        {
            int part = (int)(n % 1000);
            if (part > 0)
            {
                var fragment = ArabicTriplet(part);
                if (level > 0)
                    fragment = part switch
                    {
                        1 => ArSingular[level],
                        2 => ArDual[level],
                        >= 3 and <= 10 => fragment + " " + ArPlural[level],
                        _ => fragment + " " + ArSingular[level],
                    };
                result = result.Length > 0 ? fragment + " و" + result : fragment;
            }
            n /= 1000;
            level++;
            if (level > 4 && n > 0) throw new ArgumentOutOfRangeException(nameof(n), "Amount too large");
        }
        return result;
    }

    private static string ArabicTriplet(int n)
    {
        int h = n / 100, r = n % 100;
        string result = h > 0 ? ArHundreds[h] : "";
        if (r > 0)
        {
            string tail = r < 20 ? ArOnes[r] : (r % 10 > 0 ? ArOnes[r % 10] + " و" + ArTens[r / 10] : ArTens[r / 10]);
            result = result.Length > 0 ? result + " و" + tail : tail;
        }
        return result;
    }

    private static readonly string[] EnLevels = ["", "Thousand", "Million", "Billion", "Trillion"];
    private static readonly string[] EnOnes =
        ["", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve",
         "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"];
    private static readonly string[] EnTens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    public static string EnglishNumber(long n)
    {
        if (n == 0) return "Zero";
        string result = "";
        int level = 0;
        while (n > 0)
        {
            int part = (int)(n % 1000);
            if (part > 0)
            {
                var fragment = EnglishTriplet(part);
                if (level > 0) fragment += " " + EnLevels[level];
                result = result.Length > 0 ? fragment + " " + result : fragment;
            }
            n /= 1000;
            level++;
            if (level > 4 && n > 0) throw new ArgumentOutOfRangeException(nameof(n), "Amount too large");
        }
        return result;
    }

    private static string EnglishTriplet(int n)
    {
        int h = n / 100, r = n % 100;
        string result = h > 0 ? EnOnes[h] + " Hundred" : "";
        if (r > 0)
        {
            string tail = r < 20 ? EnOnes[r] : (r % 10 > 0 ? EnTens[r / 10] + "-" + EnOnes[r % 10] : EnTens[r / 10]);
            result = result.Length > 0 ? result + " " + tail : tail;
        }
        return result;
    }
}
