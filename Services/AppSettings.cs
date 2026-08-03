using System.Text.Json;

namespace Picall.Services;

public sealed class AppSettings
{
    public List<string> ExtraFolders { get; set; } = [];
    public HashSet<string> FavoritePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double TileWidth { get; set; } = 194;
    public bool IncludePhotos { get; set; } = true;
    public bool IncludeVideos { get; set; } = true;
    public string SortMode { get; set; } = "newest";
    public string? SelectedSource { get; set; }
    public HashSet<string> ExcludedFormats { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string DateFilter { get; set; } = "any";
    public string SizeFilter { get; set; } = "any";

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile)) ?? new AppSettings();
            settings.FavoritePaths = new HashSet<string>(settings.FavoritePaths, StringComparer.OrdinalIgnoreCase);
            settings.ExcludedFormats = new HashSet<string>(settings.ExcludedFormats, StringComparer.OrdinalIgnoreCase);
            settings.ExtraFolders = settings.ExtraFolders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return settings;
        }
        catch { return new AppSettings(); }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            var temp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, AppPaths.SettingsFile, true);
        }
        catch { }
    }
}
