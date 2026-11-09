using System.Windows;
using System.Windows.Controls;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class NewInvoiceView : UserControl
{
    public NewInvoiceView()
    {
        InitializeComponent();
        // the expanded party details close with Esc or a click outside them (the arrow also toggles them)
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape && DataContext is NewInvoiceViewModel { DetailsExpanded: true } vm)
            { vm.DetailsExpanded = false; e.Handled = true; }
        };
        PreviewMouseDown += (_, e) =>
        {
            if (DataContext is not NewInvoiceViewModel { DetailsExpanded: true } vm) return;
            if (e.OriginalSource is not DependencyObject d) return;
            // clicks inside a dropdown list (separate popup window) belong to the details
            if (PresentationSource.FromDependencyObject(d) != PresentationSource.FromVisual(this)) return;
            for (var x = d; x != null; x = x is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(x) : LogicalTreeHelper.GetParent(x))
                if (x == PartyOverlay || x is Button { Name: "DetailsToggle" }) return;
            vm.DetailsExpanded = false;
        };
    }

    private void OnThemeLostFocus(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ThemeFieldViewModel t) t.CommitCommand.Execute(null);
    }

    private void OnThemeKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && (sender as FrameworkElement)?.DataContext is ThemeFieldViewModel t) t.CommitCommand.Execute(null);
    }

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
