using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace BatchStudio.Core.Data;

public enum ColumnKind { Text, Number, Date }

/// <summary>
/// In-memory copy of an Excel table. Every value is kept as a display string
/// (numbers in invariant culture, dates as yyyy-MM-dd) so comparing and hashing is exact.
/// </summary>
public sealed class TableData
{
    public List<string> Headers { get; } = [];
    public List<ColumnKind> Kinds { get; } = [];
    public List<string[]> Rows { get; } = [];

    public int KeyColumn { get; init; }

    public TableData Clone()
    {
        var t = new TableData { KeyColumn = KeyColumn };
        t.Headers.AddRange(Headers);
        t.Kinds.AddRange(Kinds);
        foreach (var r in Rows) t.Rows.Add((string[])r.Clone());
        return t;
    }

    public static string RowSignature(string[] row) => string.Join("\u001F", row.Select(v => v ?? ""));

    /// <summary>Map of key -> row. Duplicate ids get a suffix ("SUP-001#2") so every row stays addressable.</summary>
    public Dictionary<string, string[]> ByKey()
    {
        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int blank = 0;
        foreach (var r in Rows)
        {
            var k = (r.Length > KeyColumn ? r[KeyColumn] : "")?.Trim() ?? "";
            if (k.Length == 0) k = "(blank)#" + (++blank);
            if (seen.TryGetValue(k, out var n))
            {
                seen[k] = n + 1;
                k = $"{k}#{n + 1}";
            }
            else seen[k] = 1;
            map[k] = r;
        }
        return map;
    }

    /// <summary>Returns a row re-ordered to <paramref name="headers"/> (missing columns become empty).</summary>
    public string[] Project(string[] row, IReadOnlyList<string> headers)
    {
        var res = new string[headers.Count];
        for (int i = 0; i < headers.Count; i++)
        {
            int j = Headers.FindIndex(h => string.Equals(h, headers[i], StringComparison.OrdinalIgnoreCase));
            res[i] = j >= 0 && j < row.Length ? row[j] ?? "" : "";
        }
        return res;
    }

    public string ContentHash()
    {
        var sb = new StringBuilder();
        sb.AppendJoin("\u001E", Headers).Append('\n');
        foreach (var r in Rows) sb.Append(RowSignature(r)).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Next id such as SUP-052 from existing ids that follow PREFIX-number.</summary>
    public string NextId(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return "";
        int max = 0, width = 3;
        foreach (var r in Rows)
        {
            var k = r.Length > KeyColumn ? r[KeyColumn] ?? "" : "";
            if (!k.StartsWith(prefix + "-", StringComparison.OrdinalIgnoreCase)) continue;
            var num = k[(prefix.Length + 1)..];
            if (int.TryParse(num, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
            {
                max = Math.Max(max, n);
                width = Math.Max(width, num.Length);
            }
        }
        return $"{prefix}-{(max + 1).ToString().PadLeft(width, '0')}";
    }
}
