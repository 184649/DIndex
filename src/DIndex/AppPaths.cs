using System.IO;

namespace DIndex;

public static class AppPaths
{
    public static string AppDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DIndex");
    public static string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");
    public static string CachePath => Path.Combine(AppDataDirectory, "index-cache.tsv");
    public static string ErrorLogPath => Path.Combine(AppDataDirectory, "error.log");
    public static string UpdateWorkDirectory => Path.Combine(AppDataDirectory, "update");
}
