using System.Windows.Controls;
using System.Windows.Input;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is HistoryViewModel vm && vm.Selected != null) vm.OpenInvoiceCommand.Execute(vm.Selected);
    }
}
