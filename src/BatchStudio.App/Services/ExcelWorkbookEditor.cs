using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.RegularExpressions;
using BatchStudio.Core.Data;
using BatchStudio.Core.Workbook;

namespace BatchStudio.App.Services;

/// <summary>Excel is busy (a cell is being edited, a dialog is open) or the workbook is open read-only.</summary>
public sealed class ExcelBusyException(string message, Exception? inner = null) : InvalidOperationException(message, inner);

/// <summary>
/// Writes into the BATCH STUDIO workbook through Microsoft Excel, so the file is saved by Excel itself and keeps
/// its VBA, buttons and everything else exactly as before:
/// <list type="bullet">
/// <item>workbook open in Excel → the changes go into that open workbook (events off while writing) and it is saved;</item>
/// <item>otherwise → a hidden Excel opens it with macros disabled, writes, saves and closes.</item>
/// </list>
/// </summary>
public sealed class ExcelWorkbookEditor : IWorkbookEditor
{
    private readonly dynamic _app;
    private readonly dynamic _wb;
    private readonly bool _owned;
    private int? _pid;
    private readonly bool _events, _screen;
    private bool _saved;

    private ExcelWorkbookEditor(dynamic app, dynamic wb, bool owned)
    {
        _app = app;
        _wb = wb;
        _owned = owned;
        if (!owned)
        {
            _events = Retry(() => (bool)_app.EnableEvents);
            _screen = Retry(() => (bool)_app.ScreenUpdating);
            Retry(() => { _app.EnableEvents = false; _app.ScreenUpdating = false; });
        }
    }

    public static IWorkbookEditor Open(string path)
    {
        var full = Path.GetFullPath(path);
        var running = FindOpenWorkbook(full);
        if (running != null)
        {
            if (Retry(() => (bool)running.ReadOnly))
                throw new ExcelBusyException(Loc.T("Excel.ReadOnly"));
            return new ExcelWorkbookEditor(Retry(() => running.Application), running, owned: false);
        }
        if (ExcelTableStore.IsLocked(full)) throw new FileLockedException(full, new IOException("locked"));

        var type = Type.GetTypeFromProgID("Excel.Application")
                   ?? throw new InvalidOperationException(Loc.T("Excel.Missing"));
        dynamic app = Activator.CreateInstance(type)!;
        int? pid = ExcelProcess.Id(app);
        try
        {
            app.Visible = false;
            app.DisplayAlerts = false;
            app.ScreenUpdating = false;
            app.EnableEvents = false;
            app.AskToUpdateLinks = false;
            app.AutomationSecurity = 3;           // msoAutomationSecurityForceDisable: no macros run
            dynamic wb = app.Workbooks.Open(full, 0, false);   // UpdateLinks:=0, ReadOnly:=False
            return new ExcelWorkbookEditor(app, wb, owned: true) { _pid = pid };
        }
        catch
        {
            ExcelProcess.Shutdown(app, pid);
            throw;
        }
    }

