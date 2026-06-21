using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;

namespace DIndex;

public sealed class FileIndexService : IDisposable
{
    private readonly ConcurrentDictionary<string, SearchRecord> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _watcherLock = new();
    private readonly EnumerationOptions _directoryOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.System | FileAttributes.Temporary
    };

    public int Count => _items.Count;
    public event Action<int>? CountChanged;
    public event Action<string>? StatusChanged;

    public void ReplaceAll(IEnumerable<SearchRecord> records)
    {
        _items.Clear();
        foreach (var record in records)
        {
            _items[record.FullPath] = record;
        }
        CountChanged?.Invoke(_items.Count);
    }

    public IReadOnlyCollection<SearchRecord> Snapshot() => _items.Values.ToArray();

    public async Task RebuildAsync(AppSettings settings, Action<int, string>? progress, CancellationToken cancellationToken)
    {
        _items.Clear();
        DisposeWatchers();

        var excludes = settings.ExcludeFolderNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = settings.SearchRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        await Task.Run(() =>
        {
            var pending = new ConcurrentQueue<string>();
            var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                pending.Enqueue(root);
                seen.TryAdd(root, 0);
                if (settings.IncludeDirectories) TryAddDirectory(root);
            }

            var workers = Math.Max(2, Math.Min(Environment.ProcessorCount, 8));
            var tasks = Enumerable.Range(0, workers).Select(_ => Task.Run(() =>
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (!pending.TryDequeue(out var directory))
                    {
                        if (pending.IsEmpty) break;
                        Thread.Yield();
                        continue;
                    }

                    if (ShouldSkipDirectory(directory, excludes)) continue;

                    try
                    {
                        foreach (var sub in Directory.EnumerateDirectories(directory, "*", _directoryOptions))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (ShouldSkipDirectory(sub, excludes)) continue;
                            if (!seen.TryAdd(sub, 0)) continue;
                            if (settings.IncludeDirectories) TryAddDirectory(sub);
                            pending.Enqueue(sub);
                        }
                    }
                    catch
                    {
                    }

                    try
                    {
                        foreach (var file in Directory.EnumerateFiles(directory, "*", _directoryOptions))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            TryAddFile(file);
                        }
                    }
                    catch
                    {
                    }

                    var count = _items.Count;
                    if (count % 1000 == 0)
                    {
                        progress?.Invoke(count, directory);
                    }
                }
            }, cancellationToken)).ToArray();

            Task.WaitAll(tasks);
        }, cancellationToken);

        ConfigureWatchers(settings, excludes);
        CountChanged?.Invoke(_items.Count);
        StatusChanged?.Invoke($"索引完了: {_items.Count:N0} 件");
    }

    public List<SearchRecord> Search(string keyword, bool pathSearch, int limit, CancellationToken cancellationToken)
    {
        keyword = (keyword ?? string.Empty).Trim();
        var values = _items.Values;

        if (string.IsNullOrWhiteSpace(keyword))
        {
            return values.OrderByDescending(x => x.UpdatedTicks).Take(limit).ToList();
        }

        var words = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                           .Select(x => x.ToLowerInvariant())
                           .ToArray();

        bool IsMatch(SearchRecord item)
        {
            var target = pathSearch ? item.FullPathLower : item.NameLower;
            for (var i = 0; i < words.Length; i++)
            {
                if (!target.Contains(words[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        int Score(SearchRecord item)
        {
            var target = pathSearch ? item.FullPathLower : item.NameLower;
            var first = target.IndexOf(words[0], StringComparison.Ordinal);
            var exactBonus = item.NameLower.Equals(words[0], StringComparison.Ordinal) ? -10000 : 0;
            var prefixBonus = item.NameLower.StartsWith(words[0], StringComparison.Ordinal) ? -5000 : 0;
            var dirPenalty = item.IsDirectory ? 0 : 30;
            return exactBonus + prefixBonus + first + dirPenalty;
        }

        if (values.Count > 50000)
        {
            return values.AsParallel()
                         .WithCancellation(cancellationToken)
                         .Where(IsMatch)
                         .OrderBy(Score)
                         .ThenBy(x => x.Name)
                         .Take(Math.Max(1, limit))
                         .ToList();
        }

        return values.Where(IsMatch)
                     .OrderBy(Score)
                     .ThenBy(x => x.Name)
                     .Take(Math.Max(1, limit))
                     .ToList();
    }

    public static void Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    public static void OpenParent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(target)) return;
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{target}\"", UseShellExecute = true });
    }

    public static void CopyPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) System.Windows.Clipboard.SetText(path);
    }

    private void ConfigureWatchers(AppSettings settings, IReadOnlySet<string> excludes)
    {
        DisposeWatchers();
        foreach (var root in settings.SearchRoots.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Created += (_, e) => ApplyChange(e.FullPath, settings.IncludeDirectories, excludes);
                watcher.Changed += (_, e) => ApplyChange(e.FullPath, settings.IncludeDirectories, excludes);
                watcher.Deleted += (_, e) => RemovePath(e.FullPath);
                watcher.Renamed += (_, e) => { RemovePath(e.OldFullPath); ApplyChange(e.FullPath, settings.IncludeDirectories, excludes); };
                watcher.Error += (_, _) => StatusChanged?.Invoke("監視バッファが溢れた可能性があります。必要に応じて再索引してください。");
                lock (_watcherLock) _watchers.Add(watcher);
            }
            catch
            {
            }
        }
    }

    private void ApplyChange(string path, bool includeDirectories, IReadOnlySet<string> excludes)
    {
        try
        {
            if (ShouldSkipDirectory(path, excludes)) return;
            if (Directory.Exists(path))
            {
                if (includeDirectories) TryAddDirectory(path);
                return;
            }
            if (File.Exists(path)) TryAddFile(path);
        }
        catch
        {
        }
    }

    private void RemovePath(string path)
    {
        _items.TryRemove(path, out _);
        CountChanged?.Invoke(_items.Count);
    }

    private void TryAddDirectory(string path)
    {
        try { _items[path] = SearchRecord.FromDirectory(path); CountChanged?.Invoke(_items.Count); } catch { }
    }

    private void TryAddFile(string path)
    {
        try { _items[path] = SearchRecord.FromFile(path); CountChanged?.Invoke(_items.Count); } catch { }
    }

    private static bool ShouldSkipDirectory(string path, IReadOnlySet<string> excludes)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return !string.IsNullOrWhiteSpace(name) && excludes.Contains(name);
    }

    private void DisposeWatchers()
    {
        lock (_watcherLock)
        {
            foreach (var watcher in _watchers)
            {
                try { watcher.EnableRaisingEvents = false; watcher.Dispose(); } catch { }
            }
            _watchers.Clear();
        }
    }

    public void Dispose() => DisposeWatchers();
}
