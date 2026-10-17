using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Channels;

namespace DIndex;

/// <summary>
/// A portable directory-enumeration index. Watchers are hints to reconcile current state,
/// not a durable journal. Searches hold one immutable, reusable published snapshot.
/// </summary>
public sealed class FileIndexService : IDisposable
{
    private const int MaximumPendingPaths = 8192;
    private const int MaximumBatchPaths = 512;
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(5);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _buildGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private PublishedSnapshot _snapshot = new(Array.Empty<SearchRecord>(), 0);
    private IndexSession? _active;
    private IndexSession? _building;
    private IndexConfiguration? _snapshotConfiguration;
    private long _nextVersion;
    private bool _disposed;
    private int _cachedOnly;
    private DateTime _nextRecoveryUtc;
    private int _recoveryDelaySeconds = 5;

    public int Count => Volatile.Read(ref _snapshot).Records.Length;
    public long SnapshotVersion => Volatile.Read(ref _snapshot).Version;
    public bool IsDirty => Volatile.Read(ref _cachedOnly) != 0 || Volatile.Read(ref _active)?.IsDirty == true;
    public bool IsRebuilding => Volatile.Read(ref _building) is not null;
    public event Action<int>? CountChanged;
    public event Action<string>? StatusChanged;
    public event Action<long>? SnapshotChanged;

    public void ReplaceAll(IEnumerable<SearchRecord> records, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(settings);
        var configuration = IndexConfiguration.Create(settings);
        ReplaceAllCore(records.Where(record => configuration.AllowsRecord(record)), configuration);
    }

    public void ReplaceAll(IEnumerable<SearchRecord> records) => ReplaceAllCore(records, null);

