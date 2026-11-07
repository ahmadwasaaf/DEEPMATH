using System.Text.Json;
using ClosedXML.Excel;

namespace BatchStudio.Core.Data;

/// <summary>
/// Folder structure of one entity's Database:
/// <code>
/// &lt;Entity&gt; Database\
///   Suppliers\Suppliers.xlsx      Clients\Clients.xlsx
///   Items\Items.xlsx              Categories\Categories.xlsx
///   INVOICE HISTORY\Invoice History.xlsx
///   Templates\                    (invoice templates + their .map.json)
///   BATCH STUDIO\SUPPLIERS\       BATCH STUDIO\CLIENTS\   (archived invoices)
///   .batchstudio\                 (entity.json, sync state, backups - hidden)
/// </code>
/// </summary>
public static class DatabaseLayout
{
    public const string TemplatesFolder = "Templates";
    public const string ArchiveFolder = "BATCH STUDIO";
    public const string ArchiveSuppliers = "SUPPLIERS";
    public const string ArchiveClients = "CLIENTS";
    public const string SystemFolder = ".batchstudio";
    public const string EntityFile = "entity.json";

    public static string Templates(string root) => Path.Combine(root, TemplatesFolder);
    public static string ArchiveSuppliersPath(string root) => Path.Combine(root, ArchiveFolder, ArchiveSuppliers);
    public static string ArchiveClientsPath(string root) => Path.Combine(root, ArchiveFolder, ArchiveClients);
    public static string SystemDir(string root) => Path.Combine(root, SystemFolder);
    public static string SyncState(string root) => Path.Combine(root, SystemFolder, "sync");
    public static string Backups(string root) => Path.Combine(root, SystemFolder, "backups");

    /// <summary>Lists what is missing from a Database folder (empty list = complete).</summary>
    public static IReadOnlyList<string> Validate(string root)
    {
        var missing = new List<string>();
        if (!Directory.Exists(root))
        {
            missing.Add(root);
            return missing;
        }
        foreach (var d in DataSets.All)
            if (!File.Exists(d.FullPath(root))) missing.Add(d.RelativePath);
        foreach (var f in new[] { TemplatesFolder, Path.Combine(ArchiveFolder, ArchiveSuppliers), Path.Combine(ArchiveFolder, ArchiveClients) })
            if (!Directory.Exists(Path.Combine(root, f))) missing.Add(f + Path.DirectorySeparatorChar);
        return missing;
    }

    /// <summary>Creates every missing folder and data file. Existing files are never touched.</summary>
    public static EntityInfo EnsureStructure(string root, string entityName)
    {
        Directory.CreateDirectory(root);
        foreach (var d in DataSets.All)
        {
            Directory.CreateDirectory(Path.Combine(root, d.Folder));
            var path = d.FullPath(root);
            if (!File.Exists(path)) CreateEmptyTable(path, d);
        }
        Directory.CreateDirectory(Templates(root));
        Directory.CreateDirectory(ArchiveSuppliersPath(root));
        Directory.CreateDirectory(ArchiveClientsPath(root));

        var sys = Directory.CreateDirectory(SystemDir(root));
        try { sys.Attributes |= FileAttributes.Hidden; } catch { /* not supported on every FS */ }
        Directory.CreateDirectory(SyncState(root));
        Directory.CreateDirectory(Backups(root));

        var info = ReadEntityInfo(root) ?? new EntityInfo { Name = entityName, CreatedUtc = DateTime.UtcNow };
        if (string.IsNullOrWhiteSpace(info.Name)) info.Name = entityName;
        WriteEntityInfo(root, info);
        return info;
    }

    public static EntityInfo? ReadEntityInfo(string root)
    {
        var p = Path.Combine(SystemDir(root), EntityFile);
        if (!File.Exists(p)) return null;
        try { return JsonSerializer.Deserialize<EntityInfo>(File.ReadAllText(p)); }
        catch { return null; }
    }

    public static void WriteEntityInfo(string root, EntityInfo info)
    {
        Directory.CreateDirectory(SystemDir(root));
        File.WriteAllText(Path.Combine(SystemDir(root), EntityFile),
            JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static void CreateEmptyTable(string path, DataSetDefinition def)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(def.SheetName);
        for (int i = 0; i < def.DefaultHeaders.Count; i++)
        {
            var c = ws.Cell(1, i + 1);
            c.Value = def.DefaultHeaders[i];
            c.Style.Font.Bold = true;
            c.Style.Fill.BackgroundColor = XLColor.FromHtml("#1F2D46");
            c.Style.Font.FontColor = XLColor.White;
        }
        ws.SheetView.FreezeRows(1);
        ws.Columns(1, def.DefaultHeaders.Count).Width = 24;
        wb.SaveAs(path);
    }
}

public sealed class EntityInfo
{
    public string Name { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public int SchemaVersion { get; set; } = 1;
    /// <summary>Default currency for amount-in-words (USD / SAR).</summary>
    public string Currency { get; set; } = "USD";
}
