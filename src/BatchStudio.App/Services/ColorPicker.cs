using System.Windows;
using System.Windows.Media;
using BatchStudio.App.Views;

namespace BatchStudio.App.Services;

/// <summary>The program's colour picker window (HEX, R/G/B, saturation square, hue bar, palette, recent colours).</summary>
public static class ColorPicker
{
    /// <summary>Returns the picked colour as a VBA colour number (r + g*256 + b*65536), or null when cancelled.</summary>
    public static int? Pick(int? initial)
    {
        Color? start = initial is { } v ? Color.FromRgb((byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF)) : null;
        var w = new ColorPickerWindow(start);
        if (Application.Current?.MainWindow is { IsLoaded: true } owner && owner != w) w.Owner = owner;
        else w.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        if (w.ShowDialog() != true) return null;
        var c = w.Selected;
        return c.R + c.G * 256 + c.B * 65536;
    }
}
