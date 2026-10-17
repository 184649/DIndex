using System.Collections.Concurrent;
using System.IO;

namespace DIndex;

/// <summary>Same-directory replacement keeps the previous file intact until the new file is durable.</summary>
internal static class AtomicFileStorage
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim GateFor(string path) => Gates.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));

    public static string CreateTemporaryPath(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
    }

    public static void Commit(string temporaryPath, string path, bool keepBackup = true)
    {
        if (!File.Exists(path))
        {
            File.Move(temporaryPath, path);
            return;
        }
        try { File.Replace(temporaryPath, path, keepBackup ? path + ".bak" : null, true); }
        catch (Exception replacementError) when (replacementError is IOException or UnauthorizedAccessException)
        {
            // ReplaceFile on Windows can move the original to its backup before reporting failure.
            if (keepBackup && !File.Exists(path) && File.Exists(path + ".bak"))
            {
                try { RestoreBackup(path); }
                catch (Exception recoveryError) { throw new AggregateException("ファイルの置換に失敗しました。バックアップからの復元も完了できませんでした。", replacementError, recoveryError); }
            }
            throw;
        }
    }

    public static void RestoreBackup(string path)
    {
        var temporary = CreateTemporaryPath(path);
        try
        {
            using (var input = new FileStream(path + ".bak", FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            Commit(temporary, path, keepBackup: false);
        }
        finally { DeleteTemporary(temporary); }
    }

    public static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
