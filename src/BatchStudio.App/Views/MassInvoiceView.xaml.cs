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
            if (e.Key == System.Windows.Input.Key.Escape && DataContext is MassInvoiceViewModel { IsReviewFormOpen: true } fv)
            {
                fv.ReviewForm.CancelCommand.Execute(null);
                e.Handled = true;
                return;
            }
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

    /// <summary>Right-click on a row outside the selection: that row alone is selected (the menu acts on it).</summary>
    private void OnReviewRightClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        DependencyObject? d = e.OriginalSource as DependencyObject;
        while (d != null && d is not DataGridRow)
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);   // a Run inside a TextBlock is not a Visual
        if (d is DataGridRow row && !row.IsSelected)
        {
            ReviewGrid.SelectedItems.Clear();
            row.IsSelected = true;
        }
    }

    /// <summary>Review › right-click › Package All: the selected rows (or the row right-clicked) moved to "Rejected files".</summary>
    private void OnPackageSelected(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MassInvoiceViewModel vm) return;
        var items = ReviewGrid.SelectedItems.OfType<AnalysisItemViewModel>().ToList();
        if (items.Count == 0) return;
        vm.PackageMany(items);
    }

    /// <summary>Review › right-click › Export as text: the selected rows (or the row right-clicked), one .txt each on the Desktop.</summary>
    private async void OnExportTextSelected(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MassInvoiceViewModel vm) return;
        var items = ReviewGrid.SelectedItems.OfType<AnalysisItemViewModel>().ToList();
        await vm.ExportTextAsync(items);
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
            h.MinWidth = 0;
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
