using System.IO;
using System.Security;

namespace DIndex.Updating;

// MSI owns the entire installed payload. An EXE-only replacement would bypass
// Windows Installer repair/upgrade bookkeeping and can mix incompatible files.
internal static class InstallationMode
{
    internal const string MarkerFileName = "installed-by-msi.json";
    internal const string UpdateMessage = "このアプリはMSIでインストールされています。EXE単体の自動更新は利用できません。更新は新しいMSIを実行し、修復はインストール済みバージョンのMSIから行ってください。";

    internal static bool IsMsiManaged(string appDirectory)
    {
        var marker = Path.Combine(Path.GetFullPath(appDirectory), MarkerFileName);
        try
        {
            // Presence alone is authoritative, even for an empty/corrupt marker,
            // a directory, or a link. Never parse untrusted JSON to enable updates.
            _ = File.GetAttributes(marker);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        // A failure to inspect installation state must not enable EXE updates.
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
        catch (SecurityException) { return true; }
    }

    internal static void ThrowIfMsiManaged(string appDirectory)
    {
        if (IsMsiManaged(appDirectory)) throw new InvalidOperationException(UpdateMessage);
    }
}
