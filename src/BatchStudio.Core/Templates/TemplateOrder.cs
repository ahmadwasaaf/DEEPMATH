using System.Text.RegularExpressions;

namespace BatchStudio.Core.Templates;

/// <summary>
/// The fixed order of templates in every list of the program: own templates (e.g. "Madar") first, then INVOICE,
/// then INV1, INV2, INV3 ... by number (INV10 after INV9, not after INV1), so new INV templates fall into place.
/// </summary>
public sealed partial class TemplateOrder : IComparer<string>
{
    public static readonly TemplateOrder Instance = new();

    [GeneratedRegex(@"^INV(\d+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex InvNumber();

    private static (int Group, int Number, string Name) Key(string? name)
    {
        var n = (name ?? "").Trim();
        if (string.Equals(n, "INVOICE", StringComparison.OrdinalIgnoreCase)) return (1, 0, n);
        var m = InvNumber().Match(n);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var k)) return (2, k, n);
        return (0, 0, n);
    }

    public int Compare(string? x, string? y)
    {
        var a = Key(x);
        var b = Key(y);
        int c = a.Group.CompareTo(b.Group);
        if (c == 0) c = a.Number.CompareTo(b.Number);
        if (c == 0) c = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return c;
    }

    public static List<string> Sort(IEnumerable<string> names) => names.OrderBy(n => n, Instance).ToList();
}
