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
            if (e.Key == System.Windows.Input.Key.Escape && DataContext is MassInvoiceViewModel { IsReviewing: true, IsBusy: false } rv && !rv.IsAddingParty)
            {
                rv.CloseReviewCommand.Execute(null);
                e.Handled = true;
                return;
            }
            if (e.Key == System.Windows.Input.Key.Escape && DataContext is MassInvoiceViewModel { IsAddingParty: true } vm)
            {
                vm.PartyForm.CancelCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    /// <summary>The typed / picked name stops before the X and + icons on the right of the box.</summary>
    private void OnPartyComboLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ComboBox box) return;
        box.ApplyTemplate();
        if (box.Template?.FindName("PART_EditableTextBox", box) is FrameworkElement tb)
        {
            var m = tb.Margin;
            if (m.Right < 60) tb.Margin = new Thickness(m.Left, m.Top, m.Right + 56, m.Bottom);
        }
    }

    private void OnAmountLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MassRowViewModel row }) row.FormatAmount();
    }

    private void OnReviewAmountLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AnalysisItemViewModel item }) item.FormatAmount();
    }

    private void OnInvoiceNoLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MassRowViewModel row }) row.CommitInvoiceNo();
    }

    /// <summary>Review › Select all: its header cell white (the blue outlined box stands out on it), the box centred.</summary>
    private void OnSelectAllLoaded(object sender, RoutedEventArgs e)
    {
        DependencyObject? d = sender as DependencyObject;
        while (d != null && d is not DataGridColumnHeader) d = VisualTreeHelper.GetParent(d);
        if (d is DataGridColumnHeader h)
        {
            h.Background = System.Windows.Media.Brushes.White;
            h.Padding = new Thickness(0);
            h.HorizontalContentAlignment = HorizontalAlignment.Center;
            h.VerticalContentAlignment = VerticalAlignment.Center;
        }
    }

    private void OnCenterHeaderLoaded(object sender, RoutedEventArgs e)
    {
        DependencyObject? d = sender as DependencyObject;
        while (d != null && d is not DataGridColumnHeader) d = VisualTreeHelper.GetParent(d);
        if (d is DataGridColumnHeader h) h.HorizontalContentAlignment = HorizontalAlignment.Center;
    }
}
