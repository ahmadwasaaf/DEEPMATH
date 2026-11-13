using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
}
