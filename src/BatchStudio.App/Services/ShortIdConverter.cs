using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;

namespace BatchStudio.App.Services;

/// <summary>SUP-001 → SUP-1 in the lists (display only: the workbook keeps its codes).</summary>
public sealed partial class ShortIdConverter : IValueConverter
{
    [GeneratedRegex(@"^\s*([A-Za-z]+)-0*(\d+)\s*$")]
    private static partial Regex Code();

    public static string Short(string? id)
    {
        var m = Code().Match(id ?? "");
        return m.Success ? $"{m.Groups[1].Value}-{m.Groups[2].Value}" : id ?? "";
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Short(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value ?? "";
}
