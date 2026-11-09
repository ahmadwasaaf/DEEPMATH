using System.Windows;
using System.Windows.Controls;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class NewInvoiceView : UserControl
{
    public NewInvoiceView() => InitializeComponent();

    private void OnCenterHeaderLoaded(object sender, RoutedEventArgs e)
    {
        DependencyObject? d = sender as DependencyObject;
        while (d != null && d is not System.Windows.Controls.Primitives.DataGridColumnHeader)
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        if (d is System.Windows.Controls.Primitives.DataGridColumnHeader h)
            h.HorizontalContentAlignment = HorizontalAlignment.Center;
    }

    /// <summary>30000 → 30,000 (30000.5 → 30,000.50) once the amount box is left.</summary>
    private void OnAmountLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is NewInvoiceViewModel vm) vm.FormatAmount();
    }
}
