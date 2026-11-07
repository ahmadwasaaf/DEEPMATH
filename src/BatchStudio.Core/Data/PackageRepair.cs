using System.IO.Compression;
using System.Text.RegularExpressions;
using ClosedXML.Excel;

namespace BatchStudio.Core.Data;

/// <summary>
/// Opens Excel files that Excel itself accepts but ClosedXML rejects because a relationship points to a
/// part that is not in the package ("Specified part does not exist in the package") - common in files
/// produced by other tools. Dangling relationships are dropped in an in-memory copy; the file on disk is untouched.
/// </summary>
public static class PackageRepair
{
    public static XLWorkbook OpenWorkbook(string path)
    {
        var bytes = ReadAllShared(path);
        try
        {
            return new XLWorkbook(new MemoryStream(bytes));
        }
        catch (Exception original)
        {
            try { return new XLWorkbook(new MemoryStream(RemoveDanglingRelationships(bytes))); }
            catch { throw original; }
        }
    }

    public static byte[] ReadAllShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        return ms.ToArray();
    }

    public static byte[] RemoveDanglingRelationships(byte[] package)
    {
        using var input = new MemoryStream(package);
        using var src = new ZipArchive(input, ZipArchiveMode.Read);
        var names = new HashSet<string>(src.Entries.Select(e => e.FullName), StringComparer.OrdinalIgnoreCase);
        var output = new MemoryStream();
        using (var dst = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in src.Entries)
            {
                using var es = e.Open();
                using var buf = new MemoryStream();
                es.CopyTo(buf);
                var data = buf.ToArray();
                if (e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                    data = System.Text.Encoding.UTF8.GetBytes(CleanRels(e.FullName, System.Text.Encoding.UTF8.GetString(data), names));
                var ne = dst.CreateEntry(e.FullName, CompressionLevel.Fastest);
                using var ns = ne.Open();
                ns.Write(data, 0, data.Length);
            }
        }
        return output.ToArray();
    }

    /// <summary>
    /// Removes macro buttons (shapes with a macro) and ActiveX / form controls from a saved .xlsx,
    /// like the V28 archive did ("Remove controls from archived copy"). Pictures and plain shapes stay.
    /// </summary>
    public static void StripControls(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var input = new MemoryStream(bytes);
        using var src = new ZipArchive(input, ZipArchiveMode.Read);
        var output = new MemoryStream();
        bool changed = false;
        using (var dst = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var e in src.Entries)
            {
                using var es = e.Open();
                using var buf = new MemoryStream();
                es.CopyTo(buf);
                var data = buf.ToArray();
                var name = e.FullName.Replace('\\', '/');
                string? xml = null;
                if (Regex.IsMatch(name, @"^xl/worksheets/sheet\d+\.xml$", RegexOptions.IgnoreCase))
                {
                    xml = System.Text.Encoding.UTF8.GetString(data);
                    var x = RemoveAlternateContentWith(xml, "<controls");
                    x = Regex.Replace(x, @"<controls>.*?</controls>", "", RegexOptions.Singleline);
                    x = Regex.Replace(x, @"<legacyDrawing\b[^>]*/>", "");
                    if (x != xml) { xml = x; changed = true; } else xml = null;
                }
                else if (Regex.IsMatch(name, @"^xl/drawings/drawing\d+\.xml$", RegexOptions.IgnoreCase))
                {
                    var orig = System.Text.Encoding.UTF8.GetString(data);
                    var x = Regex.Replace(orig, @"<xdr:(twoCellAnchor|oneCellAnchor|absoluteAnchor)\b.*?</xdr:\1>",
                        m => Regex.IsMatch(m.Value, "\\bmacro=\"[^\"]+\"") ? "" : m.Value, RegexOptions.Singleline);
                    if (x != orig) { xml = x; changed = true; }
                }
                var ne = dst.CreateEntry(e.FullName, CompressionLevel.Optimal);
                using var ns = ne.Open();
                var outData = xml != null ? System.Text.Encoding.UTF8.GetBytes(xml) : data;
                ns.Write(outData, 0, outData.Length);
            }
        }
        if (changed) File.WriteAllBytes(path, output.ToArray());
    }

    /// <summary>Drops top-level &lt;mc:AlternateContent&gt; blocks that contain <paramref name="needle"/> (handles nesting).</summary>
    private static string RemoveAlternateContentWith(string s, string needle)
    {
        var sb = new System.Text.StringBuilder();
        int depth = 0, start = -1, pos = 0;
        foreach (Match m in Regex.Matches(s, @"<(/?)mc:AlternateContent\b[^>]*?(/?)>"))
        {
            if (m.Groups[2].Value == "/") continue;
            if (m.Groups[1].Value != "/")
            {
                if (depth == 0) start = m.Index;
                depth++;
            }
            else if (--depth == 0 && start >= 0)
            {
                var end = m.Index + m.Length;
                if (s.IndexOf(needle, start, end - start, StringComparison.Ordinal) >= 0)
                {
                    sb.Append(s, pos, start - pos);
                    pos = end;
                }
                start = -1;
            }
        }
        sb.Append(s, pos, s.Length - pos);
        return sb.ToString();
    }

    private static string CleanRels(string relsName, string xml, HashSet<string> names)
    {
        // "xl/worksheets/_rels/sheet1.xml.rels" -> source part "xl/worksheets/sheet1.xml"
        var dir = Path.GetDirectoryName(relsName)?.Replace('\\', '/') ?? "";
        var baseDir = dir.EndsWith("_rels") ? dir[..^5].TrimEnd('/') : dir;
        return Regex.Replace(xml, @"<Relationship\b[^>]*/>", m =>
        {
            var r = m.Value;
            if (r.Contains("TargetMode=\"External\"")) return r;
            var t = Regex.Match(r, "Target=\"([^\"]+)\"").Groups[1].Value;
            var full = t.StartsWith('/') ? t.TrimStart('/') : Normalize(baseDir.Length > 0 ? baseDir + "/" + t : t);
            return names.Contains(full) ? r : "";
        });
    }

    private static string Normalize(string p)
    {
        var parts = new List<string>();
        foreach (var s in p.Split('/'))
        {
            if (s == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
            else if (s != "." && s.Length > 0) parts.Add(s);
        }
        return string.Join('/', parts);
    }
}
