using System.Diagnostics;
using System.IO;
using System.Reflection;
using DIndex;

internal static class ProgressiveIndexTests
{
    public static Task FixturePathsAreCanonical()
    {
        using var tree = new Tree();
        // '/' is accepted on Windows, while '..' also exposes unnormalized fixture paths on Unix.
        var path = tree.Write("nested/../nested/leaf.txt");
        var expected = Path.GetFullPath(Path.Combine(tree.Root, "nested", "leaf.txt"));
        Equal(new[] { expected }, new[] { path }, "Fixture paths must be canonical on every platform");
        Equal(new[] { SearchRecord.FromFile(path).FullPath }, new[] { path }, "Fixture and indexed record must use the same path representation");
        return Task.CompletedTask;
    }

    public static async Task SearchableBeforeCompletion()
    {
        using var tree = new Tree();
        for (var i = 0; i < 1400; i++) tree.Write($"early-{i:D4}.txt");
        using var service = new FileIndexService();
        using var blocked = new BlockedScan();
        var notifications = 0;
        var firstPublication = new TaskCompletionSource<(IReadOnlyCollection<SearchRecord> Snapshot, int SearchCount, bool IsDirty, bool IsRebuilding)>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SnapshotChanged += _ =>
        {
            Interlocked.Increment(ref notifications);
            if (!firstPublication.Task.IsCompleted && service.Count > 0 && service.IsRebuilding)
                firstPublication.TrySetResult((service.Snapshot(), service.Search("early-", false, 2000, CancellationToken.None).Count,
                    service.IsDirty, service.IsRebuilding));
        };
        service.CountChanged += _ => throw new InvalidOperationException("Faulty preview subscriber");
        service.SnapshotChanged += _ => throw new IOException("Faulty snapshot subscriber");
        var build = service.RebuildAsync(tree.Settings(), blocked.Progress, CancellationToken.None);
        await blocked.WaitEntered();
        var firstResult = await firstPublication.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var first = firstResult.Snapshot;
        var firstCount = first.Count;
        Assert(firstCount > 0 && firstCount <= 512 && !build.IsCompleted && firstResult.IsRebuilding && firstResult.IsDirty,
            "First preview is nonempty, bounded, and explicitly incomplete");
        Assert(firstResult.SearchCount == firstCount, "Search uses the exact first preview before scan completion");
        // Slow filesystems can reach the first timer before 512 files are discovered. Require
        // the documented growth threshold, not an assumed speed or a full-scan preview.
        await Until(() => service.Count >= Math.Max(512, 2 * firstCount) && Volatile.Read(ref notifications) >= 2,
            "a geometrically expanded preview includes more discovered files");
        Assert(!build.IsCompleted && first.Count == firstCount, "Expansion preserves immutable older readers");
        Assert(notifications >= 2 && notifications <= 3, "At most three geometric previews for the fixed 1400-file fixture");
        var building = Session(service, "_building");
        var deleted = first.First().FullPath;
        File.Delete(deleted);
        var updated = service.Snapshot().First(x => x.FullPath != deleted).FullPath;
        File.WriteAllText(updated, new string('x', 73));
        var created = tree.Write("created-during-scan.txt", "fresh");
        Enqueue(building, deleted); Enqueue(building, updated); Enqueue(building, created);
        blocked.Release(); await build;
        Equal(Directory.EnumerateFiles(tree.Root).Order(), service.Snapshot().Select(x => x.FullPath).Order(), "Final reconciliation equals current filesystem");
        Assert(service.Snapshot().Single(x => x.FullPath == updated).SizeBytes == 73, "Final metadata includes watcher changes during preview");
        Assert(service.Count == service.Snapshot().Select(x => x.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "No duplicate paths in final snapshot");
        await IndexStateAssertions.WaitForCleanStaticSnapshot(service, Directory.EnumerateFiles(tree.Root), "preview final clean state");
        Assert(!service.IsRebuilding && first.Count == firstCount, "Completed state and old snapshot remain correct");
    }

    public static async Task CancelRestoresBaseline()
    {
        using var tree = new Tree();
        var retained = tree.Write("retained.txt");
        for (var i = 0; i < 1100; i++) tree.Write($"new-{i}.txt");
        using var service = new FileIndexService();
        service.ReplaceAll(new[] { SearchRecord.FromFile(retained) }, tree.InitialDirectorySettings());
        using var canceled = new CancellationTokenSource();
        using var blocked = new BlockedScan();
        var build = service.RebuildAsync(tree.Settings(), blocked.Progress, canceled.Token);
        await blocked.WaitEntered();
        await Until(() => service.Count > 1, "preview before cancellation");
        Assert(Has(service, retained), "Previously searchable records remain during preview");
        var previewVersion = service.SnapshotVersion;
        canceled.Cancel(); blocked.Release(); await Canceled(build);
        Equal(new[] { retained }, service.Snapshot().Select(x => x.FullPath), "Cancellation restores the cached baseline");
        Assert(service.SnapshotVersion > previewVersion && !service.IsRebuilding && service.IsDirty, "Rollback has a new version and retains unverified cache state");
    }

    public static async Task SupersededPreviewAndScope()
    {
        using var old = new Tree(); using var current = new Tree();
        for (var i = 0; i < 1100; i++) old.Write($"old-{i}.txt");
        var latest = current.Write("allowed/latest.txt"); current.Write("skip/hidden.txt");
        using var service = new FileIndexService(); using var blocked = new BlockedScan();
        var stale = service.RebuildAsync(old.Settings(), blocked.Progress, CancellationToken.None);
        await blocked.WaitEntered(); await Until(() => service.Count > 0, "old preview");
        var build = service.RebuildAsync(current.Settings("skip"), null, CancellationToken.None);
        Assert(service.Snapshot().All(x => x.FullPath.StartsWith(current.Root, StringComparison.Ordinal)), "Scope changes hide the previous preview immediately");
        blocked.Release(); await build; await Canceled(stale);
        await IndexStateAssertions.WaitForCleanStaticSnapshot(service, new[] { latest }, "new scope after superseding preview");
        var published = service.Snapshot(); var publishedPaths = published.Select(x => x.FullPath).ToArray();
        var version = service.SnapshotVersion; var request = service.RequestVersion; var active = Session(service, "_active");
        await Task.Delay(1200);
        await IndexStateAssertions.WaitForCleanStaticSnapshot(service, new[] { latest }, "new scope after stale-publisher window");
        Equal(new[] { latest }, service.Snapshot().Select(x => x.FullPath), "New scope excludes forbidden folders and all old paths");
        Assert(ReferenceEquals(active, Session(service, "_active")) && request == service.RequestVersion && !service.IsRebuilding,
            "Stale workers cannot replace the current active/request generation");
        Assert(service.SnapshotVersion >= version, "Snapshot versions remain monotonic across valid watcher publications");
        Equal(publishedPaths, published.Select(x => x.FullPath), "Old reader stays immutable across same-generation watcher publications");
    }

    public static async Task SupersededSameScopeCancellation()
    {
        using var tree = new Tree(); var retained = tree.Write("cached.txt");
        for (var i = 0; i < 1100; i++) tree.Write($"new-{i}.txt");
        using var service = new FileIndexService();
        service.ReplaceAll(new[] { SearchRecord.FromFile(retained) }, tree.InitialDirectorySettings());
        using var blocked = new BlockedScan();
        var stale = service.RebuildAsync(tree.Settings(), blocked.Progress, CancellationToken.None);
        await blocked.WaitEntered(); await Until(() => service.Count > 1, "preview to supersede");
        using var cancellation = new CancellationTokenSource();
        var current = service.RebuildAsync(tree.Settings(), null, cancellation.Token);
        cancellation.Cancel(); await Canceled(current);
        Equal(new[] { retained }, service.Snapshot().Select(x => x.FullPath), "Queued cancellation restores baseline, never promotes the old partial preview");
        blocked.Release(); await Canceled(stale);
        Equal(new[] { retained }, service.Snapshot().Select(x => x.FullPath), "Late old cleanup cannot replace the restored baseline");
    }

    public static async Task UnreadableFallbackAndScope()
    {
        using var tree = new Tree(); using var removed = new Tree();
        var keep = tree.Write("known.txt"); var denied = tree.Write("denied/keep.txt");
        var excluded = tree.Write("skip/hidden.txt"); var foreign = removed.Write("foreign.txt");
        using var service = new FileIndexService();
        var initial = tree.Settings(); initial.SearchRoots.Add(removed.Root);
        service.ReplaceAll(new[] { keep, denied, excluded, foreign }.Select(SearchRecord.FromFile), initial);
        Directory.Delete(Path.Combine(tree.Root, "denied"), true);
        for (var i = 0; i < 1100; i++) tree.Write($"new-{i}.txt");
        using var blocked = new BlockedScan();
        var finalPublication = new TaskCompletionSource<(IReadOnlyCollection<SearchRecord> Snapshot, bool IsDirty, bool IsRebuilding)>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.SnapshotChanged += _ =>
        {
            if (!service.IsRebuilding) finalPublication.TrySetResult((service.Snapshot(), service.IsDirty, service.IsRebuilding));
        };
        var build = service.RebuildAsync(tree.Settings("skip"), (count, directory) =>
        {
            // The existing suite exercises real Unix access denial where supported. Inject the
            // same failure marker here so preservation during previews is deterministic on Windows too.
            Session(service, "_building").GetType().GetMethod("NoteFailure")!.Invoke(Session(service, "_building"), new object[] { Path.Combine(tree.Root, "denied") });
            blocked.Progress(count, directory);
        }, CancellationToken.None);
        Assert(!Has(service, excluded) && !Has(service, foreign), "Scope narrowing clears excluded cached records immediately");
        await blocked.WaitEntered(); await Until(() => service.Count > 2, "filtered preview");
        Assert(Has(service, keep) && Has(service, denied), "Preview preserves all allowed cached records, including unreadable fallback");
        Assert(!Has(service, excluded) && !Has(service, foreign), "Preview cannot leak excluded records");
        Assert(service.Count == service.Snapshot().Select(x => x.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Merge deduplicates cached and discovered paths");
        blocked.Release(); await build;
        var final = await finalPublication.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(final.Snapshot.Any(x => x.FullPath == denied) && final.IsDirty && !final.IsRebuilding,
            "Final unreadable fallback survives previews and remains dirty before automatic retry");
        Assert(!final.Snapshot.Any(x => x.FullPath == excluded || x.FullPath == foreign), "Final fallback still enforces the narrowed scope");
    }

    public static async Task FailedScanRestoresBaseline()
    {
        using var tree = new Tree(); var keep = tree.Write("keep.txt");
        for (var i = 0; i < 1100; i++) tree.Write($"new-{i}.txt");
        using var service = new FileIndexService();
        service.ReplaceAll(new[] { SearchRecord.FromFile(keep) }, tree.InitialDirectorySettings());
        using var blocked = new BlockedScan();
        var build = service.RebuildAsync(tree.Settings(), (count, directory) =>
        {
            blocked.Progress(count, directory);
            throw new IOException("Injected scan callback failure after preview");
        }, CancellationToken.None);
        await blocked.WaitEntered(); await Until(() => service.Count > 1, "preview before scan failure");
        blocked.Release();
        try { await build; throw new Exception("Expected injected failure"); } catch (IOException) { }
        Equal(new[] { keep }, service.Snapshot().Select(x => x.FullPath), "Failed scan restores baseline");
        Assert(!service.IsRebuilding && service.IsDirty, "Failure does not strand a partial generation or mark cache clean");
    }

    public static async Task CanceledCacheSupersedesPreview()
    {
        using var tree = new Tree(); var keep = tree.Write("keep.txt");
        for (var i = 0; i < 1100; i++) tree.Write($"new-{i}.txt");
        using var service = new FileIndexService();
        service.ReplaceAll(new[] { SearchRecord.FromFile(keep) }, tree.InitialDirectorySettings());
        using var blocked = new BlockedScan();
        var build = service.RebuildAsync(tree.Settings(), blocked.Progress, CancellationToken.None);
        await blocked.WaitEntered(); await Until(() => service.Count > 1, "preview before cache supersession");
        using var cancellation = new CancellationTokenSource();
        IEnumerable<SearchRecord> CanceledRecords()
        {
            cancellation.Cancel();
            yield return SearchRecord.FromFile(keep);
        }
        var restore = service.ReplaceAllAsync(CanceledRecords(), tree.Settings(), service.RequestVersion, cancellation.Token);
        await Canceled(restore);
        Equal(new[] { keep }, service.Snapshot().Select(x => x.FullPath), "Canceled cache restore cannot leave a canceled build preview published");
        blocked.Release(); await Canceled(build);
        Assert(!service.IsRebuilding && service.IsDirty, "Prior cache remains unverified");
    }

    public static async Task ActiveWatcherCannotReplacePreview()
    {
        using var tree = new Tree();
        using var service = new FileIndexService();
        await service.RebuildAsync(tree.Settings(), null, CancellationToken.None);
        await IndexStateAssertions.WaitForCleanStaticSnapshot(service, Array.Empty<string>(), "empty active baseline");
        var active = Session(service, "_active");
        using var cancellation = new CancellationTokenSource(); using var blocked = new BlockedScan();
        string old; long version; Task build;
        // Make file creation + rebuild admission atomic relative to the old pump's publication.
        lock (typeof(FileIndexService).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!)
        {
            old = tree.Write("old.txt"); // Empty published baseline still needs an early preview.
            version = service.SnapshotVersion;
            build = service.RebuildAsync(tree.Settings(), blocked.Progress, cancellation.Token);
        }
        await blocked.WaitEntered();
        await Until(() => service.SnapshotVersion > version, "active generation preview");
        var preview = service.Snapshot();
        Assert(IndexStateAssertions.Capture(service).IsPartial, "The observed publication is a real rebuild preview");
        File.Delete(old); var added = tree.Write("after-preview.txt");
        Enqueue(active, old); Enqueue(active, added);
        await Until(() => (bool)active.GetType().GetProperty("HasUnpublishedChanges")!.GetValue(active)!, "old pump stages its watcher updates");
        Assert(ReferenceEquals(preview, service.Snapshot()), "Old active watcher cannot overwrite an in-progress preview");
        cancellation.Cancel(); blocked.Release(); await Canceled(build);
        await Until(() => Has(service, added) && !Has(service, old) && !service.IsDirty, "old pump publishes staged changes after cancellation, without another event");
    }

    public static async Task SameScopeBaselineKeepsWatcherLive()
    {
        using var tree = new Tree(); var keep = tree.Write("keep.txt");
        using var service = new FileIndexService();
        await service.RebuildAsync(tree.Settings(), null, CancellationToken.None);
        await IndexStateAssertions.WaitForCleanStaticSnapshot(service, new[] { keep }, "nonempty active baseline");
        var active = Session(service, "_active"); var baseline = service.Snapshot();
        var partialPublications = 0;
        service.SnapshotChanged += _ => { if (IndexStateAssertions.Capture(service).IsPartial) Interlocked.Increment(ref partialPublications); };
        using var blocked = new BlockedScan();
        var build = service.RebuildAsync(tree.Settings(), blocked.Progress, CancellationToken.None);
        await blocked.WaitEntered();
        Assert(!(bool)Session(service, "_building").GetType().GetProperty("CanPublishPreviews")!.GetValue(Session(service, "_building"))!, "Nonempty unchanged-scope baseline skips previews");
        await Task.Delay(600);
        Assert(partialPublications == 0 && !IndexStateAssertions.Capture(service).IsPartial, "Unchanged scope avoids redundant previews while allowing legitimate watcher publications");
        Equal(new[] { keep }, service.Snapshot().Select(x => x.FullPath), "Static same-scope contents stay exact");
        var added = tree.Write("live-during-rebuild.txt");
        Enqueue(active, added); Enqueue(Session(service, "_building"), added);
        await Until(() => Has(service, added), "existing active watcher stays live during same-scope scan");
        Assert(!build.IsCompleted && Has(service, keep) && service.IsDirty && service.IsRebuilding, "Live old results do not imply the new scan is complete");
        blocked.Release(); await build;
        await IndexStateAssertions.WaitForCleanStaticSnapshot(service, new[] { added, keep }, "same-scope final clean state");
        Equal(new[] { added, keep }.Order(), service.Snapshot().Select(x => x.FullPath).Order(), "Same-scope final result reconciles both files");
        Equal(new[] { keep }, baseline.Select(x => x.FullPath), "Earlier same-scope reader remains immutable");
        Assert(partialPublications == 0, "Same-scope refresh never emits partial previews");
    }

    public static async Task ExpandedScopeStillPublishesEarly()
    {
        using var retained = new Tree(); using var added = new Tree();
        var keep = retained.Write("keep.txt");
        for (var i = 0; i < 1100; i++) added.Write($"expanded-{i}.txt");
        using var service = new FileIndexService();
        service.ReplaceAll(new[] { SearchRecord.FromFile(keep) }, retained.Settings());
        var expanded = retained.Settings(); expanded.SearchRoots.Add(added.Root);
        using var blocked = new BlockedScan();
        var build = service.RebuildAsync(expanded, blocked.Progress, CancellationToken.None);
        await blocked.WaitEntered(); await Until(() => service.Count > 1, "scope expansion preview");
        Assert(Has(service, keep) && service.Search("expanded-", false, 1, CancellationToken.None).Count == 1 && !build.IsCompleted, "Scope expansion preserves old records and reveals new-root matches early");
        blocked.Release(); await build;
        await IndexStateAssertions.WaitForCleanStaticSnapshot(service,
            new[] { keep }.Concat(Directory.EnumerateFiles(added.Root)), "expanded scope final clean state");
        Assert(service.Count == 1101, "Expanded scope completes without duplicates");
    }

    private static object Session(FileIndexService service, string field) => typeof(FileIndexService).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
    private static void Enqueue(object session, string path) => session.GetType().GetMethod("Enqueue")!.Invoke(session, new object[] { path, true });
    private static bool Has(FileIndexService service, string path) => service.Snapshot().Any(x => x.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase));
    private static async Task Canceled(Task operation) { try { await operation; throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { } }
    private static async Task Until(Func<bool> condition, string reason)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.Elapsed > TimeSpan.FromSeconds(10)) throw new Exception("Timeout: " + reason); await Task.Delay(10); }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal(IEnumerable<string> expected, IEnumerable<string> actual, string message)
    {
        var expectedPaths = expected.ToArray();
        var actualPaths = actual.ToArray();
        if (expectedPaths.SequenceEqual(actualPaths, StringComparer.Ordinal)) return;
        var missing = expectedPaths.Except(actualPaths, StringComparer.Ordinal).ToArray();
        var unexpected = actualPaths.Except(expectedPaths, StringComparer.Ordinal).ToArray();
        var details = System.Text.Json.JsonSerializer.Serialize(new
        {
            expectedCount = expectedPaths.Length, actualCount = actualPaths.Length,
            expected = expectedPaths.Take(10).ToArray(), actual = actualPaths.Take(10).ToArray(),
            missing = missing.Take(10).ToArray(), unexpected = unexpected.Take(10).ToArray()
        });
        throw new Exception($"{message}: {details}");
    }
    private sealed class BlockedScan : IDisposable
    {
        private readonly ManualResetEventSlim _entered = new();
        private readonly ManualResetEventSlim _release = new();
        public void Progress(int count, string directory) { _entered.Set(); Assert(_release.Wait(TimeSpan.FromSeconds(15)), "Fixture release timed out"); }
        public Task WaitEntered() => Until(() => _entered.IsSet, "scan entered deterministic barrier");
        public void Release() => _release.Set();
        public void Dispose() => _release.Set(); // Leave wait handles valid until any unwinding worker exits.
    }
    private sealed class Tree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "dindex-preview-" + Guid.NewGuid().ToString("N"));
        public Tree() => Directory.CreateDirectory(Root);
        public string Write(string relative, string value = "fixture") { var path = Path.GetFullPath(Path.Combine(Root, relative)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); return path; }
        public AppSettings InitialDirectorySettings() { var settings = Settings(); settings.IncludeDirectories = true; return settings; }
        public AppSettings Settings(params string[] excludes) => new() { SearchRoots = new() { Root }, IncludeDirectories = false, ExcludeFolderNames = excludes.ToList() };
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }
    }
}
