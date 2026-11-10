using System.Windows;
using System.Windows.Controls;

namespace BatchStudio.App.Services;

/// <summary>Lists (Suppliers, Clients, Reports): the clicked cell shows no frame of its own, the whole row is the selection.</summary>
public static class GridLook
{
    public static void NoCellFrame(DataGrid grid)
    {
        void Apply()
        {
            if (grid.Resources.Contains(typeof(DataGridCell))) return;
            // keep the theme's cell look (colours, selection) and drop only the border / focus frame
            var style = new Style(typeof(DataGridCell), grid.TryFindResource(typeof(DataGridCell)) as Style);
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
            grid.Resources[typeof(DataGridCell)] = style;
        }
        if (grid.IsLoaded) Apply(); else grid.Loaded += (_, _) => Apply();
    }
}