    public bool HasSheet(string name)
    {
        foreach (dynamic ws in Retry(() => _wb.Worksheets))
            if (string.Equals((string)ws.Name, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public ISheetEditor Sheet(string name)
    {
        foreach (dynamic ws in Retry(() => _wb.Worksheets))
            if (string.Equals((string)ws.Name, name, StringComparison.OrdinalIgnoreCase)) return new ComSheet(ws);
        throw new InvalidOperationException($"Sheet \"{name}\" was not found in the workbook.");
    }

    /// <summary>Exports one sheet to PDF with its own print setup (like the sheet's Export PDF button).</summary>
    public void ExportSheetPdf(string sheetName, string pdfPath)
    {
        foreach (dynamic ws in Retry(() => _wb.Worksheets))
            if (string.Equals((string)ws.Name, sheetName, StringComparison.OrdinalIgnoreCase))
            {
                Retry(() => { _app.Calculate(); });
                using (PdfPrinter.Use((object)_app))                    // Arabic text keeps its colour in the PDF
                    Retry(() => { ws.ExportAsFixedFormat(0, pdfPath); });   // 0 = xlTypePDF
                return;
            }
        throw new InvalidOperationException($"Sheet \"{sheetName}\" was not found in the workbook.");
    }

    public void Save()
    {
        Retry(() => { _wb.Save(); });
        _saved = true;
    }

    public void Dispose()
    {
        if (_owned)
        {
            try { _wb.Close(false); } catch { }
            try { Marshal.FinalReleaseComObject(_wb); } catch { }
            ExcelProcess.Shutdown(_app, _pid);
        }
        else
        {
            try { Retry(() => { _app.EnableEvents = _events; _app.ScreenUpdating = _screen; }); } catch { }
        }
        _ = _saved;
    }

    // ------------------------------------------------------------------ busy handling
    private static readonly int[] BusyCodes =
    [
        unchecked((int)0x80010001),   // RPC_E_CALL_REJECTED
        unchecked((int)0x8001010A),   // RPC_E_SERVERCALL_RETRYLATER
        unchecked((int)0x800AC472),   // VBA_E_IGNORE (Excel in edit mode)
    ];

    internal static T Retry<T>(Func<T> f)
    {
        for (int i = 0; ; i++)
        {
            try { return f(); }
            catch (COMException ex) when (BusyCodes.Contains(ex.HResult))
            {
                if (i >= 40) throw new ExcelBusyException(Loc.T("Excel.Busy"), ex);
                Thread.Sleep(250);
            }
        }
    }

    internal static void Retry(Action a) => Retry(() => { a(); return true; });

    // ------------------------------------------------------------------ find the workbook if Excel has it open
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx ctx);

    internal static dynamic? FindOpenWorkbook(string fullPath)
    {
        if (GetRunningObjectTable(0, out var rot) != 0 || rot == null) return null;
        if (CreateBindCtx(0, out var ctx) != 0) return null;
        var fileName = Path.GetFileName(fullPath);
        dynamic? byName = null;
        rot.EnumRunning(out var e);
        var arr = new IMoniker[1];
        while (e.Next(1, arr, IntPtr.Zero) == 0)
        {
            try
            {
                arr[0].GetDisplayName(ctx, null, out var display);
                if (string.IsNullOrEmpty(display)) continue;
                bool exact = string.Equals(Normalize(display), Normalize(fullPath), StringComparison.OrdinalIgnoreCase);
                // OneDrive / SharePoint workbooks are registered under their https:// address
                bool sameName = !exact && Uri.UnescapeDataString(display.Replace('/', '\\')).EndsWith("\\" + fileName, StringComparison.OrdinalIgnoreCase);
                if (!exact && !sameName) continue;
                rot.GetObject(arr[0], out var obj);
                dynamic wb = obj;
                string name;
                try { name = Retry(() => (string)wb.Name); } catch { continue; }
                if (!string.Equals(name, fileName, StringComparison.OrdinalIgnoreCase)) continue;
                if (exact) return wb;
                byName ??= wb;
            }
            catch (COMException) { }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        }
        return byName;
    }

    private static string Normalize(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\'); } catch { return p; }
    }

    // ------------------------------------------------------------------ one sheet
    private sealed class ComSheet(dynamic ws) : ISheetEditor
    {
        public string Name => Retry(() => (string)ws.Name);

        public int LastUsedRow => Retry(() => { dynamic u = ws.UsedRange; return (int)u.Row + (int)u.Rows.Count - 1; });
        public int LastUsedColumn => Retry(() => { dynamic u = ws.UsedRange; return (int)u.Column + (int)u.Columns.Count - 1; });

        private dynamic Range(int r1, int c1, int r2, int c2) => ws.Range[ws.Cells[r1, c1], ws.Cells[r2, c2]];

        public object?[,] Read(int r1, int c1, int r2, int c2)
        {
            object? v = Retry(() => (object?)Range(r1, c1, r2, c2).Value);
            var a = new object?[r2 - r1 + 1, c2 - c1 + 1];
            if (v is object[,] arr)
            {
                int lb0 = arr.GetLowerBound(0), lb1 = arr.GetLowerBound(1);
                for (int i = 0; i < a.GetLength(0); i++)
                    for (int j = 0; j < a.GetLength(1); j++)
                        a[i, j] = Clean(arr[lb0 + i, lb1 + j]);
            }
            else a[0, 0] = Clean(v);
            return a;
        }

        private static object? Clean(object? v) => v switch
        {
            int i when i < -2146826200 => null,   // #N/A, #VALUE! ... come back as error ints
            _ => v,
        };

        public void Write(int r1, int c1, object?[,] values)
        {
            int rows = values.GetLength(0), cols = values.GetLength(1);
            if (rows == 0 || cols == 0) return;
            var a = new object?[rows, cols];
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                    a[i, j] = ToCom(values[i, j]);
            Retry(() => { Range(r1, c1, r1 + rows - 1, c1 + cols - 1).Value = a; });
        }

        public void Set(int r, int c, object? value) => Retry(() => { ws.Cells[r, c].Value = ToCom(value); });

        /// <summary>Text that Excel would turn into a number, date or formula is written with a leading apostrophe.</summary>
        private static object? ToCom(object? v) => v switch
        {
            null => null,
            string s when s.Length == 0 => null,
            string s when NeedsQuote(s) => "'" + s,
            decimal m => (double)m,
            _ => v,
        };

        private static bool NeedsQuote(string s)
        {
            var t = s.Trim();
            if (t.Length == 0) return false;
            if (t[0] is '=' or '+' or '-' or '@' or '\'') return true;
            if (double.TryParse(t, NumberStyles.Any, CultureInfo.CurrentCulture, out _)) return true;
            if (double.TryParse(t, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) return true;
            if (Regex.IsMatch(t, @"^\d{1,4}[-/.]\d{1,2}([-/.]\d{1,4})?$")) return true;   // looks like a date
            if (Regex.IsMatch(t, @"^\d+%$|^\d{1,2}:\d{2}")) return true;
            return t.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || t.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
        }

        public void SetNumberFormat(int r, int c, string format) => Retry(() => { ws.Cells[r, c].NumberFormat = format; });

        public void SetFontColor(int r, int c, int red, int green, int blue) =>
            Retry(() => { ws.Cells[r, c].Font.Color = red + green * 256 + blue * 65536; });

        public void CopyRow(int fromRow, int toRow, int c1, int c2) => Retry(() =>
        {
            Range(fromRow, c1, fromRow, c2).Copy(ws.Cells[toRow, c1]);
            ws.Rows[toRow].RowHeight = ws.Rows[fromRow].RowHeight;
        });

        public void CopyColumnFormat(int fromCol, int toCol) => Retry(() =>
        {
            // copy the whole column (formats, width), then drop the copied values
            ws.Columns[fromCol].Copy(ws.Columns[toCol]);
            ws.Columns[toCol].ClearContents();
            ws.Columns[toCol].ColumnWidth = ws.Columns[fromCol].ColumnWidth;
        });

        public void DeleteRow(int r) => Retry(() => { ws.Rows[r].Delete(); });
        public void ShowRow(int r) => Retry(() => { ws.Rows[r].Hidden = false; });

        public (int R1, int C1, int R2, int C2)? GetPrintArea()
        {
            string pa = Retry(() => (string)(ws.PageSetup.PrintArea ?? ""));
            var m = Regex.Match(pa.Split(',')[0], @"\$?([A-Z]+)\$?(\d+):\$?([A-Z]+)\$?(\d+)");
            if (!m.Success) return null;
            return (int.Parse(m.Groups[2].Value), Col(m.Groups[1].Value), int.Parse(m.Groups[4].Value), Col(m.Groups[3].Value));
        }

        public void SetPrintArea(int r1, int c1, int r2, int c2) =>
            Retry(() => { ws.PageSetup.PrintArea = $"${Letters(c1)}${r1}:${Letters(c2)}${r2}"; });

        private static int Col(string letters) => letters.Aggregate(0, (n, ch) => n * 26 + (ch - 'A' + 1));

        private static string Letters(int c)
        {
            var s = "";
            while (c > 0) { int m = (c - 1) % 26; s = (char)('A' + m) + s; c = (c - 1) / 26; }
            return s;
        }
    }
}
