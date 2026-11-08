using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    /// <summary>A command picked in a row's Actions list runs at once (like the V28 sheet), then the list resets.</summary>
    private void OnActionChosen(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box || box.SelectedItem is not ComboBoxItem { Tag: string action }) return;
        var row = box.DataContext as HistoryRow;
        box.SelectedIndex = -1;
        if (row != null && DataContext is HistoryViewModel vm) vm.RunActionCommand.Execute(new object[] { row, action });
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<ComboBox>(d) != null) return;
        if (DataContext is HistoryViewModel vm && vm.Selected != null) vm.OpenInvoiceCommand.Execute(vm.Selected);
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
