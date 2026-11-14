using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BatchStudio.App.Services;
using BatchStudio.App.ViewModels;

namespace BatchStudio.App.Views;

public partial class BankAccountsView : UserControl
{
    public BankAccountsView()
    {
        InitializeComponent();
        GridLook.NoCellFrame(MovesGrid);
    }

    private BankAccountsViewModel? Vm => DataContext as BankAccountsViewModel;

    /// <summary>Enter in the amount / description adds the movement (several in a row, without the mouse).</summary>
    private void OnEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Vm == null) return;
        e.Handled = true;
        Vm.SubmitEntryCommand.Execute(null);
        if (string.IsNullOrEmpty(Vm.EntryError)) { Amount.Focus(); Amount.SelectAll(); }
    }

    /// <summary>Right-click selects the row under the mouse first, so its menu acts on it.</summary>
    private void OnRowRightDown(object sender, MouseButtonEventArgs e)
    {
        var row = Find<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row != null) { MovesGrid.SelectedItem = row.Item; row.Focus(); }
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Find<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is StatementRow r && r.IsMovement)
        {
            Vm?.EditMovementCommand.Execute(r);
            Amount.Focus();
            Amount.SelectAll();
        }
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        if (MovesGrid.SelectedItem is StatementRow r) { Vm?.EditMovementCommand.Execute(r); Amount.Focus(); Amount.SelectAll(); }
    }

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (MovesGrid.SelectedItem is StatementRow r) Vm?.DeleteMovementCommand.Execute(r);
    }

    private static T? Find<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }
}
