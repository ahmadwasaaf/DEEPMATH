using System.Text.Json;
using System.Text.Json.Serialization;

namespace BatchStudio.Core.Entities;

/// <summary>One business entity (e.g. "Afaq") and the Database folder that belongs to it.</summary>
public sealed class EntityProfile
{
    public string Name { get; set; } = "";
    public string DatabasePath { get; set; } = "";
    public DateTime LastOpenedUtc { get; set; }

    public override string ToString() => Name;
}

public enum AppLanguage { English, Arabic }
public enum AppTheme { Dark, Light }

/// <summary>Per-user settings, stored in %AppData%\BATCH STUDIO\settings.json.</summary>
public sealed class AppSettings
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppLanguage Language { get; set; } = AppLanguage.English;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public List<EntityProfile> Entities { get; set; } = [];
    public string? ActiveEntity { get; set; }

    /// <summary>Save edits automatically a few seconds after the last change.</summary>
    public bool AutoSave { get; set; } = true;
    public int KeepBackups { get; set; } = 30;

    public EntityProfile? Active =>
        Entities.FirstOrDefault(e => string.Equals(e.Name, ActiveEntity, StringComparison.OrdinalIgnoreCase))
        ?? Entities.OrderByDescending(e => e.LastOpenedUtc).FirstOrDefault();
}

public static class SettingsStore
{
    public static string Folder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BATCH STUDIO");

    public static string FilePath => Path.Combine(Folder, "settings.json");

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings();
        }
        catch { /* corrupt settings: start fresh, keep the old file */ }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Folder);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings, Json));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
