using System.Data;
using System.Windows;
using System.Windows.Controls;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class DataSetView : UserControl
{
    public DataSetView() => InitializeComponent();

    /// <summary>Columns are named c0, c1… (safe binding paths); show the real Excel header instead.</summary>
    private void OnAutoGeneratingColumn(object? sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (DataContext is DataSetViewModel vm && vm.Table?.Columns[e.PropertyName] is DataColumn col)
            e.Column.Header = col.Caption;
        e.Column.MaxWidth = 380;
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
