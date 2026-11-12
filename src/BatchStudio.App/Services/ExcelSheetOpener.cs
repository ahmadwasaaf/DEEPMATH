using System.Runtime.InteropServices;
using System.Windows;

namespace BatchStudio.App.Services;

/// <summary>
/// Templates page › double-click: opens the template's sheet in Excel so its design can be changed - in the workbook
/// already open in Excel, or opened the normal way (as a double-click in Explorer does), then that sheet is shown.
/// </summary>
public static class ExcelSheetOpener
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int cmd);

    public static void Open(string workbook, string sheet)
    {
        if (!File.Exists(workbook)) { Dialogs.Error(Loc.F("Tpl.OpenMissing", workbook)); return; }
        if (Show(workbook, sheet)) return;
        Shell.Open(workbook);
        // Excel opens the file in its own time: the sheet is shown as soon as the workbook appears
        var thread = new Thread(() =>
        {
            for (int i = 0; i < 90; i++)
            {
                Thread.Sleep(500);
                try { if (Show(workbook, sheet)) return; }
                catch { }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>The workbook is open in Excel: its sheet is activated and Excel comes to the front.</summary>
    private static bool Show(string workbook, string sheet)
    {
        try
        {
            if (ExcelWorkbookEditor.FindOpenWorkbook(workbook) is not { } wb) return false;
            ExcelWorkbookEditor.Retry(() =>
            {
                dynamic app = wb.Application;
                app.Visible = true;
                wb.Activate();
                dynamic ws = wb.Worksheets[sheet];
                ws.Activate();
                try
                {
                    var hwnd = new IntPtr((long)app.Hwnd);
                    ShowWindow(hwnd, 9);   // restore if minimised
                    SetForegroundWindow(hwnd);
                }
                catch { }
            });
            return true;
        }
        catch (COMException) { return false; }
        catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return false; }
    }
}
