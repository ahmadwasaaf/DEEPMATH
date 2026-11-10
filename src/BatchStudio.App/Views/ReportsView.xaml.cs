using System.Windows.Controls;
using System.Windows.Input;
using BatchStudio.App.Services;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class ReportsView : UserControl
{
    public ReportsView()
    {
        InitializeComponent();
        GridLook.NoCellFrame(List);
        ListWidth.Attach(ListSplitter, ListColumn, () => "list.reports", 330);
        SearchHost.SizeChanged += (_, _) => SearchBox.Width = Math.Max(0, Math.Min(300, SearchHost.ActualWidth));
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ReportsViewModel vm) vm.ViewReportCommand.Execute(null);
    }
}
