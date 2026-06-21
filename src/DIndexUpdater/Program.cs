using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;

namespace DIndexUpdater;

internal static class Program
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = ParseArgs(args);
            if (!options.TryGetValue("processId", out var processIdText) ||
                !options.TryGetValue("appPath", out var appPath) ||
                !options.TryGetValue("appZip", out var appZipUrl))
            {
                Console.WriteLine("Usage: DIndexUpdater --processId <id> --appPath <path> --appZip <url> [--restart]");
                return 1;
            }

            _ = int.TryParse(processIdText, out var processId);
            var restart = options.ContainsKey("restart");
            var appDirectory = Path.GetDirectoryName(appPath);
            if (string.IsNullOrWhiteSpace(appDirectory)) return 1;

            Console.WriteLine("DIndex updater started.");
            await WaitForProcessExitAsync(processId);

            var workDir = Path.Combine(Path.GetTempPath(), "DIndexUpdater", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);
            var zipPath = Path.Combine(workDir, "DIndex.zip");
            var extractDir = Path.Combine(workDir, "extract");
            Directory.CreateDirectory(extractDir);

            Console.WriteLine("Downloading app zip...");
            await DownloadFileAsync(appZipUrl, zipPath);
            Console.WriteLine("Extracting...");
            ZipFile.ExtractToDirectory(zipPath, extractDir, true);

            var newExe = Directory.EnumerateFiles(extractDir, "DIndex.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (string.IsNullOrWhiteSpace(newExe) || !File.Exists(newExe))
            {
                Console.WriteLine("DIndex.exe was not found in downloaded zip.");
                return 1;
            }

            var backupPath = appPath + ".bak";
            if (File.Exists(backupPath)) SafeDelete(backupPath);
            if (File.Exists(appPath)) File.Move(appPath, backupPath, true);

            try
            {
                File.Copy(newExe, appPath, true);
                if (File.Exists(backupPath)) SafeDelete(backupPath);
            }
            catch
            {
                if (File.Exists(backupPath)) File.Move(backupPath, appPath, true);
                throw;
            }

            if (restart)
            {
                Console.WriteLine("Restarting DIndex...");
                Process.Start(new ProcessStartInfo { FileName = appPath, UseShellExecute = true, WorkingDirectory = appDirectory });
            }

            try { Directory.Delete(workDir, true); } catch { }
            Console.WriteLine("Update completed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            try
            {
                var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DIndex");
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "updater.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\r\n{ex}\r\n\r\n");
            }
            catch
            {
            }
            return 1;
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            if (!key.StartsWith("--", StringComparison.Ordinal)) continue;
            key = key[2..];
            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                dict[key] = args[i + 1];
                i++;
            }
            else
            {
                dict[key] = "true";
            }
        }
        return dict;
    }

    private static async Task WaitForProcessExitAsync(int processId)
    {
        if (processId <= 0) return;
        try
        {
            var process = Process.GetProcessById(processId);
            for (var i = 0; i < 120 && !process.HasExited; i++)
            {
                await Task.Delay(500);
                process.Refresh();
            }
            if (!process.HasExited)
            {
                process.Kill(true);
                await process.WaitForExitAsync();
            }
        }
        catch
        {
        }
    }

    private static async Task DownloadFileAsync(string url, string path)
    {
        await using var input = await Http.GetStreamAsync(url);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
        await input.CopyToAsync(output);
    }

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
