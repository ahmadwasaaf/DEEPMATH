using System.Windows;
using System.Windows.Controls;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class NewInvoiceView : UserControl
{
    public NewInvoiceView() => InitializeComponent();

    /// <summary>30000 → 30,000 (30000.5 → 30,000.50) once the amount box is left.</summary>
    private void OnAmountLostFocus(object sender, RoutedEventArgs e)
    {
        if (DataContext is NewInvoiceViewModel vm) vm.FormatAmount();
    }
}
