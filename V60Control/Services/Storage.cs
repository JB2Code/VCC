using System.IO;
using System.Text.Json;
using V60Control.Models;

namespace V60Control.Services;

/// <summary>Persistenz für Einstellungen und Presets unter %AppData%\V60Control.</summary>
public static class Storage
{
    public static string BaseDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "V60Control");

    public static string ThumbDir { get; } = Path.Combine(BaseDir, "thumbnails");

    private static string SettingsFile => Path.Combine(BaseDir, "settings.json");
    private static string PresetsFile => Path.Combine(BaseDir, "presets.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    static Storage()
    {
        Directory.CreateDirectory(BaseDir);
        Directory.CreateDirectory(ThumbDir);
    }

    public static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsFile))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void SaveSettings(AppSettings settings)
        => File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, JsonOpts));

    public static List<Preset> LoadPresets()
    {
        try
        {
            if (File.Exists(PresetsFile))
                return JsonSerializer.Deserialize<List<Preset>>(File.ReadAllText(PresetsFile)) ?? [];
        }
        catch { }
        return [];
    }

    public static void SavePresets(IEnumerable<Preset> presets)
        => File.WriteAllText(PresetsFile, JsonSerializer.Serialize(presets.ToList(), JsonOpts));
}
