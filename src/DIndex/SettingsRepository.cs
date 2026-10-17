using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DIndex;

public sealed class SettingsRepository
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _settingsPath;
    private string? _expectedFingerprint;
    private bool _hasObservation;
    private bool _loadAttempted;

    public SettingsRepository(string? settingsPath = null) => _settingsPath = Path.GetFullPath(settingsPath ?? AppPaths.SettingsPath);
    public string? LastLoadWarning { get; private set; }

    public AppSettings Load()
    {
        var gate = AtomicFileStorage.GateFor(_settingsPath);
        gate.Wait();
        try
        {
            LastLoadWarning = null;
            _loadAttempted = true;
            _hasObservation = false;
            try
            {
                _expectedFingerprint = Fingerprint(_settingsPath);
                _hasObservation = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ErrorLogger.Write(ex);
                LastLoadWarning = "設定ファイルの状態を確認できません。元ファイルを保護するため、再起動して読み込めるまで保存を停止します。";
                return new AppSettings { SearchRoots = new List<string>() };
            }
            Exception? primaryError = null;
            if (File.Exists(_settingsPath))
            {
                try { return ReadSettings(_settingsPath, out _expectedFingerprint); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    primaryError = ex;
                    ErrorLogger.Write(ex);
                }
            }
            if (File.Exists(_settingsPath + ".bak"))
            {
                try
                {
                    var settings = ReadSettings(_settingsPath + ".bak");
                    EnsureUnchanged();
                    if (File.Exists(_settingsPath)) PreserveCorruptFile(_settingsPath);
                    AtomicFileStorage.RestoreBackup(_settingsPath);
                    // Read back the restored bytes so the returned settings and save baseline agree.
                    settings = ReadSettings(_settingsPath, out _expectedFingerprint);
                    LastLoadWarning = "設定をバックアップから復元しました。";
                    return settings;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    primaryError ??= ex;
                    ErrorLogger.Write(ex);
                }
            }
            if (primaryError is not null)
                LastLoadWarning = "設定を読み込めないため既定値を使用します。設定ファイルとバックアップを確認してください。";
            // Do not overwrite corrupt or inaccessible user settings while merely loading them.
            return primaryError is null ? CreateDefault() : new AppSettings { SearchRoots = new List<string>() };
        }
        catch (InvalidOperationException ex)
        {
            ErrorLogger.Write(ex);
            LastLoadWarning = ex.Message;
            _hasObservation = false;
            return new AppSettings { SearchRoots = new List<string>() };
        }
        finally { gate.Release(); }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        var json = JsonSerializer.Serialize(settings, Options);
        var gate = AtomicFileStorage.GateFor(_settingsPath);
        gate.Wait();
        string? temporary = null;
        try
        {
            if (!_hasObservation && !_loadAttempted)
            {
                _expectedFingerprint = Fingerprint(_settingsPath);
                _hasObservation = true;
            }
            EnsureUnchanged();
            temporary = AtomicFileStorage.CreateTemporaryPath(_settingsPath);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)) writer.Write(json);
                stream.Flush(flushToDisk: true);
            }
            // Preserve a known-good backup when the user explicitly saves after recovering from corrupt JSON.
            var keepBackup = true;
            if (File.Exists(_settingsPath))
            {
                try { _ = ReadSettings(_settingsPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    PreserveCorruptFile(_settingsPath);
                    keepBackup = false;
                }
            }
            if (keepBackup && File.Exists(_settingsPath + ".bak"))
            {
                try { _ = ReadSettings(_settingsPath + ".bak"); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    PreserveCorruptFile(_settingsPath + ".bak");
                }
            }
            EnsureUnchanged();
            AtomicFileStorage.Commit(temporary, _settingsPath, keepBackup);
            _expectedFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            _hasObservation = true;
        }
        finally
        {
            if (temporary is not null) AtomicFileStorage.DeleteTemporary(temporary);
            gate.Release();
        }
    }

    private void EnsureUnchanged()
    {
        if (!_hasObservation)
            throw new InvalidOperationException("設定ファイルの状態を確認できないため保存できません。ファイルを確認してから再起動してください。");
        if (!string.Equals(_expectedFingerprint, Fingerprint(_settingsPath), StringComparison.Ordinal))
            throw new InvalidOperationException("設定ファイルが外部で変更されています。外部の変更を保護するため保存を中止しました。DIndex を再起動してから設定を変更してください。");
    }

    private static string? Fingerprint(string path)
    {
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return Convert.ToHexString(SHA256.HashData(input));
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static void PreserveCorruptFile(string path)
    {
        // A load failure must not turn the next preference edit into irreversible data loss.
        var archive = path + $".corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
        File.Copy(path, archive, overwrite: false);
    }

    private static AppSettings ReadSettings(string path) => ReadSettings(path, out _);

    private static AppSettings ReadSettings(string path, out string fingerprint)
    {
        var bytes = File.ReadAllBytes(path);
        fingerprint = Convert.ToHexString(SHA256.HashData(bytes));
        using var input = new MemoryStream(bytes);
        using var reader = new StreamReader(input, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var settings = JsonSerializer.Deserialize<AppSettings>(reader.ReadToEnd(), Options) ?? throw new InvalidDataException("設定が空です。");
        Validate(settings);
        settings.SearchRoots = settings.SearchRoots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        settings.ExcludeFolderNames = settings.ExcludeFolderNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return settings;
    }

    private static void Validate(AppSettings settings)
    {
        if (settings.SearchRoots is null || settings.ExcludeFolderNames is null || settings.SearchRoots.Any(string.IsNullOrWhiteSpace) ||
            settings.ExcludeFolderNames.Any(string.IsNullOrWhiteSpace) || settings.ResultLimit < 1 || settings.ResultLimit > 100_000)
            throw new InvalidDataException("設定の検索フォルダ、除外名、または表示件数が不正です。表示件数は 1～100,000 にしてください。");
    }

    private static AppSettings CreateDefault() => new() { SearchRoots = CreateDefaultRoots() };

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
