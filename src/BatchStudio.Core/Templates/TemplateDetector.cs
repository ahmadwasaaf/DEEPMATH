using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace BatchStudio.Core.Templates;

/// <summary>
/// Finds the variable cells of an invoice template (party name, native name, date, invoice no,
/// total, bank block, address block, items table, amount in words, VAT...).
/// Order of evidence: defined names (0.99) &gt; formulas (amount-to-words argument, VAT %) &gt;
/// labels next to the cell, English or Arabic &gt; layout (largest header text, script below it).
/// </summary>
public static class TemplateDetector
{
    private const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Dictionary<string, Regex> Labels = new()
    {
        ["invoice_no"] = new(@"invoice\s*(no|number|#)|inv\.?\s*no|رقم\s*الفاتورة", O),
        ["issue_date"] = new(@"issue\s*date|invoice\s*date|^\s*date\s*:?\s*$|التاريخ|تاريخ\s*الفاتورة", O),
        ["total"] = new(@"total\s*value|grand\s*total|total\s*due|net\s*total|الإجمالي\s*النهائي|الاجمالي\s*النهائي", O),
        ["bank"] = new(@"payment\s*&?\s*banking|bank(ing)?\s*(details|instructions)|بيانات\s*البنك", O),
        ["address"] = new(@"company\s*address|^\s*address\s*:|عنوان\s*الشركة", O),
        ["party_label"] = new(@"السيد\s*/\s*السادة|messrs|bill\s*to|sold\s*to|invoice\s*to", O),
        ["vat_no"] = new(@"vat\s*(no|number)|الرقم\s*الضريبي", O),
        ["subtotal"] = new(@"sub\s*total|الإجمالي\s*قبل", O),
        ["buyer"] = new(@"buyer\s*/?\s*consignee|المشتري", O),
    };

    private static readonly (string Key, Regex Rx)[] ItemHeaders =
    [
        ("code", new(@"^(item\s*)?code$|رمز|كود", O)),
        ("description", new(@"descriptions?|البيان|الوصف", O)),
        ("qty", new(@"^qty$|quantity|الكمية", O)),
        ("unit", new(@"^unit$|الوحدة", O)),
        ("price", new(@"price|السعر", O)),
        ("amount", new(@"total\s*amount|^amount$|الإجمالي|المبلغ", O)),
    ];

