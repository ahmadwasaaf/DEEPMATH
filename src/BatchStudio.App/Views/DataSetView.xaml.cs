using System.Data;
using System.Windows;
using System.Windows.Controls;
using BatchStudio.App.Services;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class DataSetView : UserControl
{
    public DataSetView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ApplyLayout();
        ListWidth.Attach(ListSplitter, ListColumn, () => DataContext is DataSetViewModel { CompactGrid: true } vm ? "list." + vm.Session.Definition.Key : null, DefaultListWidth);
        // the search box keeps its width but never overlaps the toolbar buttons
        SearchHost.SizeChanged += (_, _) => SearchBox.Width = Math.Max(0, Math.Min(300, SearchHost.ActualWidth));
    }

    private void OnThemeLostFocus(object sender, System.Windows.RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThemeFieldViewModel t) t.CommitCommand.Execute(null);
    }

    private void OnThemeKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && (sender as FrameworkElement)?.DataContext is ThemeFieldViewModel t) t.CommitCommand.Execute(null);
    }

    private const double DefaultListWidth = 330;

    /// <summary>Suppliers / Clients: an ID + name list (its width dragged by the user) and a wide details pane.</summary>
    private void ApplyLayout()
    {
        if (DataContext is DataSetViewModel { CompactGrid: true } vm)
        {
            ListColumn.Width = new GridLength(ListWidth.Get("list." + vm.Session.Definition.Key, DefaultListWidth));
            ListColumn.MinWidth = 220;
            DetailsColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailsColumn.MinWidth = 320;
        }
        else
        {
            ListColumn.Width = new GridLength(1, GridUnitType.Star);
            ListColumn.MinWidth = 200;
            DetailsColumn.Width = new GridLength(360);
            DetailsColumn.MinWidth = 260;
        }
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
                // suppliers: the name with the number of bank accounts next to it
                if (index == 1 && vm.Session.Definition.Key == "suppliers")
                {
                    e.Column = new DataGridTemplateColumn
                    {
                        Header = col.Caption,
                        CellTemplate = (DataTemplate)FindResource("NameWithBadge"),
                        SortMemberPath = e.PropertyName,
                        Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                        MinWidth = 70,
                        IsReadOnly = true,
                    };
                    return;
                }
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
