using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class MassInvoiceView : UserControl
{
    public MassInvoiceView()
    {
        InitializeComponent();
        // Esc closes the new supplier / client form
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && DataContext is MassInvoiceViewModel { IsAddingParty: true } vm)
            {
                vm.PartyForm.CancelCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

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
