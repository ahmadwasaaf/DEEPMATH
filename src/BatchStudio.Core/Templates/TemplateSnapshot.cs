using System.Text.RegularExpressions;
using BatchStudio.Core.Data;
using BatchStudio.Core.Workbook;
using ClosedXML.Excel;

namespace BatchStudio.Core.Templates;

/// <summary>One border side: width in device-independent pixels and ARGB colour.</summary>
public sealed record SnapEdge(double Width, uint Color);

/// <summary>A cell (or a merged block) of a template, as it looks: text, fill, font, alignment, borders.</summary>
public sealed record SnapCell(
    int Row, int Col, int RowSpan, int ColSpan, string Text,
    uint? Fill, uint FontColor, string FontName, double FontSize, bool Bold, bool Italic,
    string Horizontal, string Vertical, bool Wrap, int Indent,
    SnapEdge? Left, SnapEdge? Top, SnapEdge? Right, SnapEdge? Bottom);

/// <summary>A picture placed on the sheet (logo, stamp): image bytes and its box in device-independent pixels.</summary>
public sealed record SnapPicture(byte[] Data, double X, double Y, double Width, double Height);

/// <summary>
/// What a template sheet looks like, read once with ClosedXML (no Excel needed): the print area's column widths and
/// row heights (device-independent pixels), its cells and pictures, and the colour the supplier theme replaces.
/// The program draws this as the preview shown while hovering a template in a list.
/// </summary>
public sealed partial class TemplateSnapshot
{
    public required double[] Columns { get; init; }
    public required double[] Rows { get; init; }
    public required List<SnapCell> Cells { get; init; }
    public required List<SnapPicture> Pictures { get; init; }
    /// <summary>The template's own theme colour (ARGB), replaced by the supplier's colour; null = no theme.</summary>
    public uint? ThemeBase { get; init; }
    /// <summary>INVOICE / INV1 keep row 1 as it is (their theme is applied by Theme.bas from row 2).</summary>
    public bool KeepRow1 { get; init; }

    public double Width => Columns.Sum();
    public double Height => Rows.Sum();

    private const int MaxRows = 90, MaxCols = 24;