    private void ReplaceAllCore(IEnumerable<SearchRecord> records, IndexConfiguration? configuration)
    {
        ArgumentNullException.ThrowIfNull(records);
        var items = new Dictionary<string, SearchRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records) items[record.FullPath] = record;
        IndexSession? previous;
        IndexSession? building;
        PublishedSnapshot snapshot;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            previous = _active;
            building = _building;
            _active = null;
            _building = null;
            _snapshotConfiguration = configuration;
            Volatile.Write(ref _cachedOnly, 1);
            snapshot = new PublishedSnapshot(items.Values.ToArray(), ++_nextVersion);
            Volatile.Write(ref _snapshot, snapshot);
        }
        building?.Dispose();
        previous?.Dispose();
        NotifyPublished(snapshot);
    }

    // Returning the same read-only wrapper avoids a million-reference copy on every save/read.
    public IReadOnlyCollection<SearchRecord> Snapshot() => Volatile.Read(ref _snapshot).View;

    public Task RebuildAsync(AppSettings settings, Action<int, string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        return StartRebuild(IndexConfiguration.Create(settings), progress, cancellationToken, null);
    }

    private Task StartRebuild(IndexConfiguration configuration, Action<int, string>? progress,
        CancellationToken cancellationToken, IndexSession? expectedActive)
    {
        IndexSession session;
        IndexSession? previousBuild;
        IndexSession? obsoleteActive = null;
        PublishedSnapshot? filteredSnapshot = null;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (expectedActive is not null && (!ReferenceEquals(_active, expectedActive) || _building is not null))
                return Task.CompletedTask;
            previousBuild = _building;
            if (_snapshotConfiguration is null || !_snapshotConfiguration.SameScope(configuration))
            {
                // Removed roots/exclusions must disappear even if this rebuild is later canceled.
                // Stop the old writer before publishing the narrowed, still-usable cached subset.
                obsoleteActive = _active;
                _active = null;
                _snapshotConfiguration = configuration;
                Volatile.Write(ref _cachedOnly, 1);
                if (_snapshot.Records.Length > 0)
                {
                    filteredSnapshot = new PublishedSnapshot(_snapshot.Records.Where(configuration.AllowsRecord).ToArray(), ++_nextVersion);
                    Volatile.Write(ref _snapshot, filteredSnapshot);
                }
            }
            session = new IndexSession(configuration, CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token), cancellationToken);
            _building = session;
        }
        previousBuild?.Dispose();
        obsoleteActive?.Dispose();
        if (filteredSnapshot is not null) NotifyPublished(filteredSnapshot);
        // Watcher setup and network filesystem calls must never run on the UI thread.
        return Task.Run(() => BuildAndPublishAsync(session, progress));
    }

    private async Task BuildAndPublishAsync(IndexSession session, Action<int, string>? progress)
    {
        var token = session.Token;
        var committed = false;
        var entered = false;
        try
        {
            // Latest request wins, but only one generation allocates/scans at a time. A canceled
            // synchronous network read may take time to return; do not start unbounded replacements.
            await _buildGate.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            token.ThrowIfCancellationRequested();
            // Subscribe before enumeration so writes during the initial scan are replayed.
            ConfigureWatchers(session);
            await ScanAsync(session, session.Configuration.Roots, session.Items, progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            PreserveUnreadableRecords(session);
            await ApplyPendingBatchAsync(session, token).ConfigureAwait(false);
            var records = CopyRecords(session.Items, token);
            PublishedSnapshot snapshot;
            IndexSession? old;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (_disposed || !ReferenceEquals(_building, session)) throw new OperationCanceledException(token);
                session.DetachRequestCancellation();
                token.ThrowIfCancellationRequested();
                old = _active;
                _active = session;
                _building = null;
                _snapshotConfiguration = session.Configuration;
                Volatile.Write(ref _cachedOnly, 0);
                snapshot = new PublishedSnapshot(records, ++_nextVersion);
                Volatile.Write(ref _snapshot, snapshot);
                session.MarkPublished();
                committed = true;
                if (!session.IsDirty)
                {
                    _recoveryDelaySeconds = 5;
                    _nextRecoveryUtc = DateTime.MinValue;
                }
            }
            old?.Dispose();
            NotifyPublished(snapshot);
            NotifyStatus(session, session.IsDirty
                ? $"索引: {records.Length:N0} 件 / 未確認の変更・読取不可の場所があります。自動再確認します。"
                : $"索引完了: {records.Length:N0} 件");
            _ = PumpChangesAsync(session);
        }
        finally
        {
            if (entered) _buildGate.Release();
            if (!committed)
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_building, session)) _building = null;
                }
                session.Dispose();
                session.ReleaseCancellation();
            }
        }
    }

    public List<SearchRecord> Search(string keyword, bool pathSearch, int limit, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var values = Volatile.Read(ref _snapshot).Records;
            keyword = (keyword ?? string.Empty).Trim();
            List<SearchRecord> result;
            if (string.IsNullOrWhiteSpace(keyword))
            {
                result = values.OrderByDescending(x =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return x.UpdatedTicks;
                    }, new CancelableComparer<long>(cancellationToken))
                    .Take(limit).ToList();
            }
            else
            {
                var words = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(x => x.ToLowerInvariant()).ToArray();
                bool IsMatch(SearchRecord item)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = pathSearch ? item.FullPathLower : item.NameLower;
                    foreach (var word in words)
                        if (!target.Contains(word, StringComparison.Ordinal)) return false;
                    return true;
                }
                int Score(SearchRecord item)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = pathSearch ? item.FullPathLower : item.NameLower;
                    var first = target.IndexOf(words[0], StringComparison.Ordinal);
                    var exactBonus = item.NameLower.Equals(words[0], StringComparison.Ordinal) ? -10000 : 0;
                    var prefixBonus = item.NameLower.StartsWith(words[0], StringComparison.Ordinal) ? -5000 : 0;
                    return exactBonus + prefixBonus + first + (item.IsDirectory ? 0 : 30);
                }
                // Preserve the existing AND-substring predicate, score and culture-sensitive name order.
                // Broad-query regressions ruled out unconditional trigram/refinement/top-K replacement.
                var scoreComparer = new CancelableComparer<int>(cancellationToken);
                var nameComparer = new CancelableComparer<string>(cancellationToken);
                result = values.Length > 50000
                    ? values.AsParallel().WithCancellation(cancellationToken).Where(IsMatch)
                        .OrderBy(Score, scoreComparer).ThenBy(x => x.Name, nameComparer).Take(Math.Max(1, limit)).ToList()
                    : values.Where(IsMatch).OrderBy(Score, scoreComparer).ThenBy(x => x.Name, nameComparer)
                        .Take(Math.Max(1, limit)).ToList();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Array/LINQ sorting can wrap a comparer cancellation in InvalidOperationException.
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private sealed class CancelableComparer<T>(CancellationToken token) : IComparer<T>
    {
        public int Compare(T? x, T? y)
        {
            token.ThrowIfCancellationRequested();
            return Comparer<T>.Default.Compare(x!, y!);
        }
    }

    private async Task ScanAsync(IndexSession session, IEnumerable<string> roots,
        ConcurrentDictionary<string, SearchRecord> target, Action<int, string>? progress, CancellationToken token,
        ConcurrentDictionary<string, byte>? directories = null)
    {
        directories ??= session.Directories;
        var queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = false, SingleWriter = false });
        var seen = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var outstanding = 0;
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            if (!session.Configuration.IsInScope(root, directory: true) || !seen.TryAdd(root, 0)) continue;
            if (HasReparseAncestor(root)) continue;
            Interlocked.Increment(ref outstanding);
            queue.Writer.TryWrite(root);
        }
        if (outstanding == 0) queue.Writer.TryComplete();
        long lastProgress = 0;
        var workers = Enumerable.Range(0, Math.Clamp(Environment.ProcessorCount, 2, 8)).Select(_ => Task.Run(async () =>
        {
            await foreach (var directory in queue.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                session.EnterWorker();
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (!TryGetAttributes(directory, out var attributes, out var error))
                    {
                        if (error is not null) session.NoteFailure(directory);
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) == 0) continue;
                    directories.TryAdd(directory, 0);
                    if (session.Configuration.IncludeDirectories) TryStoreRecord(session, target, directory, true);
                    // Index a link itself, but never descend through junctions/symlinks.
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    try
                    {
                        var options = new EnumerationOptions
                        {
                            IgnoreInaccessible = false, RecurseSubdirectories = false, ReturnSpecialDirectories = false,
                            AttributesToSkip = FileAttributes.System | FileAttributes.Temporary
                        };
                        foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", options))
                        {
                            token.ThrowIfCancellationRequested();
                            if (!TryGetAttributes(entry, out var entryAttributes, out var entryError))
                            {
                                if (entryError is not null) session.NoteFailure(entry);
                                continue;
                            }
                            var isDirectory = (entryAttributes & FileAttributes.Directory) != 0;
                            if (isDirectory && session.Configuration.Excludes.Contains(Path.GetFileName(entry))) continue;
                            if (!isDirectory)
                            {
                                TryStoreRecord(session, target, entry, false);
                                continue;
                            }
                            if (!seen.TryAdd(entry, 0)) continue;
                            // Completion counts queued AND running directories. Waiting workers remain
                            // alive while a single root discovers the rest of its directory tree.
                            Interlocked.Increment(ref outstanding);
                            queue.Writer.TryWrite(entry);
                        }
                    }
                    catch (Exception ex) when (IsReadFailure(ex)) { session.NoteFailure(directory); }
                    if (progress is not null)
                    {
                        var now = Stopwatch.GetTimestamp();
                        var last = Interlocked.Read(ref lastProgress);
                        if ((now - last) >= Stopwatch.Frequency / 4 && Interlocked.CompareExchange(ref lastProgress, now, last) == last)
                        {
                            token.ThrowIfCancellationRequested();
                            progress(target.Count, directory);
                        }
                    }
                }
                finally
                {
                    session.LeaveWorker();
                    if (Interlocked.Decrement(ref outstanding) == 0) queue.Writer.TryComplete();
                }
            }
        }, token)).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
    }

    private void ConfigureWatchers(IndexSession session)
    {
        foreach (var root in session.Configuration.Roots)
        {
            session.Token.ThrowIfCancellationRequested();
            if (!session.Configuration.IsInScope(root, true)) continue;
            try
            {
                var attributes = File.GetAttributes(root);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || HasReparseAncestor(root)) continue;
                var watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true, InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                watcher.Created += (_, e) => session.Enqueue(e.FullPath, subtree: true);
                watcher.Changed += (_, e) => session.Enqueue(e.FullPath, subtree: false);
                watcher.Deleted += (_, e) => session.Enqueue(e.FullPath, subtree: true);
                watcher.Renamed += (_, e) => { session.Enqueue(e.OldFullPath, true); session.Enqueue(e.FullPath, true); };
                watcher.Error += (_, _) =>
                {
                    session.MarkNeedsRecovery();
                    NotifyStatus(session, "ファイル監視の変更欠落を検出しました。索引を自動再確認します。");
                };
                session.AddWatcher(watcher, root);
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex) when (IsReadFailure(ex) || ex is ArgumentException || ex is InvalidOperationException)
            {
                session.NoteFailure(root);
            }
        }
    }

    private async Task PumpChangesAsync(IndexSession session)
    {
        var token = session.Token;
        var nextHealth = DateTime.UtcNow + HealthInterval;
        try
        {
            while (true)
            {
                await Task.Delay(UpdateInterval, token).ConfigureAwait(false);
                try
                {
                    var changed = await ApplyPendingBatchAsync(session, token).ConfigureAwait(false);
                    if (changed) PublishUpdates(session, token);
                    if (DateTime.UtcNow >= nextHealth)
                    {
                        session.CheckRoots();
                        nextHealth = DateTime.UtcNow + HealthInterval;
                    }
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    // A transient I/O/permission failure must not kill the only recovery worker.
                    session.MarkNeedsRecovery();
                    NotifyStatus(session, "ファイル監視の再確認が必要です。自動再確認します。");
                }
                if (session.NeedsRecovery) TryStartRecovery(session);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { session.Dispose(); session.ReleaseCancellation(); }
    }

    private async Task<bool> ApplyPendingBatchAsync(IndexSession session, CancellationToken token)
    {
        var changes = session.TakeBatch(MaximumBatchPaths);
        if (changes.Length == 0) return false;
        try
        {
            foreach (var change in changes)
            {
                token.ThrowIfCancellationRequested();
                var path = change.Key;
                if (!TryGetAttributes(path, out var attributes, out var error))
                {
                    if (error is null) RemoveSubtree(session, path, token);
                    else session.NoteFailure(path); // Access denied is not evidence of deletion.
                    continue;
                }
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if (!session.Configuration.IsInScope(path, isDirectory) || HasReparseAncestor(path)
                    || (attributes & (FileAttributes.System | FileAttributes.Temporary)) != 0)
                {
                    RemoveSubtree(session, path, token);
                    continue;
                }
                if (!isDirectory)
                {
                    if (change.Value) RemoveSubtree(session, path, token);
                    TryStoreRecord(session, session.Items, path, false);
                }
                else if (change.Value)
                {
                    var subtree = new ConcurrentDictionary<string, SearchRecord>(StringComparer.OrdinalIgnoreCase);
                    var directories = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
                    var failuresBefore = session.FailureVersion;
                    await ScanAsync(session, new[] { path }, subtree, null, token, directories).ConfigureAwait(false);
                    // A partial/denied enumeration must not delete previously readable descendants.
                    if (session.FailureVersion == failuresBefore) RemoveSubtree(session, path, token);
                    foreach (var directory in directories) session.Directories.TryAdd(directory.Key, 0);
                    foreach (var item in subtree)
                    {
                        token.ThrowIfCancellationRequested();
                        session.Items[item.Key] = item.Value;
                    }
                }
                else if (session.Configuration.IncludeDirectories) TryStoreRecord(session, session.Items, path, true);
            }
            return true;
        }
        finally { session.FinishBatch(); }
    }

    private void PublishUpdates(IndexSession session, CancellationToken token)
    {
        var records = CopyRecords(session.Items, token);
        PublishedSnapshot snapshot;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_active, session) || token.IsCancellationRequested) return;
            snapshot = new PublishedSnapshot(records, ++_nextVersion);
            Volatile.Write(ref _snapshot, snapshot);
            session.MarkPublished();
        }
        NotifyPublished(snapshot);
    }

    private void TryStartRecovery(IndexSession session)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_active, session) || _building is not null) return;
            if (DateTime.UtcNow < _nextRecoveryUtc) return;
            _nextRecoveryUtc = DateTime.UtcNow.AddSeconds(_recoveryDelaySeconds);
            _recoveryDelaySeconds = Math.Min(120, _recoveryDelaySeconds * 2);
        }
        _ = RecoverAsync(session);
    }

    private async Task RecoverAsync(IndexSession session)
    {
        try
        {
            // Do not link the new generation to the old session: committing it cancels the old one.
            await StartRebuild(session.Configuration, null, _lifetime.Token, session).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsReadFailure(ex) || ex is ObjectDisposedException)
        {
            NotifyStatus(session, "自動再確認できませんでした。検索場所の接続・権限を確認してください。");
        }
    }

    private void PreserveUnreadableRecords(IndexSession session)
    {
        if (!session.HasFailures) return;
        foreach (var record in Volatile.Read(ref _snapshot).Records)
        {
            session.Token.ThrowIfCancellationRequested();
            if (session.Configuration.IsInScope(record.FullPath, record.IsDirectory)
                && (!record.IsDirectory || session.Configuration.IncludeDirectories)
                && session.CouldNotRead(record.FullPath))
            {
                session.Items.TryAdd(record.FullPath, record);
                for (var directory = record.IsDirectory ? record.FullPath : record.Folder;
                    !string.IsNullOrEmpty(directory) && session.Configuration.IsInScope(directory, true);
                    directory = Path.GetDirectoryName(directory)) session.Directories.TryAdd(directory, 0);
            }
        }
    }

    private static void TryStoreRecord(IndexSession session, ConcurrentDictionary<string, SearchRecord> target, string path, bool directory)
    {
        session.Token.ThrowIfCancellationRequested();
        try
        {
            var record = directory ? SearchRecord.FromDirectory(path) : SearchRecord.FromFile(path);
            // Metadata factories can return empty metadata for a vanished entry. Its watcher event
            // will reconcile any recreation; do not knowingly insert a phantom in this generation.
            if (directory ? Directory.Exists(path) : File.Exists(path)) target[record.FullPath] = record;
        }
        catch (Exception ex) when (IsReadFailure(ex)) { session.NoteFailure(path); }
    }

    private static SearchRecord[] CopyRecords(ConcurrentDictionary<string, SearchRecord> items, CancellationToken token)
    {
        // Only the scan (before publication) or one update pump writes this session at a time.
        var result = new SearchRecord[items.Count];
        var i = 0;
        foreach (var item in items)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            if (i == result.Length) Array.Resize(ref result, Math.Max(4, i * 2));
            result[i++] = item.Value;
        }
        if (i != result.Length) Array.Resize(ref result, i);
        token.ThrowIfCancellationRequested();
        return result;
    }

    private static void RemoveSubtree(IndexSession session, string path, CancellationToken token)
    {
        // Ordinary file notifications are O(1). Only known directory operations scan descendants.
        var directory = session.Directories.ContainsKey(path)
            || session.Items.TryGetValue(path, out var known) && known.IsDirectory;
        session.Items.TryRemove(path, out _);
        if (!directory) return;
        foreach (var item in session.Items)
        {
            token.ThrowIfCancellationRequested();
            if (IsPathOrDescendant(item.Key, path)) session.Items.TryRemove(item.Key, out _);
        }
        foreach (var item in session.Directories)
        {
            token.ThrowIfCancellationRequested();
            if (IsPathOrDescendant(item.Key, path)) session.Directories.TryRemove(item.Key, out _);
        }
    }

    private static bool IsPathOrDescendant(string path, string parent)
    {
        if (path.Equals(parent, StringComparison.OrdinalIgnoreCase)) return true;
        if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) return false;
        return parent.Length > 0 && (IsSeparator(parent[^1]) || path.Length > parent.Length && IsSeparator(path[parent.Length]));
    }

    private static bool IsSeparator(char value) => value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
    private static bool IsReadFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static bool TryGetAttributes(string path, out FileAttributes attributes, out Exception? error)
    {
        try { attributes = File.GetAttributes(path); error = null; return true; }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            attributes = default;
            error = ex is FileNotFoundException or DirectoryNotFoundException ? null : ex;
            return false;
        }
    }

    private static bool HasReparseAncestor(string path)
    {
        for (var parent = Path.GetDirectoryName(path); !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if (TryGetAttributes(parent, out var attributes, out _) && (attributes & FileAttributes.ReparsePoint) != 0) return true;
        }
        return false;
    }

    private void NotifyPublished(PublishedSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot, Volatile.Read(ref _snapshot))) return;
        NotifySafely(CountChanged, snapshot.Records.Length);
        NotifySafely(SnapshotChanged, snapshot.Version);
    }

    private void NotifyStatus(IndexSession session, string message)
    {
        if (!session.Token.IsCancellationRequested && (ReferenceEquals(session, Volatile.Read(ref _active)) || ReferenceEquals(session, Volatile.Read(ref _building))))
            NotifySafely(StatusChanged, message);
    }

    private static void NotifySafely<T>(Action<T>? subscribers, T value)
    {
        if (subscribers is null) return;
        foreach (Action<T> subscriber in subscribers.GetInvocationList())
        {
            try { subscriber(value); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // UI shutdown/a faulty subscriber must not strand a committed index without a pump.
                Debug.WriteLine($"Index notification failed: {ex}");
            }
        }
    }

    public static void Open(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }
    public static void OpenParent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(target)) Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{target}\"", UseShellExecute = true });
    }
    public static void CopyPath(string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) System.Windows.Clipboard.SetText(path);
    }

    public void Dispose()
    {
        IndexSession? active;
        IndexSession? building;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            active = _active;
            building = _building;
            _active = null;
            _building = null;
        }
        _lifetime.Cancel();
        active?.Dispose();
        building?.Dispose();
        _lifetime.Dispose();
        // Session sources are released by their scan/pump finally blocks after readers finish.
    }

    private sealed class PublishedSnapshot(SearchRecord[] records, long version)
    {
        public SearchRecord[] Records { get; } = records;
        public ReadOnlyCollection<SearchRecord> View { get; } = Array.AsReadOnly(records);
        public long Version { get; } = version;
    }

    private sealed class IndexConfiguration(string[] roots, HashSet<string> excludes, bool includeDirectories)
    {
        public string[] Roots { get; } = roots;
        public HashSet<string> Excludes { get; } = excludes;
        public bool IncludeDirectories { get; } = includeDirectories;
        public static IndexConfiguration Create(AppSettings settings)
        {
            var roots = settings.SearchRoots.Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => Path.TrimEndingDirectorySeparator(Path.GetFullPath(x)))
                .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Length).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
            // One watcher/scan per covered tree; overlapping roots do not multiply events.
            roots = roots.Where((root, i) => !roots.Take(i).Any(parent => IsPathOrDescendant(root, parent))).ToArray();
            return new IndexConfiguration(roots, settings.ExcludeFolderNames.Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase), settings.IncludeDirectories);
        }
        public bool SameScope(IndexConfiguration other) => IncludeDirectories == other.IncludeDirectories
            && Roots.SequenceEqual(other.Roots, StringComparer.OrdinalIgnoreCase) && Excludes.SetEquals(other.Excludes);
        public bool AllowsRecord(SearchRecord record) => (!record.IsDirectory || IncludeDirectories) && IsInScope(record.FullPath, record.IsDirectory);
        public bool IsInScope(string path, bool directory)
        {
            if (!Roots.Any(root => IsPathOrDescendant(path, root))) return false;
            var ancestor = directory ? path : Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(ancestor))
            {
                var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(ancestor));
                if (Excludes.Contains(name)) return false;
                ancestor = Path.GetDirectoryName(ancestor);
            }
            return true;
        }
        public AppSettings ToSettings() => new() { SearchRoots = Roots.ToList(), ExcludeFolderNames = Excludes.ToList(), IncludeDirectories = IncludeDirectories };
    }

    private sealed class IndexSession(IndexConfiguration configuration, CancellationTokenSource cancellation, CancellationToken requestToken) : IDisposable
    {
        private readonly object _lock = new();
        private readonly CancellationTokenRegistration _requestRegistration = requestToken.Register(cancellation.Cancel);
        private int _released;
        private readonly Dictionary<string, bool> _pending = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(FileSystemWatcher Watcher, string Root, long CreationTicks)> _watchers = new();
        private readonly ConcurrentDictionary<string, byte> _failedPaths = new(StringComparer.OrdinalIgnoreCase);
        private int _needsRecovery;
        private int _failureVersion;
        private int _activeWorkers;
        private int _peakWorkers;
        private bool _applying;
        private bool _unpublished;
        private bool _disposed;
        public IndexConfiguration Configuration { get; } = configuration;
        public CancellationToken Token { get; } = cancellation.Token;
        public ConcurrentDictionary<string, SearchRecord> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
        public ConcurrentDictionary<string, byte> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int PeakWorkerCount => Volatile.Read(ref _peakWorkers);
        public void EnterWorker()
        {
            var active = Interlocked.Increment(ref _activeWorkers);
            int peak;
            while ((peak = Volatile.Read(ref _peakWorkers)) < active && Interlocked.CompareExchange(ref _peakWorkers, active, peak) != peak) { }
        }
        public void LeaveWorker() => Interlocked.Decrement(ref _activeWorkers);
        public int FailureVersion => Volatile.Read(ref _failureVersion);
        public bool HasFailures => !_failedPaths.IsEmpty;
        public bool NeedsRecovery => Volatile.Read(ref _needsRecovery) != 0;
        public bool IsDirty { get { lock (_lock) return NeedsRecovery || _pending.Count > 0 || _applying || _unpublished; } }
        public void NoteFailure(string path)
        {
            // Bound failure tracking too: once saturated, preserve records under configured roots.
            if (_failedPaths.Count < 1024) _failedPaths.TryAdd(path, 0);
            else foreach (var root in Configuration.Roots) _failedPaths.TryAdd(root, 0);
            Interlocked.Increment(ref _failureVersion);
            MarkNeedsRecovery();
        }
        public bool CouldNotRead(string path) => _failedPaths.Keys.Any(failed => IsPathOrDescendant(path, failed));
        public void MarkNeedsRecovery() => Interlocked.Exchange(ref _needsRecovery, 1);
        public void Enqueue(string path, bool subtree)
        {
            if (Token.IsCancellationRequested) return;
            lock (_lock)
            {
                if (_disposed) return;
                if (_pending.TryGetValue(path, out var previous)) _pending[path] = subtree || previous;
                else if (_pending.Count < MaximumPendingPaths) _pending.Add(path, subtree);
                else MarkNeedsRecovery();
            }
        }
        public KeyValuePair<string, bool>[] TakeBatch(int maximum)
        {
            lock (_lock)
            {
                var batch = _pending.Take(maximum).ToArray();
                foreach (var change in batch) _pending.Remove(change.Key);
                _applying = batch.Length > 0;
                _unpublished |= _applying;
                return batch;
            }
        }
        public void FinishBatch() { lock (_lock) _applying = false; }
        public void MarkPublished() { lock (_lock) _unpublished = false; }
        public void AddWatcher(FileSystemWatcher watcher, string root)
        {
            lock (_lock)
            {
                if (_disposed) { watcher.Dispose(); Token.ThrowIfCancellationRequested(); return; }
                _watchers.Add((watcher, root, Directory.GetCreationTimeUtc(root).Ticks));
            }
        }
        public void CheckRoots()
        {
            lock (_lock)
            {
                foreach (var (_, root, creationTicks) in _watchers)
                {
                    if (!Directory.Exists(root) || Directory.GetCreationTimeUtc(root).Ticks != creationTicks) MarkNeedsRecovery();
                }
            }
        }
        public void DetachRequestCancellation() => _requestRegistration.Dispose();
        public void ReleaseCancellation()
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            _requestRegistration.Dispose();
            cancellation.Dispose();
        }
        public void Dispose()
        {
            List<FileSystemWatcher> watchers;
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                cancellation.Cancel();
                watchers = _watchers.Select(x => x.Watcher).ToList();
                _watchers.Clear();
                _pending.Clear();
            }
            foreach (var watcher in watchers) watcher.Dispose();
        }
    }
}
