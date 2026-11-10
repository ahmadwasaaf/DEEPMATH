using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Services;

/// <summary>
/// The name list next to a details pane can be widened by dragging its right edge (a GridSplitter);
/// the width is remembered per page in the settings.
/// </summary>
public static class ListWidth
{
    private const double DetailsMin = 320;

    public static double Get(string key, double fallback)
    {
        var w = MainViewModel.Current?.Settings.ListWidths;
        return w != null && w.TryGetValue(key, out var v) && v >= 120 && v < 5000 ? v : fallback;
    }

    /// <summary>Gives <paramref name="column"/> its remembered width and saves it whenever the splitter is released.</summary>
    public static void Attach(GridSplitter splitter, ColumnDefinition column, Func<string?> key, double fallback)
    {
        void Apply()
        {
            if (key() is { } k) column.Width = new GridLength(Get(k, fallback));
        }
        Apply();
        // a width saved on a larger window never pushes the details pane out of view
        if (splitter.Parent is Grid grid)
            grid.SizeChanged += (_, _) => column.MaxWidth = Math.Max(column.MinWidth, grid.ActualWidth - splitter.Width - DetailsMin);
        splitter.DragCompleted += (_, _) =>
        {
            if (key() is not { } k || MainViewModel.Current is not { } main) return;
            main.Settings.ListWidths[k] = Math.Round(column.ActualWidth);
            main.SaveSettings();
        };
        // double-click on the edge: back to the default width
        splitter.MouseDoubleClick += (_, _) =>
        {
            if (key() is not { } k || MainViewModel.Current is not { } main) return;
            main.Settings.ListWidths.Remove(k);
            main.SaveSettings();
            column.Width = new GridLength(fallback);
        };
        splitter.Loaded += (_, _) => Apply();
    }
}
