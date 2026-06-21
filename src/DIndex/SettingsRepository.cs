using System.IO;
using System.Text.Json;

namespace DIndex;

public sealed class SettingsRepository
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppSettings Load()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDirectory);
            if (!File.Exists(AppPaths.SettingsPath))
            {
                var defaultSettings = CreateDefault();
                Save(defaultSettings);
                return defaultSettings;
            }

            var json = File.ReadAllText(AppPaths.SettingsPath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? CreateDefault();
            if (settings.SearchRoots.Count == 0)
            {
                settings.SearchRoots = CreateDefaultRoots();
            }
            return settings;
        }
        catch
        {
            return CreateDefault();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        var json = JsonSerializer.Serialize(settings, Options);
        File.WriteAllText(AppPaths.SettingsPath, json);
    }

    private static AppSettings CreateDefault()
    {
        return new AppSettings { SearchRoots = CreateDefaultRoots() };
    }

    private static List<string> CreateDefaultRoots()
    {
        var roots = new List<string>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { "Desktop", "Downloads", "Documents" })
        {
            var path = Path.Combine(userProfile, name);
            if (Directory.Exists(path)) roots.Add(path);
        }

        if (roots.Count == 0 && Directory.Exists(userProfile)) roots.Add(userProfile);
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
