using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BatchStudio.Core.Workbook;

/// <summary>
/// Copies one worksheet of the BATCH STUDIO workbook (.xlsm) into a standalone .xlsx at package level, so its
/// formatting, images and print setup stay exactly as in the workbook (nothing is re-written by a library):
/// other sheets, VBA, calcChain and external links are left out; formulas pointing to other sheets keep their
/// last calculated value; defined names are kept only when they point into the copied sheet.
/// The workbook itself is only read.
/// </summary>
public static class SheetExtractor
{
    public sealed record SheetInfo(string Name, string State, string RelId, string PartName, int Index)
    {
        public bool Visible => State is "" or "visible";
    }

    /// <summary>The unzipped workbook package (read once, extract many sheets).</summary>
    public sealed class Package
    {
        internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        internal List<string> Order { get; } = [];
        internal List<string> SheetElements { get; } = [];
        internal Dictionary<string, Rel> WorkbookRels { get; } = new();
        public List<SheetInfo> Sheets { get; } = [];
        private string[]? _sharedStrings;

        internal string Text(string part) => Encoding.UTF8.GetString(Files[part]);

        internal string[] SharedStrings
        {
            get
            {
                if (_sharedStrings != null) return _sharedStrings;
                var part = WorkbookRels.Values.FirstOrDefault(r => r.Type.EndsWith("/sharedStrings"));
                var path = part == null ? null : Resolve("xl/workbook.xml", part.Target);
                if (path == null || !Files.ContainsKey(path)) return _sharedStrings = [];
                return _sharedStrings = Regex.Matches(Text(path), @"<si>.*?</si>|<si/>", RegexOptions.Singleline)
                    .Select(m => m.Value).ToArray();
            }
        }

        public SheetInfo? Find(string name) =>
            Sheets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    internal sealed record Rel(string Id, string Type, string Target, bool External, string Raw);

    public static Package Load(byte[] xlsm)
    {
        var p = new Package();
        using (var zip = new ZipArchive(new MemoryStream(xlsm), ZipArchiveMode.Read))
        {
            foreach (var e in zip.Entries)
            {
                if (e.FullName.EndsWith('/')) continue;
                using var s = e.Open();
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                p.Files[e.FullName] = ms.ToArray();
                p.Order.Add(e.FullName);
            }
        }
        if (!p.Files.ContainsKey("xl/workbook.xml")) throw new InvalidDataException("Not an Excel workbook (xl/workbook.xml is missing).");
        foreach (var r in ParseRels(p.Text("xl/_rels/workbook.xml.rels"))) p.WorkbookRels[r.Id] = r;
        var wb = p.Text("xl/workbook.xml");
        int i = 0;
        foreach (Match m in Regex.Matches(wb, @"<sheet\b[^>]*/>"))
        {
            var el = m.Value;
            var name = WebUtility.HtmlDecode(Attr(el, "name") ?? "");
            var state = Attr(el, "state") ?? "";
            var rid = Attr(el, "r:id") ?? "";
            var part = p.WorkbookRels.TryGetValue(rid, out var rel) ? Resolve("xl/workbook.xml", rel.Target) : "";
            p.SheetElements.Add(el);
            p.Sheets.Add(new SheetInfo(name, state, rid, part, i++));
        }
        return p;
    }

