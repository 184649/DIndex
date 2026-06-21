using System.IO;
using System.Text;

namespace DIndex;

public sealed class IndexCacheService
{
    public async Task SaveAsync(IEnumerable<SearchRecord> records, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDirectory);
            await using var stream = new FileStream(AppPaths.CachePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var r in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = string.Join('\t', Escape(r.FullPath), Escape(r.Name), Escape(r.Folder), Escape(r.Extension), r.IsDirectory ? "1" : "0", r.SizeBytes, r.UpdatedTicks);
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
            }
        }
        catch
        {
        }
    }

    public async Task<List<SearchRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        var list = new List<SearchRecord>();
        try
        {
            if (!File.Exists(AppPaths.CachePath)) return list;
            using var stream = new FileStream(AppPaths.CachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1024 * 128, true);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 7) continue;
                var fullPath = Unescape(parts[0]);
                var name = Unescape(parts[1]);
                var folder = Unescape(parts[2]);
                var extension = Unescape(parts[3]);
                _ = long.TryParse(parts[5], out var sizeBytes);
                _ = long.TryParse(parts[6], out var updatedTicks);
                list.Add(new SearchRecord
                {
                    FullPath = fullPath,
                    Name = name,
                    Folder = folder,
                    Extension = extension,
                    IsDirectory = parts[4] == "1",
                    SizeBytes = sizeBytes,
                    UpdatedTicks = updatedTicks,
                    NameLower = name.ToLowerInvariant(),
                    FullPathLower = fullPath.ToLowerInvariant()
                });
            }
        }
        catch
        {
        }
        return list;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
    private static string Unescape(string value) => value.Replace("\\n", "\n").Replace("\\r", "\r").Replace("\\t", "\t").Replace("\\\\", "\\");
}
