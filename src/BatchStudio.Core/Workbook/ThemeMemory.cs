using System.Globalization;
using BatchStudio.Core.Data;

namespace BatchStudio.Core.Workbook;

/// <summary>
/// Client Theme Memory of the workbook (Theme.bas): the VeryHidden sheet "CLIENT THEME MEMORY" holds
/// Supplier | Template | RGB, one row per supplier + template (INVOICE or INV1). RGB is the VBA colour number
/// (red + green*256 + blue*65536).
/// </summary>
public static class ThemeMemory
{
    public const string Sheet = "CLIENT THEME MEMORY";

    public static bool Supports(string? template) =>
        string.Equals(template?.Trim(), "INVOICE", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(template?.Trim(), "INV1", StringComparison.OrdinalIgnoreCase);

    private static string Key(string supplier, string template) =>
        supplier.Trim().ToUpperInvariant() + "|" + template.Trim().ToUpperInvariant();

    /// <summary>All assignments of the sheet copy (empty when the workbook has none).</summary>
    public static Dictionary<string, int> Read(string? path)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        if (path == null || !File.Exists(path)) return map;
        try
        {
            using var wb = PackageRepair.OpenWorkbook(path);
            var ws = wb.Worksheet(1);
            var src = new XLSource(ws);
            int last = Math.Max(src.LastRow(1), src.LastRow(3));
            for (int r = 2; r <= last; r++)
            {
                var s = src.Text(r, 1).Trim();
                var t = src.Text(r, 2).Trim();
                if (s.Length == 0 || t.Length == 0) continue;
                if (double.TryParse(src.Text(r, 3), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0 && v <= 0xFFFFFF)
                    map[Key(s, t)] = (int)v;
            }
        }
        catch { }
        return map;
    }

    public static int? Find(Dictionary<string, int> map, string supplier, string template) =>
        map.TryGetValue(Key(supplier, template), out var v) ? v : null;

    /// <summary>Saves (or with null removes) the colour of a supplier + template, updating its row instead of adding a duplicate.</summary>
    public static void Assign(IWorkbookEditor ed, string supplier, string template, int? rgb)
    {
        if (!ed.HasSheet(Sheet))
            throw new InvalidOperationException("The workbook has no CLIENT THEME MEMORY sheet. Run ASSIGN THEME once in Excel to create it.");
        var sh = ed.Sheet(Sheet);
        var src = BlockSource.ReadAll(sh, 3);
        int last = Math.Max(1, Math.Max(src.LastRow(1), src.LastRow(3)));
        int found = 0;
        for (int r = 2; r <= last; r++)
            if (string.Equals(src.Text(r, 1).Trim(), supplier.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(src.Text(r, 2).Trim(), template.Trim(), StringComparison.OrdinalIgnoreCase)) { found = r; break; }
        if (rgb is not { } color)
        {
            if (found > 0) sh.DeleteRow(found);
            return;
        }
        if (found == 0)
        {
            found = last + 1;
            sh.Set(found, 1, supplier.Trim());
            sh.Set(found, 2, template.Trim().ToUpperInvariant() == "INV1" ? "INV1" : "INVOICE");
        }
        sh.Set(found, 3, (double)color);
    }

    // ---------------------------------------------------------------- colours
    public static string ToHex(int rgb) => $"#{rgb & 0xFF:X2}{(rgb >> 8) & 0xFF:X2}{(rgb >> 16) & 0xFF:X2}";

    public static bool TryParseHex(string? text, out int rgb)
    {
        rgb = 0;
        var t = (text ?? "").Trim().TrimStart('#');
        if (t.Length == 3) t = string.Concat(t.Select(c => $"{c}{c}"));
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        int r = (v >> 16) & 0xFF, g = (v >> 8) & 0xFF, b = v & 0xFF;
        rgb = r + g * 256 + b * 65536;
        return true;
    }

    public static (byte R, byte G, byte B) Split(int rgb) => ((byte)(rgb & 0xFF), (byte)((rgb >> 8) & 0xFF), (byte)((rgb >> 16) & 0xFF));
}