    private static readonly Dictionary<string, string> NamedHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["InvoiceTarget"] = TemplateFields.Total,
        ["SupplierBankTarget"] = TemplateFields.Bank,
        ["SupplierAddressTarget"] = TemplateFields.Address,
        ["SupplierEnglishTarget"] = TemplateFields.PartyName,
        ["SupplierChineseTarget"] = TemplateFields.PartyNativeName,
        ["InvoiceNumberTarget"] = TemplateFields.InvoiceNo,
        ["InvoiceDateTarget"] = TemplateFields.IssueDate,
    };

    private static readonly Regex TitleWords = new(@"^\s*(commercial\s+)?(tax\s+)?invoice\s*$|^فاتورة", O);
    private static readonly Regex LineFormula = new(@"^=\s*\$?[A-Z]{1,3}\$?\d+\s*\*\s*\$?[A-Z]{1,3}\$?\d+\s*$", O);
    private static readonly Regex WordsFormula = new(@"amountwords|tafqeet|spellnumber|numbertowords", O);
    private static readonly Regex InvoiceNoLike = new(@"^[A-Za-z]{2,5}-\d{5,}$", O);

    public static TemplateMap DetectFile(string path, string? sheet = null)
    {
        var model = SheetModel.Load(path, sheet);
        var map = Detect(model);
        map.TemplateFile = Path.GetFileName(path);
        map.FileHash = TemplateMap.HashFile(path);
        return map;
    }

    public static TemplateMap Detect(SheetModel s)
    {
        var map = new TemplateMap { Sheet = s.Name, DetectedUtc = DateTime.UtcNow };
        var f = map.Fields;
        void Put(string field, FieldMapping m)
        {
            if (!f.TryGetValue(field, out var cur) || m.Confidence > cur.Confidence) f[field] = m;
        }
        FieldMapping Found(int r, int c, double conf, string method, string? sample = null, string? note = null) =>
            new() { Cell = Ref(r, c), Confidence = conf, Method = method, Sample = Short(sample), Note = note };

        // 1) defined names in this sheet
        foreach (var (name, (r, c)) in s.Names)
            if (NamedHints.TryGetValue(name, out var field))
                Put(field, Found(r, c, 0.99, $"named range {name}", Val(s, r, c)));

        // 2) items table
        int? headerRow = null;
        for (int r = s.Row1; r <= s.Row2 && headerRow == null; r++)
        {
            var cols = new Dictionary<string, int>();
            for (int c = s.Col1; c <= s.Col2; c++)
            {
                var v = Val(s, r, c);
                if (v.Length == 0) continue;
                foreach (var (key, rx) in ItemHeaders)
                    if (!cols.ContainsKey(key) && rx.IsMatch(v)) { cols[key] = c; break; }
            }
            if (cols.Count < 3 || !cols.TryGetValue("amount", out var amt)) continue;
            var rows = new List<int>();
            for (int rr = r + 1; rr <= s.Row2; rr++)
            {
                s.Cells.TryGetValue((rr, amt), out var cell);
                bool line = cell != null && (cell.IsNumber || (cell.Formula != null && LineFormula.IsMatch(cell.Formula)));
                if (line) rows.Add(rr);
                else if (rows.Count > 0) break;
            }
            if (rows.Count == 0) continue;
            headerRow = r;
            map.Items = new ItemsTableMapping
            {
                HeaderRow = r, FirstRow = rows[0], LastRow = rows[^1], Confidence = 0.95,
                Columns = cols.ToDictionary(kv => kv.Key, kv => XLHelper.GetColumnLetterFromNumber(kv.Value)),
            };
        }

        // 3) labels
        foreach (var cell in s.Cells.Values.OrderBy(c => c.Row).ThenBy(c => c.Col))
        {
            if (cell.Formula != null || cell.Row == headerRow) continue;
            var text = cell.Text;
            foreach (var field in new[] { "invoice_no", "issue_date", "total", "bank", "address", "party_label", "vat_no", "buyer" })
            {
                var m = Labels[field].Match(text);
                if (!m.Success) continue;
                var rest = text[(m.Index + m.Length)..].Trim(' ', ':', '\n', '/', '|');
                if (rest.Length > 0 && Labels[field].Replace(rest, "").Trim(' ', ':', '/', '|').Length == 0) rest = ""; // bilingual label
                bool multiline = text.Trim().Contains('\n');
                bool arabicLabel = Regex.IsMatch(m.Value, @"[؀-ۿ]");
                int r = cell.Row, c = cell.Col;

                if (field is "bank" or "address" or "buyer" or "vat_no")
                {
                    var key = field switch
                    {
                        "bank" => TemplateFields.Bank,
                        "address" => TemplateFields.Address,
                        "buyer" => TemplateFields.BuyerBlock,
                        _ => TemplateFields.VatNo,
                    };
                    if (multiline && rest.Length > 5)
                        Put(key, Found(r, c, 0.93, "label + content in same cell", text));
                    else if (field == "vat_no" && Regex.Replace(rest, @"\D", "").Length >= 8)
                        Put(key, Found(r, c, 0.85, "label + number in same cell", text));
                    else if (rest.Length == 0)
                        for (int rr = r + 1; rr <= Math.Min(r + 3, s.Row2); rr++)
                        {
                            var (ar, ac) = Anchor(s, rr, c);
                            var v = Val(s, ar, ac);
                            if (v.Length > 5) { Put(key, Found(ar, ac, 0.7, "heading + block below", v)); break; }
                        }
                    continue;
                }
                if (field == "party_label")
                {
                    var hit = Neighbour(s, r, c, arabicLabel);
                    if (hit != null)
                        Put(TemplateFields.PartyName, Found(hit.Value.Row, hit.Value.Col, 0.9, $"next to label \"{m.Value.Trim()}\"", hit.Value.Text));
                    continue;
                }
                if (rest.Length > 2 && field != "total")
                {
                    Put(field, Found(r, c, 0.6, "value inside label cell", text));
                    continue;
                }
                var n = Neighbour(s, r, c, arabicLabel);
                if (n != null)
                    Put(field, Found(n.Value.Row, n.Value.Col, n.Value.Text.Length > 0 ? 0.9 : 0.75,
                        $"next to label \"{m.Value.Trim()}\"", n.Value.Text));
            }
        }

        // 4) amount in words
        foreach (var cell in s.Cells.Values)
        {
            if (cell.Formula != null && WordsFormula.IsMatch(cell.Formula))
            {
                Put(TemplateFields.AmountWords, Found(cell.Row, cell.Col, 0.95, "amount-to-words formula", cell.Formula));
                var arg = Regex.Match(cell.Formula, @"\(([^()]+)\)\s*$");
                if (arg.Success)
                {
                    var a = arg.Groups[1].Value.Trim();
                    if (TryParseRef(a, out var ar, out var ac))
                        Put(TemplateFields.Total, Found(ar, ac, 0.97, "argument of amount-to-words formula", Val(s, ar, ac)));
                    else if (s.ForeignNames.TryGetValue(a, out var other))
                        map.Warnings.Add($"The amount-in-words formula uses \"{a}\", which points to sheet \"{other}\" - " +
                                         "so this template shows that sheet's total in words. The app computes words from this template's own total.");
                }
                if (Regex.IsMatch(cell.Formula, @"\[\d+\]!"))
                    map.Warnings.Add("Amount in words uses a function from an external workbook (slow, breaks when that file is missing). " +
                                     "The app writes the words itself (USD / SAR).");
            }
            else if (cell.Formula == null && cell.Text.Length > 15 && Regex.IsMatch(cell.Text, @"\bonly\b\)?|فقط", O))
                Put(TemplateFields.AmountWords, Found(cell.Row, cell.Col, 0.85, "text containing \"only\" / \"فقط\"", cell.Text));
        }

        // 5) VAT
        foreach (var cell in s.Cells.Values)
        {
            if (cell.Formula == null) continue;
            var pm = Regex.Match(cell.Formula, @"\*\s*(\d+(?:\.\d+)?)\s*%");
            if (!pm.Success) continue;
            Put(TemplateFields.VatAmount, Found(cell.Row, cell.Col, 0.9, $"formula with {pm.Groups[1].Value}%", cell.Formula));
            map.VatRate = double.Parse(pm.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0;
        }
        // grand total = SUM(...) just below the VAT cell that references it (Madar: G30 = SUM(G28,G29))
        if (f.TryGetValue(TemplateFields.VatAmount, out var vat) && vat.Cell != null && TryParseRef(vat.Cell, out var vr, out var vc))
            for (int rr = vr + 1; rr <= vr + 3; rr++)
                if (s.Cells.TryGetValue((rr, vc), out var tc) && tc.Formula != null &&
                    Regex.IsMatch(tc.Formula, @"\b" + vat.Cell + @"\b", O))
                {
                    Put(TemplateFields.Total, Found(rr, vc, 0.9, "sum below the VAT amount", tc.Formula));
                    break;
                }

        // 6) party name from layout: largest Latin text above the items table
        int topLimit = (headerRow ?? s.Row2) - 1;
        var cands = s.Cells.Values.Where(c =>
                c.Row <= topLimit && c.Formula == null && c.Text.Length > 0 && c.Text.Length <= 70 &&
                !c.Text.Contains('\n') && !c.Text.Contains('|') && !IsLabel(c.Text) && !TitleWords.IsMatch(c.Text) &&
                !Regex.IsMatch(c.Text, @"^[\d\s\-:./]+$") && !InvoiceNoLike.IsMatch(c.Text) && Script(c.Text) == null)
            .OrderByDescending(c => c.FontSize).ThenBy(c => c.Row).ToList();
        if (cands.Count > 0)
        {
            var b = cands[0];
            Put(TemplateFields.PartyName, Found(b.Row, b.Col, b.FontSize >= 16 ? 0.8 : 0.55,
                $"largest text in header (font {b.FontSize:0.#})", b.Text));
        }
        if (f.TryGetValue(TemplateFields.PartyName, out var pn) && pn.Cell != null && TryParseRef(pn.Cell, out var pr, out var pc))
        {
            if ((pn.Sample ?? "").Contains('|')) pn.Note = "Bilingual name in one cell (native | English)";
            for (int rr = pr + 1; rr <= pr + 2; rr++)
            {
                var (ar, ac) = Anchor(s, rr, pc);
                var v = Val(s, ar, ac);
                var sc = v.Length > 0 ? Script(v) : null;
                if (sc != null && sc != "Arabic")
                {
                    Put(TemplateFields.PartyNativeName, Found(ar, ac, 0.85, $"{sc} script below the party name", v));
                    break;
                }
            }
        }

        // 7) leftovers that look like invoice numbers without a label
        if (headerRow != null)
            foreach (var cell in s.Cells.Values.Where(c => c.Row < headerRow && c.Formula == null && InvoiceNoLike.IsMatch(c.Text)))
            {
                var used = f.TryGetValue(TemplateFields.InvoiceNo, out var inv) ? inv.Cell : null;
                if (used != Ref(cell.Row, cell.Col))
                    map.Warnings.Add($"{Ref(cell.Row, cell.Col)} holds \"{cell.Text}\" (looks like an invoice number) but has no label; " +
                                     $"probably left over. The labelled cell {used ?? "-"} is used.");
            }

        foreach (var core in TemplateFields.Core)
            if (!f.ContainsKey(core)) f[core] = new FieldMapping { Cell = null, Confidence = 0, Method = "not found" };
        return map;
    }

    // ------------------------------------------------------------------ helpers
    private static string Val(SheetModel s, int r, int c) => s.Cells.TryGetValue((r, c), out var x) ? x.Text : "";

    private static bool IsLabel(string t) => Labels.Values.Any(rx => rx.IsMatch(t));

    private static MergedArea? MergedAt(SheetModel s, int r, int c) =>
        s.Merged.FirstOrDefault(m => r >= m.Row1 && r <= m.Row2 && c >= m.Col1 && c <= m.Col2);

    private static (int, int) Anchor(SheetModel s, int r, int c)
    {
        var m = MergedAt(s, r, c);
        return m == null ? (r, c) : (m.Row1, m.Col1);
    }

    private static int SpanEnd(SheetModel s, int r, int c) => MergedAt(s, r, c)?.Col2 ?? c;
    private static int SpanStart(SheetModel s, int r, int c) => MergedAt(s, r, c)?.Col1 ?? c;

    /// <summary>Value cell beside a label: right then left (left first for Arabic labels), then below; else the empty slot to the right.</summary>
    private static (int Row, int Col, string Text)? Neighbour(SheetModel s, int r, int c, bool preferLeft)
    {
        (int, int, string)? Scan(int dir)
        {
            int cc = dir > 0 ? SpanEnd(s, r, c) + 1 : SpanStart(s, r, c) - 1;
            for (int steps = 0; cc >= s.Col1 && cc <= s.Col2 && steps < 6; steps++)
            {
                var (ar, ac) = Anchor(s, r, cc);
                var v = Val(s, ar, ac);
                if (v.Length > 0) return IsLabel(v) ? null : (ar, ac, v);
                cc = dir > 0 ? SpanEnd(s, r, cc) + 1 : SpanStart(s, r, cc) - 1;
            }
            return null;
        }
        foreach (var d in preferLeft ? new[] { -1, 1 } : new[] { 1, -1 })
            if (Scan(d) is { } hit) return hit;
        var (br, bc) = Anchor(s, r + 1, c);
        var bv = Val(s, br, bc);
        if (bv.Length > 0 && !IsLabel(bv)) return (br, bc, bv);
        int rc = SpanEnd(s, r, c) + 1;
        return rc <= s.Col2 ? (r, rc, "") : null;
    }

    /// <summary>Name of the dominant non-Latin script, or null for Latin/neutral text.</summary>
    public static string? Script(string s)
    {
        var letters = s.Where(char.IsLetter).ToList();
        if (letters.Count == 0) return null;
        var nl = letters.Where(ch => ch > 'ɏ').ToList();
        if (nl.Count < letters.Count * 0.6) return null;
        int u = nl[0];
        return u switch
        {
            >= 0x0900 and <= 0x097F => "Hindi",
            >= 0x4E00 and <= 0x9FFF or >= 0x3400 and <= 0x4DBF => "Chinese",
            >= 0x0600 and <= 0x06FF or >= 0x0750 and <= 0x077F => "Arabic",
            >= 0xAC00 and <= 0xD7AF => "Korean",
            >= 0x3040 and <= 0x30FF => "Japanese",
            >= 0x0980 and <= 0x09FF => "Bengali",
            >= 0x0B80 and <= 0x0BFF => "Tamil",
            >= 0x0E00 and <= 0x0E7F => "Thai",
            >= 0x0400 and <= 0x04FF => "Cyrillic",
            _ => "Other",
        };
    }

    public static string Ref(int r, int c) => XLHelper.GetColumnLetterFromNumber(c) + r.ToString(CultureInfo.InvariantCulture);

    public static bool TryParseRef(string a1, out int row, out int col)
    {
        row = col = 0;
        var m = Regex.Match(a1.Trim(), @"^\$?([A-Za-z]{1,3})\$?(\d+)$");
        if (!m.Success) return false;
        col = XLHelper.GetColumnNumberFromLetter(m.Groups[1].Value.ToUpperInvariant());
        row = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return true;
    }

    private static string? Short(string? s) => s == null ? null : (s.Length > 80 ? s[..80] : s).Replace("\n", " / ");
}
