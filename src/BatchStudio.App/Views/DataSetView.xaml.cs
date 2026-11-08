using System.Data;
using System.Windows;
using System.Windows.Controls;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class DataSetView : UserControl
{
    public DataSetView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ApplyLayout();
    }

    /// <summary>Suppliers / Clients: a narrow ID + name list and a wide details pane.</summary>
    private void ApplyLayout()
    {
        bool compact = DataContext is DataSetViewModel { CompactGrid: true };
        ListColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
        ListColumn.MaxWidth = compact ? 460 : double.PositiveInfinity;
        ListColumn.MinWidth = compact ? 300 : 0;
        DetailsColumn.Width = compact ? new GridLength(2, GridUnitType.Star) : new GridLength(360);
    }

    /// <summary>Columns are named c0, c1… (safe binding paths); show the real Excel header instead.</summary>
    private void OnAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        bool star = false;
        if (DataContext is DataSetViewModel vm && vm.Table?.Columns[e.PropertyName] is DataColumn col)
        {
            e.Column.Header = col.Caption;
            if (vm.CompactGrid)
            {
                int index = vm.Table.Columns.IndexOf(col);
                if (index > 1) { e.Cancel = true; return; }
                star = index == 1;
            }
        }
        if (star) e.Column.Width = new DataGridLength(1, DataGridLengthUnitType.Star);
        else e.Column.MaxWidth = 380;
        e.Column.MinWidth = 70;
        if (e.Column is DataGridTextColumn text)
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(6, 0, 6, 0)));
            text.ElementStyle = style;
        }
    }
}
