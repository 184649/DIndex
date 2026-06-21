using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace DIndex;

public sealed class UpdateService
{
    private const string VersionUrl = "https://raw.githubusercontent.com/Chairman-bits/DIndex/main/version.json";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string CurrentVersion => NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0");

    public async Task<(bool HasUpdate, UpdateInfo? Info, string Message)> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var json = await Http.GetStringAsync(VersionUrl, cancellationToken);
            var info = JsonSerializer.Deserialize<UpdateInfo>(json, JsonOptions);
            if (info is null || string.IsNullOrWhiteSpace(info.Version))
            {
                return (false, null, "version.json を読み取れませんでした。");
            }

            var current = new Version(CurrentVersion);
            var latest = new Version(NormalizeVersion(info.Version));
            if (latest > current)
            {
                return (true, info, $"新しいバージョン {info.Version} があります。");
            }

            return (false, info, $"最新です。現在: {CurrentVersion}");
        }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
            return (false, null, $"更新確認に失敗しました: {ex.Message}");
        }
    }

    public async Task<bool> StartUpdateAsync(UpdateInfo info, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(info.DownloadUrl) || string.IsNullOrWhiteSpace(info.UpdaterUrl))
        {
            System.Windows.MessageBox.Show("version.json に downloadUrl / updaterUrl がありません。", "DIndex", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }

        try
        {
            Directory.CreateDirectory(AppPaths.UpdateWorkDirectory);
            var updaterZip = Path.Combine(AppPaths.UpdateWorkDirectory, "DIndexUpdater.zip");
            var updaterDir = Path.Combine(AppPaths.UpdateWorkDirectory, "updater");
            if (Directory.Exists(updaterDir)) Directory.Delete(updaterDir, true);
            Directory.CreateDirectory(updaterDir);

            await DownloadFileAsync(info.UpdaterUrl, updaterZip, cancellationToken);
            ZipFile.ExtractToDirectory(updaterZip, updaterDir, true);

            var updaterExe = Directory.EnumerateFiles(updaterDir, "DIndexUpdater.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(updaterExe) || !File.Exists(updaterExe))
            {
                System.Windows.MessageBox.Show("DIndexUpdater.exe が見つかりません。", "DIndex", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return false;
            }

            var appExe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName ?? Path.Combine(System.AppContext.BaseDirectory, "DIndex.exe");
            var args = $"--processId {Environment.ProcessId} --appPath \"{appExe}\" --appZip \"{info.DownloadUrl}\" --restart";
            Process.Start(new ProcessStartInfo
            {
                FileName = updaterExe,
                Arguments = args,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(updaterExe) ?? updaterDir
            });
            return true;
        }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
            System.Windows.MessageBox.Show(ex.ToString(), "DIndex Update Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return false;
        }
    }

    private static async Task DownloadFileAsync(string url, string path, CancellationToken cancellationToken)
    {
        await using var input = await Http.GetStreamAsync(url, cancellationToken);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static string NormalizeVersion(string value)
    {
        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries).Take(3).ToList();
        while (parts.Count < 3) parts.Add("0");
        return string.Join('.', parts);
    }
}
