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

/// <summary>Animated background of the Home page (Settings → Home background).</summary>
public sealed class BackgroundSettings
{
    public bool Enabled { get; set; } = true;
    /// <summary>Network, Constellation, Floating, DataStream, Waves, Sphere.</summary>
    public string Style { get; set; } = "Network";
    /// <summary>0.2 (very slow) … 3 (fast); 1 = normal.</summary>
    public double Speed { get; set; } = 1.0;
    /// <summary>Dot opacity 0.05 … 1 (lines are fainter).</summary>
    public double Opacity { get; set; } = 0.45;
    /// <summary>"auto" (follows the theme) or #RRGGBB.</summary>
    public string Color { get; set; } = "auto";
    /// <summary>0.4 (few) … 2 (many); 1 = normal.</summary>
    public double Density { get; set; } = 1.0;
    public bool Mouse { get; set; } = true;

    public static readonly string[] Styles = ["Network", "Constellation", "Floating", "DataStream", "Waves", "Sphere", "Math", "LiveChart", "Aurora", "Geometry", "Ledger"];
}
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

    public BackgroundSettings Background { get; set; } = new();

    /// <summary>Accent colour: "auto" (Windows accent) or #RRGGBB.</summary>
    public string Accent { get; set; } = "auto";
    /// <summary>Last picked accent colours (newest first, up to 6).</summary>
    public List<string> RecentAccents { get; set; } = [];
    /// <summary>Colours last picked in the colour picker (newest first, up to 10).</summary>
    public List<string> RecentColors { get; set; } = [];
    /// <summary>Settings › Appearance: the accent colour part is open.</summary>
    public bool AppearanceOpen { get; set; }

    /// <summary>Folder of the extraction engine used by Mass Invoice › Analysis (SMART_EXTRACT_ENGINE V15 with deepmath_analyze.py).</summary>
    public string? AnalysisEngineFolder { get; set; }

    /// <summary>Windows notification for each new email (Google account in Settings).</summary>
    public bool MailToasts { get; set; } = true;

    /// <summary>Width of the name list on the Suppliers / Clients / Reports pages (dragged by the user), per page.</summary>
    public Dictionary<string, double> ListWidths { get; set; } = [];

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
