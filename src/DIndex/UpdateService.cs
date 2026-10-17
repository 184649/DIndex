using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using DIndex.Updating;

namespace DIndex;

public sealed class UpdateService
{
    private const string VersionUrl = "https://raw.githubusercontent.com/Chairman-bits/DIndex/main/version.json";
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private bool _updateStarted;

    public bool IsPreviewBuild => Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Contains("-preview", StringComparison.OrdinalIgnoreCase) == true;

    public string CurrentVersion => NormalizeVersion(Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0");

    public async Task<(bool HasUpdate, UpdateInfo? Info, string Message)> CheckAsync(CancellationToken cancellationToken)
    {
        if (IsPreviewBuild) return (false, null, "開発版は自動更新の対象外です。このブランチから再ビルドして更新してください。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var readToken = timeout.Token;
        try
        {
            using var response = await Http.GetAsync(VersionUrl, HttpCompletionOption.ResponseHeadersRead, readToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > 128 * 1024) throw new InvalidDataException("更新情報が大きすぎます。");
            // A bounded stream also handles responses without Content-Length.
            await using var input = await response.Content.ReadAsStreamAsync(readToken);
            using var data = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await input.ReadAsync(buffer, readToken)) != 0)
            {
                if (data.Length + read > 128 * 1024) throw new InvalidDataException("更新情報が大きすぎます。");
                data.Write(buffer, 0, read);
            }
            var info = DeserializeManifest(data.ToArray());
            if (info is null || !Version.TryParse(NormalizeVersion(info.Version), out var latest))
                return (false, null, "version.json のバージョンを読み取れませんでした。");
            if (latest > new Version(CurrentVersion)) return (true, info, $"新しいバージョン {info.Version} があります。");
            return (false, info, $"最新です。現在: {CurrentVersion}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
            return (false, null, $"更新確認に失敗しました: {ex.Message}");
        }
    }

    public async Task<bool> StartUpdateAsync(UpdateInfo info, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!await _updateGate.WaitAsync(0, cancellationToken)) return false;
        string? workDirectory = null;
        var launched = false;
        try
        {
            if (_updateStarted) return false;
            if (IsPreviewBuild) throw new InvalidOperationException("開発版は自動更新の対象外です。このブランチから再ビルドして更新してください。");
            UpdatePackageSafety.ValidatePackageUrl(info.DownloadUrl);
            UpdatePackageSafety.ValidatePackageUrl(info.UpdaterUrl);
            UpdatePackageSafety.ValidateHash(info.DownloadSha256);
            UpdatePackageSafety.ValidateHash(info.UpdaterSha256);
            var appExe = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidOperationException("実行中のアプリのパスが見つかりません。"));
            if (!Path.GetFileName(appExe).Equals("DIndex.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("自動更新は配布版 DIndex.exe から実行してください。");
            var appDirectory = Path.GetDirectoryName(appExe)!;
            if (File.Exists(Path.Combine(appDirectory, "DIndex.dll")) || File.Exists(Path.Combine(appDirectory, "DIndex.runtimeconfig.json")))
                throw new InvalidOperationException("この起動方法は開発用ビルドです。自動更新は単一 EXE の配布版のみ対応しています。手動でビルドまたは更新してください。");
            // Fail before closing the application if its installation directory is read-only.
            var probe = Path.Combine(appDirectory, $".dindex-write-test-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }

            workDirectory = Path.Combine(AppPaths.UpdateWorkDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            var updaterZip = Path.Combine(workDirectory, "DIndexUpdater.zip");
            var appZip = Path.Combine(workDirectory, "DIndex.zip");
            // Download and validate both packages while DIndex is still running.
            await UpdatePackageSafety.DownloadVerifiedAsync(info.UpdaterUrl, updaterZip, info.UpdaterSha256, cancellationToken);
            await UpdatePackageSafety.DownloadVerifiedAsync(info.DownloadUrl, appZip, info.DownloadSha256, cancellationToken);
            var updaterDirectory = Path.Combine(workDirectory, "updater");
            var updaterExe = await UpdatePackageSafety.ExtractExecutableAsync(updaterZip, updaterDirectory, "DIndexUpdater.exe", cancellationToken);
            await UpdatePackageSafety.ExtractExecutableAsync(appZip, Path.Combine(workDirectory, "app-check"), "DIndex.exe", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var startInfo = new ProcessStartInfo { FileName = updaterExe, UseShellExecute = false, WorkingDirectory = updaterDirectory };
            foreach (var argument in new[] { "--processId", Environment.ProcessId.ToString(), "--appPath", appExe, "--appZipPath", appZip,
                         "--appSha256", info.DownloadSha256, "--restart" }) startInfo.ArgumentList.Add(argument);
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("更新プログラムを起動できませんでした。");
            launched = true;
            _updateStarted = true;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
            System.Windows.MessageBox.Show(ex.Message, "DIndex Update Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return false;
        }
        finally
        {
            if (!launched && workDirectory is not null)
            {
                try { Directory.Delete(workDirectory, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            _updateGate.Release();
        }
    }

    internal static UpdateInfo? DeserializeManifest(byte[] bytes)
    {
        ReadOnlySpan<byte> json = bytes;
        if (json.Length >= 3 && json[0] == 0xef && json[1] == 0xbb && json[2] == 0xbf) json = json[3..];
        return JsonSerializer.Deserialize<UpdateInfo>(json, JsonOptions);
    }

    private static string NormalizeVersion(string value)
    {
        if (!Version.TryParse(value, out var version)) return string.Empty;
        return $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
    }
}
