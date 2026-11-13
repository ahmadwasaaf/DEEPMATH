using System.Windows.Controls;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();

    /// <summary>Column titles centred (the Fluent header style kept).</summary>
    private void OnCenterHeaderLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        System.Windows.DependencyObject? d = sender as System.Windows.DependencyObject;
        while (d != null && d is not System.Windows.Controls.Primitives.DataGridColumnHeader) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        if (d is System.Windows.Controls.Primitives.DataGridColumnHeader h) h.HorizontalContentAlignment = System.Windows.HorizontalAlignment.Center;
    }

    /// <summary>A command picked in a row's Actions list runs at once (like INVOICE HISTORY), then the list resets.</summary>
    private void OnActionChosen(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox box || box.SelectedItem is not ComboBoxItem { Tag: string action }) return;
        var row = box.DataContext;
        box.SelectedIndex = -1;
        if (row == null || DataContext is not DashboardViewModel vm) return;
        var cmd = action == "report" ? vm.ViewReportCommand : (System.Windows.Input.ICommand)vm.ExportReportCommand;
        if (cmd.CanExecute(row)) cmd.Execute(row);
    }
}