    [GeneratedRegex(@"^INV\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InvName();

    /// <summary>Reads a template file; <paramref name="sheet"/> = the linked sheet (null = the first one). Null when unreadable.</summary>
    public static TemplateSnapshot? Load(string path, string? sheet, string templateName)
    {
        try
        {
            using var wb = PackageRepair.OpenWorkbook(path);
            var ws = (sheet != null && wb.Worksheets.TryGetWorksheet(sheet, out var named) ? named : null) ?? wb.Worksheets.FirstOrDefault();
            return ws == null ? null : Read(wb, ws, templateName);
        }
        catch (Exception)
        {
            return null;   // a preview is never worth an error message
        }
    }

    private static TemplateSnapshot Read(IXLWorkbook wb, IXLWorksheet ws, string templateName)
    {
        // the print area (or the used range, formats included)
        IXLRangeAddress? area = null;
        try { area = ws.PageSetup.PrintAreas.FirstOrDefault()?.RangeAddress; } catch { }
        area ??= ws.RangeUsed(XLCellsUsedOptions.All)?.RangeAddress;
        int r1 = area?.FirstAddress.RowNumber ?? 1, c1 = area?.FirstAddress.ColumnNumber ?? 1;
        int r2 = Math.Min(area?.LastAddress.RowNumber ?? 40, r1 + MaxRows - 1);
        int c2 = Math.Min(area?.LastAddress.ColumnNumber ?? 8, c1 + MaxCols - 1);

        var cols = new double[c2 - c1 + 1];
        for (int c = c1; c <= c2; c++)
        {
            var col = ws.Column(c);
            double w = col.IsHidden ? 0 : col.Width;
            // Excel: width in characters of the default font (Calibri 11: 7 px per character + 5 px padding)
            cols[c - c1] = w <= 0 ? 0 : Math.Truncate(w * 7 + 5);
        }
        var rows = new double[r2 - r1 + 1];
        for (int r = r1; r <= r2; r++)
        {
            var row = ws.Row(r);
            rows[r - r1] = row.IsHidden ? 0 : row.Height * 96 / 72;
        }

        // merged blocks: the top-left cell carries the block, the others are skipped
        var spans = new Dictionary<(int, int), (int Rows, int Cols)>();
        var covered = new HashSet<(int, int)>();
        foreach (var m in ws.MergedRanges)
        {
            var a = m.RangeAddress;
            int mr1 = a.FirstAddress.RowNumber, mc1 = a.FirstAddress.ColumnNumber, mr2 = a.LastAddress.RowNumber, mc2 = a.LastAddress.ColumnNumber;
            if (mr1 > r2 || mc1 > c2 || mr2 < r1 || mc2 < c1) continue;
            mr1 = Math.Max(mr1, r1); mc1 = Math.Max(mc1, c1); mr2 = Math.Min(mr2, r2); mc2 = Math.Min(mc2, c2);
            spans[(mr1, mc1)] = (mr2 - mr1 + 1, mc2 - mc1 + 1);
            for (int r = mr1; r <= mr2; r++)
                for (int c = mc1; c <= mc2; c++)
                    if (r != mr1 || c != mc1) covered.Add((r, c));
        }

        var cells = new List<SnapCell>();
        for (int r = r1; r <= r2; r++)
            for (int c = c1; c <= c2; c++)
            {
                if (covered.Contains((r, c))) continue;
                var cell = ws.Cell(r, c);
                var (rs, cs) = spans.TryGetValue((r, c), out var sp) ? sp : (1, 1);
                var style = cell.Style;
                uint? fill = style.Fill.PatternType == XLFillPatternValues.Solid ? Argb(wb, style.Fill.BackgroundColor) : null;
                string text = Text(cell);
                var right = rs == 1 && cs == 1 ? style : ws.Cell(r, c + cs - 1).Style;
                var bottom = rs == 1 && cs == 1 ? style : ws.Cell(r + rs - 1, c).Style;
                var left = Edge(wb, style.Border.LeftBorder, style.Border.LeftBorderColor);
                var top = Edge(wb, style.Border.TopBorder, style.Border.TopBorderColor);
                var rightEdge = Edge(wb, right.Border.RightBorder, right.Border.RightBorderColor);
                var bottomEdge = Edge(wb, bottom.Border.BottomBorder, bottom.Border.BottomBorderColor);
                if (fill == null && text.Length == 0 && left == null && top == null && rightEdge == null && bottomEdge == null) continue;

                var horizontal = style.Alignment.Horizontal switch
                {
                    XLAlignmentHorizontalValues.Center or XLAlignmentHorizontalValues.CenterContinuous => "Center",
                    XLAlignmentHorizontalValues.Right => "Right",
                    XLAlignmentHorizontalValues.Left or XLAlignmentHorizontalValues.Justify or XLAlignmentHorizontalValues.Distributed
                        or XLAlignmentHorizontalValues.Fill => "Left",
                    _ => cell.DataType is XLDataType.Number or XLDataType.DateTime or XLDataType.TimeSpan ? "Right" : "Left",
                };
                var vertical = style.Alignment.Vertical switch
                {
                    XLAlignmentVerticalValues.Top => "Top",
                    XLAlignmentVerticalValues.Center or XLAlignmentVerticalValues.Justify or XLAlignmentVerticalValues.Distributed => "Center",
                    _ => "Bottom",
                };
                var font = style.Font;
                cells.Add(new SnapCell(r - r1, c - c1, rs, cs, text, fill, Argb(wb, font.FontColor) ?? 0xFF000000,
                    string.IsNullOrWhiteSpace(font.FontName) ? "Calibri" : font.FontName, font.FontSize > 0 ? font.FontSize : 11,
                    font.Bold, font.Italic, horizontal, vertical, style.Alignment.WrapText, style.Alignment.Indent,
                    left, top, rightEdge, bottomEdge));
            }

        // pictures (logos, stamps)
        var pictures = new List<SnapPicture>();
        try
        {
            double[] colStart = Starts(cols), rowStart = Starts(rows);
            foreach (var pic in ws.Pictures)
            {
                var tl = pic.TopLeftCell;
                if (tl == null) continue;
                int pr = tl.Address.RowNumber, pc = tl.Address.ColumnNumber;
                if (pr < r1 || pr > r2 || pc < c1 || pc > c2) continue;
                // Left / Top: the picture's offset from its top-left cell, in pixels
                using var ms = new MemoryStream();
                pic.ImageStream.Position = 0;
                pic.ImageStream.CopyTo(ms);
                pictures.Add(new SnapPicture(ms.ToArray(), colStart[pc - c1] + pic.Left, rowStart[pr - r1] + pic.Top, pic.Width, pic.Height));
            }
        }
        catch { }

        // the colour the supplier theme replaces (as the Excel engine does)
        bool classic = string.Equals(templateName, "INVOICE", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(templateName, "INV1", StringComparison.OrdinalIgnoreCase);
        uint? theme = null;
        if (ThemeMemory.Supports(templateName))
        {
            if (classic)
                // INVOICE / INV1: the theme colour is kept in CONTROL (not in the template file): the most used strong fill
                theme = cells.Where(x => x.Row > 0 && x.Fill is { } f && Saturation(f) > 0.25)
                    .GroupBy(x => x.Fill!.Value).OrderByDescending(g => g.Sum(x => x.RowSpan * x.ColSpan)).Select(g => (uint?)g.Key).FirstOrDefault();
            else if (InvName().IsMatch(templateName))
            {
                var b1 = ws.Cell("B1").Style.Fill;
                if (b1.PatternType == XLFillPatternValues.Solid) theme = Argb(wb, b1.BackgroundColor);
            }
        }
        return new TemplateSnapshot { Columns = cols, Rows = rows, Cells = cells, Pictures = pictures, ThemeBase = theme, KeepRow1 = classic };
    }

    public static double[] Starts(double[] sizes)
    {
        var s = new double[sizes.Length + 1];
        for (int i = 0; i < sizes.Length; i++) s[i + 1] = s[i] + sizes[i];
        return s;
    }

    private static string Text(IXLCell cell)
    {
        try { return cell.GetFormattedString() ?? ""; }
        catch
        {
            try { return cell.CachedValue.ToString() ?? ""; }
            catch { return ""; }
        }
    }

    private static SnapEdge? Edge(IXLWorkbook wb, XLBorderStyleValues style, XLColor color)
    {
        double w = style switch
        {
            XLBorderStyleValues.None => 0,
            XLBorderStyleValues.Hair => 0.5,
            XLBorderStyleValues.Medium or XLBorderStyleValues.MediumDashed or XLBorderStyleValues.MediumDashDot
                or XLBorderStyleValues.MediumDashDotDot => 2,
            XLBorderStyleValues.Thick or XLBorderStyleValues.Double => 3,
            _ => 1,
        };
        return w <= 0 ? null : new SnapEdge(w, Argb(wb, color) ?? 0xFF000000);
    }

    /// <summary>ARGB of a ClosedXML colour (RGB, indexed or theme colour with tint); null when it has none.</summary>
    public static uint? Argb(IXLWorkbook wb, XLColor? c)
    {
        if (c == null || !c.HasValue) return null;
        try
        {
            if (c.ColorType == XLColorType.Theme)
            {
                var baseColor = wb.Theme.ResolveThemeColor(c.ThemeColor).Color;
                return Tint(Pack(baseColor), c.ThemeTint);
            }
            var d = c.Color;
            if (d.A == 0 && d.R == 0 && d.G == 0 && d.B == 0) return null;
            return Pack(d) | 0xFF000000;
        }
        catch { return null; }
    }

    private static uint Pack(System.Drawing.Color d) => 0xFF000000u | ((uint)d.R << 16) | ((uint)d.G << 8) | d.B;

    /// <summary>Excel tint: -1 (darker) … +1 (lighter).</summary>
    private static uint Tint(uint argb, double tint)
    {
        if (Math.Abs(tint) < 1e-6) return argb;
        static byte T(uint v, double t) => (byte)Math.Round(t < 0 ? v * (1 + t) : v + (255 - v) * t);
        return 0xFF000000u | ((uint)T((argb >> 16) & 0xFF, tint) << 16) | ((uint)T((argb >> 8) & 0xFF, tint) << 8) | T(argb & 0xFF, tint);
    }

    private static double Saturation(uint argb)
    {
        double r = ((argb >> 16) & 0xFF) / 255.0, g = ((argb >> 8) & 0xFF) / 255.0, b = (argb & 0xFF) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        return max <= 0 ? 0 : (max - min) / max;
    }
}
