using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BatchStudio.App.Services;

/// <summary>
/// Excel lays out a PDF with its active printer. With some printers, text in Arabic (and other complex scripts) loses its
/// colour in the PDF - white words on a coloured band come out black - while Excel itself shows them right. With
/// "Microsoft Print to PDF" they keep it. So every PDF the program exports uses that printer in its own Excel, and the
/// Windows default printer is left as the user set it.
/// </summary>
public static class PdfPrinter
{
    public const string Name = "Microsoft Print to PDF";

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetDefaultPrinter(StringBuilder? buffer, ref int size);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDefaultPrinter(string name);

    private static string? DefaultPrinter()
    {
        try
        {
            int size = 0;
            GetDefaultPrinter(null, ref size);
            if (size <= 0) return null;
            var sb = new StringBuilder(size);
            return GetDefaultPrinter(sb, ref size) ? sb.ToString() : null;
        }
        catch { return null; }
    }

    /// <summary>The printer's port ("Ne01:") as Windows lists it, null when it is not installed.</summary>
    private static string? Port()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Devices");
            var port = (key?.GetValue(Name) as string)?.Split(',').LastOrDefault()?.Trim();
            return string.IsNullOrEmpty(port) ? null : port;
        }
        catch { return null; }
    }

    /// <summary>
    /// Makes "Microsoft Print to PDF" the active printer of this Excel; disposing gives Excel its printer back and
    /// restores the Windows default printer if Excel changed it. Nothing happens when that printer is missing.
    /// </summary>
    public static IDisposable Use(object excel)
    {
        dynamic app = excel;
        string? before = null;
        var windowsDefault = DefaultPrinter();
        try { before = (string)app.ActivePrinter; } catch { }
        bool set = false;
        var port = Port();
        if (port != null && !(before ?? "").StartsWith(Name, StringComparison.OrdinalIgnoreCase))
        {
            // "HP LaserJet on Ne02:" - the word between name and port is in Office's language ("on", "auf", "sur" ...)
            var m = Regex.Match(before ?? "", @"^.+\s(\S+)\s\S+:$");
            foreach (var word in new[] { m.Success ? m.Groups[1].Value : "on", "on" }.Distinct())
            {
                try { app.ActivePrinter = $"{Name} {word} {port}"; set = true; break; } catch { }
            }
        }
        return new Restore(() =>
        {
            if (set && before != null) { try { app.ActivePrinter = before; } catch { } }
            if (windowsDefault != null && DefaultPrinter() is { } now && !string.Equals(now, windowsDefault, StringComparison.OrdinalIgnoreCase))
            {
                try { SetDefaultPrinter(windowsDefault); } catch { }
            }
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        private Action? _undo = undo;
        public void Dispose() { var u = _undo; _undo = null; u?.Invoke(); }
    }
}
