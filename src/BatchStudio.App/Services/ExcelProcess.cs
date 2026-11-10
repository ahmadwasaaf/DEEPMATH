using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BatchStudio.App.Services;

/// <summary>
/// The hidden Excel the program starts for its own work must end with that work. Otherwise Windows may open the user's
/// next double-clicked workbook inside it - an Excel whose screen updating, events and macros were switched off
/// (the grid stays blank). This closes it for sure, and leaves it running only if the user is working in it.
/// </summary>
public static class ExcelProcess
{
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    /// <summary>Process id of an Excel started by the program (read right after it was created).</summary>
    public static int? Id(dynamic app)
    {
        try
        {
            int hwnd = (int)app.Hwnd;
            GetWindowThreadProcessId(new IntPtr(hwnd), out var pid);
            return pid == 0 ? null : (int)pid;
        }
        catch { return null; }
    }

    /// <summary>Quits an Excel the program started (after its own workbooks were closed) and makes sure the process is gone.</summary>
    public static void Shutdown(dynamic? app, int? pid)
    {
        if (app == null) return;
        bool userIsUsingIt = false;
        try
        {
            // the user opened a file in it meanwhile: give it back as a normal Excel instead of closing their work
            userIsUsingIt = (bool)app.Visible || (bool)app.UserControl;
        }
        catch { }
        try { app.ScreenUpdating = true; } catch { }
        try { app.EnableEvents = true; } catch { }
        try { app.DisplayAlerts = true; } catch { }
        try { app.AutomationSecurity = 2; } catch { }   // msoAutomationSecurityByUI: the user's own macro setting
        if (!userIsUsingIt)
        {
            try { app.Quit(); } catch { }
        }
        try { Marshal.FinalReleaseComObject(app); } catch { }
        // release the COM wrappers still held for Excel objects, so the process can end
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        if (userIsUsingIt || pid is not { } id) return;
        try
        {
            using var p = Process.GetProcessById(id);
            if (!p.HasExited && !p.WaitForExit(4000) && p.ProcessName.Contains("EXCEL", StringComparison.OrdinalIgnoreCase)) p.Kill();
        }
        catch { }
    }
}
