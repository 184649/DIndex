using System.IO;

namespace DIndex;

public static class AppPaths
{
    // An explicit override keeps acceptance tests isolated from the normal profile.
    public static string AppDataDirectory { get; } = ResolveDataDirectory();
    private static string ResolveDataDirectory()
    {
        var custom = Environment.GetEnvironmentVariable("DINDEX_DATA_DIR");
        return string.IsNullOrWhiteSpace(custom)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DIndex")
            : Path.GetFullPath(custom);
    }
    public static string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");
    public static string CachePath => Path.Combine(AppDataDirectory, "index-cache.tsv");
    public static string ErrorLogPath => Path.Combine(AppDataDirectory, "error.log");
    public static string UpdateWorkDirectory => Path.Combine(AppDataDirectory, "update");
}

