using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class CompanyReportView : UserControl
{
    public CompanyReportView() => InitializeComponent();

    /// <summary>The list scrolls with the page (the total sits under it): the wheel goes to the outer scroll.</summary>
    private void OnGridWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not UIElement grid) return;
        e.Handled = true;
        var parent = System.Windows.Media.VisualTreeHelper.GetParent(grid) as UIElement;
        parent?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = grid });
    }

    /// <summary>Actions list: the chosen action for that row (the list goes back to "Actions").</summary>
    private void OnActionChosen(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box || box.SelectedItem is not ComboBoxItem { Tag: string action }) return;
        var line = box.DataContext;
        box.SelectedIndex = -1;
        Run(action, line);
    }

    /// <summary>Right-click: the row under the mouse is selected first (the menu acts on it).</summary>
    private void OnRowRightClick(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d != null && d is not DataGridRow) d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        if (d is DataGridRow row) { InvoiceGrid.SelectedItem = row.Item; row.IsSelected = true; }
    }

    private void OnMenuAction(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string action } && InvoiceGrid.SelectedItem is { } line) Run(action, line);
    }

    private void Run(string action, object? line)
    {
        if (DataContext is not CompanyReportViewModel vm || line == null) return;
        var cmd = action == "open" ? vm.OpenInvoiceCommand : (ICommand)vm.InvoicePdfCommand;
        if (cmd.CanExecute(line)) cmd.Execute(line);
    }

    /// <summary>Column titles centred (the Fluent header style kept).</summary>
    private void OnCenterHeaderLoaded(object sender, RoutedEventArgs e)
    {
        DependencyObject? d = sender as DependencyObject;
        while (d != null && d is not System.Windows.Controls.Primitives.DataGridColumnHeader) d = VisualTreeHelper.GetParent(d);
        if (d is System.Windows.Controls.Primitives.DataGridColumnHeader h) h.HorizontalContentAlignment = HorizontalAlignment.Center;
    }
}
