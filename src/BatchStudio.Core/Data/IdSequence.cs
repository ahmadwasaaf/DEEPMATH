using System.Globalization;
using System.Text.RegularExpressions;

namespace BatchStudio.Core.Data;

/// <summary>
/// Keeps the IDs of a list (SUP-001, SUP-002 ...) a serial without gaps: after a record is deleted (in the program
/// or in Excel) the records after it move up one number. The records keep their order (by their current number;
/// rows without an ID come last, in sheet order).
/// </summary>
public static class IdSequence
{
    /// <summary>
    /// New IDs (same order as <paramref name="ids"/>), or null when nothing changes or the list does not use the
    /// serial pattern (an ID that is not PREFIX-number is never touched).
    /// </summary>
    public static string[]? Renumber(IReadOnlyList<string?> ids, string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || ids.Count == 0) return null;
        var rx = new Regex("^" + Regex.Escape(prefix) + @"-(\d+)$", RegexOptions.IgnoreCase);
        int width = 3;
        var nums = new int?[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            var id = (ids[i] ?? "").Trim();
            if (id.Length == 0) continue;
            var m = rx.Match(id);
            if (!m.Success) return null;                       // hand-made IDs: left alone
            nums[i] = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            width = Math.Max(width, m.Groups[1].Value.Length);
        }
        var order = Enumerable.Range(0, ids.Count)
            .OrderBy(i => nums[i] is null ? 1 : 0)
            .ThenBy(i => nums[i] ?? int.MaxValue)
            .ThenBy(i => i)
            .ToList();
        var result = new string[ids.Count];
        bool changed = false;
        for (int k = 0; k < order.Count; k++)
        {
            int i = order[k];
            var want = $"{prefix}-{(k + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}";
            result[i] = want;
            if (!string.Equals((ids[i] ?? "").Trim(), want, StringComparison.Ordinal)) changed = true;
        }
        return changed ? result : null;
    }
}
