using System.Data;
using System.Globalization;
using System.Windows.Data;
using BatchStudio.App.ViewModels;
using BatchStudio.Core.Invoices;

namespace BatchStudio.App.Services;

/// <summary>
/// Bank-account count shown next to a supplier's name when it has two or more ("" otherwise).
/// Values: the row (a DataRowView of the Suppliers list, or a <see cref="Party"/>, or a name), a version (refresh), the page.
/// </summary>
public sealed class BankBadgeConverter : IMultiValueConverter
{
    /// <summary>Counts by supplier name for lists of plain names (Mass Invoice dropdowns).</summary>
    public static Dictionary<string, int> CountsByName { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        int n = 0;
        var item = values.Length > 0 ? values[0] : null;
        if (item is Party p) n = p.BankCount;
        else if (item is DataRowView rv && values.Length > 2 && values[2] is DataSetViewModel vm) n = vm.BankCount(rv.Row);
        else if (item is string name && CountsByName.TryGetValue(name.Trim(), out var c)) n = c;
        return n >= 2 ? n.ToString(CultureInfo.InvariantCulture) : "";
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
