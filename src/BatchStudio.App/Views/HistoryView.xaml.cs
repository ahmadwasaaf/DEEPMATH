using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class HistoryView : UserControl
{
    private HistoryViewModel? _vm;

    public HistoryView()
    {
        InitializeComponent();
        // the newest invoices matter most: the list always opens on the last one
        Loaded += (_, _) => { Hook(DataContext as HistoryViewModel); ScrollToEnd(); };
        Unloaded += (_, _) => Hook(null);
        DataContextChanged += (_, e) => { if (IsLoaded) { Hook(e.NewValue as HistoryViewModel); ScrollToEnd(); } };
    }

    private void Hook(HistoryViewModel? vm)
    {
        if (_vm != null) _vm.ScrollToEndRequested -= ScrollToEnd;
        _vm = vm;
        if (_vm != null) _vm.ScrollToEndRequested += ScrollToEnd;
    }

    private void ScrollToEnd()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (HistoryGrid.Items.Count == 0) return;
            HistoryGrid.UpdateLayout();
            HistoryGrid.ScrollIntoView(HistoryGrid.Items[HistoryGrid.Items.Count - 1]);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

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
