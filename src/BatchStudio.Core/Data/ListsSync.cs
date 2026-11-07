using ClosedXML.Excel;

namespace BatchStudio.Core.Data;

/// <summary>
/// Keeps the dropdown lists inside Suppliers.xlsx / Clients.xlsx up to date. In the V28 workbook the
/// Category and DEFAULT TEMPLATE dropdowns read other sheets; in the Database each file carries a hidden
/// "_Lists" sheet (columns named after the workbook names V06CategoryList, V14_ClientCategories,
/// V11_TemplateChoices) that this class refreshes from Categories.xlsx and the Templates folder.
/// </summary>
public static class ListsSync
{
    public const string SheetName = "_Lists";
    private static readonly string[] CategoryNames = ["V06CategoryList", "V14_ClientCategories"];
    private static readonly string[] TemplateNames = ["V11_TemplateChoices"];

    /// <summary>Returns the files that were updated. Locked files are skipped (retried on the next refresh).</summary>
    public static List<string> Update(string root, IReadOnlyList<string> categories, IReadOnlyList<string> templates)
    {
        var changed = new List<string>();
        foreach (var def in new[] { DataSets.Suppliers, DataSets.Clients })
        {
            var path = def.FullPath(root);
            if (!File.Exists(path)) continue;
            try
            {
                if (UpdateFile(path, categories, templates)) changed.Add(path);
            }
            catch (FileLockedException) { }
            catch (IOException) { }
        }
        return changed;
    }

    public static bool UpdateFile(string path, IReadOnlyList<string> categories, IReadOnlyList<string> templates)
    {
        using var wb = PackageRepair.OpenWorkbook(path);
        if (!wb.Worksheets.TryGetWorksheet(SheetName, out var ws)) return false;
        bool dirty = false;
        int lastCol = ws.Row(1).LastCellUsed()?.Address.ColumnNumber ?? 0;
        for (int c = 1; c <= lastCol; c++)
        {
            var name = ws.Cell(1, c).GetString().Trim();
            IReadOnlyList<string>? values = CategoryNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? categories
                : TemplateNames.Contains(name, StringComparer.OrdinalIgnoreCase) ? templates : null;
            if (values == null || values.Count == 0) continue;
            int oldLast = ws.Column(c).LastCellUsed()?.Address.RowNumber ?? 1;
            var current = Enumerable.Range(2, Math.Max(0, oldLast - 1)).Select(r => ws.Cell(r, c).GetString().Trim()).ToList();
            if (current.SequenceEqual(values)) continue;
            if (oldLast >= 2) ws.Range(2, c, oldLast, c).Clear(XLClearOptions.Contents);
            for (int i = 0; i < values.Count; i++) ws.Cell(i + 2, c).Value = values[i];
            var range = ws.Range(2, c, values.Count + 1, c);
            try { wb.DefinedNames.Delete(name); } catch { }
            wb.DefinedNames.Add(name, range);
            dirty = true;
        }
        if (!dirty) return false;
        ExcelTableStore.EnsureWritable(path);
        var tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $".~lists.{Guid.NewGuid():N}.tmp.xlsx");
        wb.SaveAs(tmp);
        File.Replace(tmp, path, null);
        return true;
    }
}