    /// <summary>
    /// A stable fingerprint of what the sheet looks like and holds (its XML, drawings/images, the shared strings it
    /// uses, styles and theme). Unchanged when only other sheets of the workbook change.
    /// </summary>
    public static string Fingerprint(Package p, string sheetName)
    {
        var sheet = p.Find(sheetName) ?? throw new InvalidOperationException($"Sheet \"{sheetName}\" was not found in the workbook.");
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var part in Reachable(p, sheet.PartName).OrderBy(x => x, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(part));
            sha.AppendData(p.Files[part]);
        }
        foreach (var r in p.WorkbookRels.Values.Where(r => r.Type.EndsWith("/styles") || r.Type.EndsWith("/theme")))
        {
            var t = Resolve("xl/workbook.xml", r.Target);
            if (p.Files.TryGetValue(t, out var b)) sha.AppendData(b);
        }
        var ss = p.SharedStrings;
        foreach (Match m in Regex.Matches(p.Text(sheet.PartName), @"<c\b[^>]*\bt=""s""[^>]*>\s*<v>(\d+)</v>"))
        {
            int idx = int.Parse(m.Groups[1].Value);
            sha.AppendData(Encoding.UTF8.GetBytes(idx < ss.Length ? ss[idx] : "?"));
        }
        sha.AppendData(Encoding.UTF8.GetBytes(sheet.Name + "|" + sheet.Index));
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    /// <summary>Parts reachable from the sheet through its relationships (the sheet itself, drawings, images, tables ...).</summary>
    private static HashSet<string> Reachable(Package p, string sheetPart)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal) { sheetPart };
        var stack = new Stack<string>();
        stack.Push(sheetPart);
        while (stack.Count > 0)
        {
            var part = stack.Pop();
            var rp = RelsPath(part);
            if (!p.Files.ContainsKey(rp)) continue;
            keep.Add(rp);
            foreach (var r in ParseRels(p.Text(rp)))
            {
                if (r.External) continue;
                var t = Resolve(part, r.Target);
                if (p.Files.ContainsKey(t) && keep.Add(t)) stack.Push(t);
            }
        }
        return keep;
    }

    public static byte[] Extract(Package p, string sheetName, bool keepValidations = false)
    {
        var sheet = p.Find(sheetName) ?? throw new InvalidOperationException($"Sheet \"{sheetName}\" was not found in the workbook.");
        var files = new Dictionary<string, byte[]>(p.Files, StringComparer.Ordinal);

        // ---- parts to keep
        var keep = new HashSet<string>(StringComparer.Ordinal)
        {
            "[Content_Types].xml", "_rels/.rels", "xl/workbook.xml", "xl/_rels/workbook.xml.rels",
            "docProps/core.xml", "docProps/app.xml",
        };
        if (files.ContainsKey("_rels/.rels"))
            foreach (var r in ParseRels(p.Text("_rels/.rels")))
                if (!r.External) keep.Add(r.Target.TrimStart('/'));
        foreach (var r in p.WorkbookRels.Values)
            if (r.Type.EndsWith("/styles") || r.Type.EndsWith("/theme") || r.Type.EndsWith("/sharedStrings"))
                keep.Add(Resolve("xl/workbook.xml", r.Target));
        keep.UnionWith(Reachable(p, sheet.PartName));

        // ---- workbook.xml
        var wb = p.Text("xl/workbook.xml");
        var newSheet = Regex.Replace(p.SheetElements[sheet.Index], @"\sstate=""[^""]*""", "");
        newSheet = Regex.Replace(newSheet, @"sheetId=""\d+""", @"sheetId=""1""");
        wb = Regex.Replace(wb, @"<sheets>.*?</sheets>", _ => "<sheets>" + newSheet + "</sheets>", RegexOptions.Singleline);
        wb = Regex.Replace(wb, @"<externalReferences>.*?</externalReferences>", "", RegexOptions.Singleline);
        wb = Regex.Replace(wb, @"<pivotCaches>.*?</pivotCaches>", "", RegexOptions.Singleline);
        wb = Regex.Replace(wb, @"\s(activeTab|firstSheet)=""\d+""", "");
        wb = Regex.Replace(wb, @"<fileVersion\b[^>]*/>", @"<fileVersion appName=""xl""/>");
        wb = Regex.Replace(wb, @"\scodeName=""[^""]*""", "");
        wb = Regex.Replace(wb, @"<definedName\b([^>]*)>(.*?)</definedName>", m =>
        {
            var full = m.Value;
            var local = Regex.Match(m.Groups[1].Value, @"localSheetId=""(\d+)""");
            if (local.Success)
            {
                if (int.Parse(local.Groups[1].Value) != sheet.Index) return "";
                full = full.Replace(local.Value, @"localSheetId=""0""");
            }
            var body = WebUtility.HtmlDecode(m.Groups[2].Value);
            var refs = Regex.Matches(body, @"(?:'((?:[^']|'')+)'|([A-Za-z0-9_.\u0080-￿]+))!")
                .Select(r => (r.Groups[1].Success ? r.Groups[1].Value : r.Groups[2].Value).Replace("''", "'"))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (refs.Count == 0 || refs.Count > 1 || !refs.Contains(sheet.Name) || body.Contains('[')) return "";
            return full;
        }, RegexOptions.Singleline);
        wb = Regex.Replace(wb, @"<definedNames>\s*</definedNames>", "");
        files["xl/workbook.xml"] = Encoding.UTF8.GetBytes(wb);

        // ---- workbook rels
        var rels = new StringBuilder();
        foreach (var r in p.WorkbookRels.Values)
        {
            var t = Resolve("xl/workbook.xml", r.Target);
            bool drop = r.Type.EndsWith("/worksheet") || r.Type.EndsWith("/chartsheet") || r.Type.EndsWith("/vbaProject") ||
                        r.Type.EndsWith("/calcChain") || r.Type.EndsWith("/externalLink");
            if (r.Id == sheet.RelId || (keep.Contains(t) && !drop)) rels.Append(r.Raw);
        }
        files["xl/_rels/workbook.xml.rels"] = Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n" +
            "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" + rels + "</Relationships>");

        // ---- the sheet: formulas pointing outside keep their value; validations / extLst / codeName dropped
        files[sheet.PartName] = Encoding.UTF8.GetBytes(FixSheetXml(p.Text(sheet.PartName), keepValidations));

        // ---- content types
        var ct = p.Text("[Content_Types].xml");
        ct = ct.Replace("application/vnd.ms-excel.sheet.macroEnabled.main+xml",
                        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml");
        ct = Regex.Replace(ct, @"<Override\b[^>]*PartName=""/([^""]+)""[^>]*/>", m => keep.Contains(m.Groups[1].Value) ? m.Value : "");
        ct = Regex.Replace(ct, @"<Default\b[^>]*ContentType=""application/vnd\.ms-office\.vbaProject""[^>]*/>", "");
        files["[Content_Types].xml"] = Encoding.UTF8.GetBytes(ct);

        if (files.TryGetValue("docProps/app.xml", out var app))
            files["docProps/app.xml"] = Encoding.UTF8.GetBytes(
                Regex.Replace(Encoding.UTF8.GetString(app), @"<HeadingPairs>.*?</TitlesOfParts>", "", RegexOptions.Singleline));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "[Content_Types].xml" }.Concat(p.Order.Where(n => n != "[Content_Types].xml")))
            {
                if (!keep.Contains(name) || !files.TryGetValue(name, out var data)) continue;
                var e = zip.CreateEntry(name, CompressionLevel.Optimal);
                e.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using var s = e.Open();
                s.Write(data);
            }
        }
        return ms.ToArray();
    }

    internal static string FixSheetXml(string sx, bool keepValidations)
    {
        const RegexOptions S = RegexOptions.Singleline;
        var cellRx = new Regex(@"<c\b[^>]*?/>|<c\b[^>]*?[^/]>.*?</c>", S);
        var fRx = new Regex(@"<f\b([^>]*)>(.*?)</f>|<f\b([^>]*)/>", S);
        bool Outside(string formula) => Regex.IsMatch(formula, @"\[\d+\]|!");

        // shared formulas whose master points outside: their children must go too
        var deadShared = new HashSet<string>();
        foreach (Match c in cellRx.Matches(sx))
        {
            var f = fRx.Match(c.Value);
            if (!f.Success || !f.Groups[2].Success) continue;
            var attrs = f.Groups[1].Value;
            if (attrs.Contains("t=\"shared\"") && Outside(f.Groups[2].Value) && Attr(attrs, "si") is { } si) deadShared.Add(si);
        }

        sx = cellRx.Replace(sx, m =>
        {
            var cell = m.Value;
            var f = fRx.Match(cell);
            if (!f.Success) return cell;
            var attrs = f.Groups[1].Success ? f.Groups[1].Value : f.Groups[3].Value;
            var formula = f.Groups[2].Success ? f.Groups[2].Value : "";
            bool drop = Outside(formula) ||
                        (attrs.Contains("t=\"shared\"") && Attr(attrs, "si") is { } si && deadShared.Contains(si));
            if (!drop) return cell;
            cell = cell.Replace(f.Value, "");
            if (Regex.IsMatch(cell, @"^<c\b[^>]*\bt=""str"""))
            {
                var v = Regex.Match(cell, @"<v>(.*?)</v>", S);
                var head = Regex.Match(cell, @"^<c\b[^>]*?(?=/?>)").Value.Replace("t=\"str\"", "t=\"inlineStr\"");
                cell = $"{head}><is><t xml:space=\"preserve\">{(v.Success ? v.Groups[1].Value : "")}</t></is></c>";
            }
            return cell;
        });
        if (!keepValidations) sx = Regex.Replace(sx, @"<dataValidations\b.*?</dataValidations>", "", S);
        sx = Regex.Replace(sx, @"<extLst>.*?</extLst>", "", S);
        sx = Regex.Replace(sx, @"(<sheetPr\b[^>]*?)\scodeName=""[^""]*""", "$1");
        sx = Regex.Replace(sx, @"(<sheetView\b[^>]*?)\stabSelected=""\d""", "$1");
        var idx = sx.IndexOf("<sheetView ", StringComparison.Ordinal);
        if (idx >= 0) sx = sx.Insert(idx + "<sheetView ".Length, "tabSelected=\"1\" ");
        return sx;
    }

    // ------------------------------------------------------------------ package helpers
    internal static string RelsPath(string part)
    {
        int i = part.LastIndexOf('/');
        return i < 0 ? "_rels/" + part + ".rels" : part[..i] + "/_rels/" + part[(i + 1)..] + ".rels";
    }

    internal static string Resolve(string basePart, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        int i = basePart.LastIndexOf('/');
        var dir = i < 0 ? "" : basePart[..i];
        var parts = new List<string>(dir.Length == 0 ? [] : dir.Split('/'));
        foreach (var seg in target.Split('/'))
        {
            if (seg is "" or ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else parts.Add(seg);
        }
        return string.Join('/', parts);
    }

    internal static List<Rel> ParseRels(string xml) =>
        Regex.Matches(xml, @"<Relationship\b[^>]*/>")
            .Select(m => new Rel(Attr(m.Value, "Id") ?? "", Attr(m.Value, "Type") ?? "",
                                 WebUtility.HtmlDecode(Attr(m.Value, "Target") ?? ""),
                                 string.Equals(Attr(m.Value, "TargetMode"), "External", StringComparison.OrdinalIgnoreCase), m.Value))
            .ToList();

    internal static string? Attr(string element, string name)
    {
        var m = Regex.Match(element, @"(?<![\w:])" + Regex.Escape(name) + @"=""([^""]*)""");
        return m.Success ? m.Groups[1].Value : null;
    }
}
