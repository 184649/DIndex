using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using DIndex.Updating;

namespace DIndexUpdater;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string? workDirectory = null;
        string? stagedExecutable = null;
        string? dataDirectory = null;
        string? appPathForRecovery = null;
        var restart = false;
        var originalExited = false;
        var installed = false;
        try
        {
            var options = ParseArgs(args);
            if (!options.TryGetValue("processId", out var processIdText) || !int.TryParse(processIdText, out var processId) || processId <= 0 ||
                !options.TryGetValue("appPath", out var rawAppPath) || !Path.IsPathFullyQualified(rawAppPath) ||
                !options.TryGetValue("appSha256", out var appSha256) ||
                (options.ContainsKey("appZip") == options.ContainsKey("appZipPath")))
                throw new ArgumentException("Usage: DIndexUpdater --processId <id> --appPath <absolute path> (--appZipPath <path> | --appZip <official URL>) --appSha256 <SHA-256> [--restart]");
            UpdatePackageSafety.ValidateHash(appSha256);
            restart = options.ContainsKey("restart");
            if (options.TryGetValue("dataDirectory", out dataDirectory))
            {
                if (!Path.IsPathFullyQualified(dataDirectory)) throw new ArgumentException("The data directory must be absolute.");
                dataDirectory = Path.GetFullPath(dataDirectory);
            }
            if (options.ContainsKey("readyFile") != options.ContainsKey("proceedFile")) throw new ArgumentException("Both handshake paths must be provided.");
            if (options.ContainsKey("readyFile") && (!Path.IsPathFullyQualified(options["readyFile"]) || !Path.IsPathFullyQualified(options["proceedFile"])))
                throw new ArgumentException("Handshake paths must be absolute.");
            var appPath = Path.GetFullPath(rawAppPath);
            if (!Path.GetFileName(appPath).Equals("DIndex.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(appPath))
                throw new InvalidDataException("The existing DIndex.exe was not found.");
            if ((File.GetAttributes(appPath) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Updating a linked executable is not supported.");
            var appDirectory = Path.GetDirectoryName(appPath)!;
            InstallationMode.ThrowIfMsiManaged(appDirectory);
            appPathForRecovery = appPath;
            // One updater per installation. Never kill an application or replace files while it is still running.
            using var updateLock = new FileStream(Path.Combine(appDirectory, ".dindex-update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
            workDirectory = Path.Combine(Path.GetTempPath(), "DIndexUpdater", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDirectory);
            var zipPath = Path.Combine(workDirectory, "DIndex.zip");
            if (options.TryGetValue("appZipPath", out var sourceZip))
            {
                if (!Path.IsPathFullyQualified(sourceZip)) throw new InvalidDataException("The package path must be absolute.");
                File.Copy(sourceZip, zipPath, overwrite: false);
                await UpdatePackageSafety.VerifyFileHashAsync(zipPath, appSha256, CancellationToken.None);
            }
            else
            {
                await UpdatePackageSafety.DownloadVerifiedAsync(options["appZip"], zipPath, appSha256, CancellationToken.None);
            }
            var executable = await UpdatePackageSafety.ExtractExecutableAsync(zipPath, Path.Combine(workDirectory, "extract"), "DIndex.exe", CancellationToken.None);
            stagedExecutable = Path.Combine(appDirectory, $".DIndex.{Guid.NewGuid():N}.staged");
            using (var input = File.OpenRead(executable))
            using (var output = new FileStream(stagedExecutable, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            if (options.TryGetValue("readyFile", out var readyFile))
            {
                SignalReady(readyFile);
                await WaitForProceedAsync(options["proceedFile"], TimeSpan.FromSeconds(60), CancellationToken.None);
            }
            Console.WriteLine("Package verified. Waiting for DIndex to close...");
            await WaitForProcessExitAsync(processId, appPath, TimeSpan.FromSeconds(60));
            originalExited = true;
            InstallVerifiedExecutable(stagedExecutable, appPath);
            installed = true;
            stagedExecutable = null;
            if (restart)
            {
                try
                {
                    using var process = StartApplication(appPath, dataDirectory);
                    var exited = process.WaitForExitAsync();
                    if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(2))) == exited)
                        throw new InvalidOperationException($"The updated application exited immediately (code {process.ExitCode}).");
                }
                catch (Exception restartError)
                {
                    Rollback(appPath);
                    try { using var restored = StartApplication(appPath, dataDirectory); }
                    catch (Exception recoveryError) { throw new AggregateException("Update startup failed. The previous executable was restored, but automatic restart also failed.", restartError, recoveryError); }
                    throw new InvalidOperationException("Update startup failed. The previous executable was restored and restarted.", restartError);
                }
            }
            Console.WriteLine("Update completed. The previous executable is retained as DIndex.exe.bak.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            if (restart && originalExited && !installed && appPathForRecovery is not null && File.Exists(appPathForRecovery))
            {
                try { using var restored = StartApplication(appPathForRecovery, dataDirectory); }
                catch (Exception restartError) { ex = new AggregateException("Update failed; automatic restart of the previous application also failed.", ex, restartError); }
            }
            WriteErrorLog(ex, dataDirectory);
            return 1;
        }
        finally
        {
            if (stagedExecutable is not null) UpdatePackageSafety.TryDelete(stagedExecutable);
            if (workDirectory is not null)
            {
                try { Directory.Delete(workDirectory, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    internal static void InstallVerifiedExecutable(string stagedExecutable, string appPath, Action<string, string, string>? replace = null)
    {
        InstallationMode.ThrowIfMsiManaged(Path.GetDirectoryName(Path.GetFullPath(appPath))!);
        UpdatePackageSafety.ValidatePortableExecutable(stagedExecutable);
        UpdatePackageSafety.ValidatePortableExecutable(appPath);
        var originalHash = HashFile(appPath);
        try
        {
            // Windows can report failure after moving the original to its backup. Recover that state too.
            if (replace is null) File.Replace(stagedExecutable, appPath, appPath + ".bak", ignoreMetadataErrors: true);
            else replace(stagedExecutable, appPath, appPath + ".bak");
        }
        catch (Exception replacementError)
        {
            try
            {
                if (!File.Exists(appPath) || !CryptographicOperations.FixedTimeEquals(originalHash, HashFile(appPath)))
                {
                    if (!File.Exists(appPath + ".bak") || !CryptographicOperations.FixedTimeEquals(originalHash, HashFile(appPath + ".bak")))
                        throw new IOException("The original executable could not be located for recovery. Existing files were preserved.");
                    Rollback(appPath);
                }
            }
            catch (Exception recoveryError)
            {
                throw new AggregateException("Replacement failed and automatic recovery could not be completed. Preserve the .bak file for manual recovery.", replacementError, recoveryError);
            }
            throw;
        }
    }

    private static byte[] HashFile(string path)
    {
        using var input = File.OpenRead(path);
        return SHA256.HashData(input);
    }

    internal static void Rollback(string appPath)
    {
        var backup = appPath + ".bak";
        if (!File.Exists(backup)) throw new IOException("The backup executable could not be found; no rollback was attempted.");
        UpdatePackageSafety.ValidatePortableExecutable(backup);
        var recovery = Path.Combine(Path.GetDirectoryName(appPath)!, $".DIndex.{Guid.NewGuid():N}.recovery");
        try
        {
            using (var input = File.OpenRead(backup))
            using (var output = new FileStream(recovery, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(appPath)) File.Replace(recovery, appPath, appPath + ".failed", ignoreMetadataErrors: true);
            else File.Move(recovery, appPath);
        }
        finally { UpdatePackageSafety.TryDelete(recovery); }
    }

    private static Process StartApplication(string appPath, string? dataDirectory) => Process.Start(CreateRestartStartInfo(appPath, dataDirectory))
        ?? throw new InvalidOperationException("DIndex could not be started.");

    internal static ProcessStartInfo CreateRestartStartInfo(string appPath, string? dataDirectory)
    {
        var start = new ProcessStartInfo { FileName = appPath, UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(appPath)! };
        if (dataDirectory is not null)
        {
            start.Environment["DINDEX_DATA_DIR"] = dataDirectory;
        }
        return start;
    }

    internal static void SignalReady(string path) => UpdatePackageSafety.WriteSignal(path);

    internal static async Task WaitForProceedAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (UpdatePackageSafety.HasValidSignal(path)) return;
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException("The application did not authorize the prepared update. The installed executable was not changed.");
    }

    internal static Dictionary<string, string> ParseArgs(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var known = new HashSet<string>(new[] { "processId", "appPath", "appZip", "appZipPath", "appSha256", "dataDirectory", "readyFile", "proceedFile", "restart" }, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("Unexpected updater argument.");
            var key = args[i][2..];
            if (!known.Contains(key) || values.ContainsKey(key)) throw new ArgumentException("Unknown or duplicate updater option.");
            if (key.Equals("restart", StringComparison.OrdinalIgnoreCase)) values.Add(key, "true");
            else
            {
                if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(args[i])) throw new ArgumentException("Missing updater argument value.");
                values.Add(key, args[i]);
            }
        }
        return values;
    }

    internal static async Task WaitForProcessExitAsync(int processId, string appPath, TimeSpan timeout)
    {
        Process process;
        try { process = Process.GetProcessById(processId); }
        catch (ArgumentException) { return; }
        using (process)
        {
            if (process.HasExited) return;
            string? processPath;
            try { processPath = process.MainModule?.FileName; }
            catch (Exception) when (process.HasExited) { return; }
            if (processPath is null || !Path.GetFullPath(processPath).Equals(Path.GetFullPath(appPath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The supplied process ID does not belong to this DIndex installation.");
            using var cancellation = new CancellationTokenSource(timeout);
            try { await process.WaitForExitAsync(cancellation.Token); }
            catch (OperationCanceledException) { throw new TimeoutException("DIndex did not close in time. The installed executable was not changed."); }
        }
    }

    private static void WriteErrorLog(Exception exception, string? dataDirectory)
    {
        try
        {
            var directory = dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DIndex");
            Directory.CreateDirectory(directory);
            File.AppendAllText(Path.Combine(directory, "updater.log"), $"[{DateTimeOffset.Now:O}]\n{exception}\n\n");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
