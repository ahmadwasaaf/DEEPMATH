using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BatchStudio.App.Services;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class TemplatesView : UserControl
{
    public TemplatesView() => InitializeComponent();

    /// <summary>Double-click on a template of the list: its sheet opens in Excel.</summary>
    private void OnTemplateDoubleClick(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d != null && !ReferenceEquals(d, sender); d = VisualTreeHelper.GetParent(d))
            if (d is ListBoxItem { DataContext: TemplateItemViewModel item })
            {
                TemplatePreview.Hide();
                (DataContext as TemplatesViewModel)?.OpenInExcel(item);
                e.Handled = true;
                return;
            }
    }
}
