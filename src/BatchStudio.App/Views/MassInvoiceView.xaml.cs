using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class MassInvoiceView : UserControl
{
    public MassInvoiceView() => InitializeComponent();

    private void OnAmountLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MassRowViewModel row }) row.FormatAmount();
    }

    private void OnInvoiceNoLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MassRowViewModel row }) row.CommitInvoiceNo();
    }

    private void OnCenterHeaderLoaded(object sender, RoutedEventArgs e)
    {
        DependencyObject? d = sender as DependencyObject;
        while (d != null && d is not DataGridColumnHeader) d = VisualTreeHelper.GetParent(d);
        if (d is DataGridColumnHeader h) h.HorizontalContentAlignment = HorizontalAlignment.Center;
    }
}
