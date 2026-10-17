using System.Diagnostics;
using System.IO;
using System.Reflection;
using DIndex;

internal static class Program
{
    private static int _passed;
    private static int _skipped;
    private static int _optionalReparseSkips;
    private sealed class SkipTestException(string reason) : Exception(reason);
    public static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Run)[]
        {
            ("search matches baseline across sizes, scopes and Unicode", SearchEquivalence),
            ("blank/small/large/empty search cancellation", SearchCancellation),
            ("snapshots are immutable and reusable", SnapshotReuse),
            ("bounded recent cache preserves limits, ties, cancellation and generations", EmptyCacheTests.Run),
            ("cached records are filtered by current settings", CacheScope),
            ("single-root traversal covers all descendants", Traversal),
            ("static watcher tails drain without hiding recovery failures", StaticWatcherTail),
            ("preview test fixture paths are canonical on every platform", ProgressiveIndexTests.FixturePathsAreCanonical),
            ("bounded previews are searchable before scan completion and reconcile changes", ProgressiveIndexTests.SearchableBeforeCompletion),
            ("canceling a preview restores the prior snapshot", ProgressiveIndexTests.CancelRestoresBaseline),
            ("superseded previews cannot overwrite or leak old scope", ProgressiveIndexTests.SupersededPreviewAndScope),
            ("same-scope queued cancellation never promotes an old partial preview", ProgressiveIndexTests.SupersededSameScopeCancellation),
            ("previews preserve unreadable fallback and enforce exclusions", ProgressiveIndexTests.UnreadableFallbackAndScope),
            ("active watcher updates cannot replace previews and survive cancellation", ProgressiveIndexTests.ActiveWatcherCannotReplacePreview),
            ("failed preview generation restores its safe baseline", ProgressiveIndexTests.FailedScanRestoresBaseline),
            ("canceled async cache supersession cannot strand a preview", ProgressiveIndexTests.CanceledCacheSupersedesPreview),
            ("unchanged scope skips previews and keeps its old watcher live", ProgressiveIndexTests.SameScopeBaselineKeepsWatcherLive),
            ("expanded scope still reveals new-root results early", ProgressiveIndexTests.ExpandedScopeStillPublishesEarly),
            ("excluded ancestors and reparse cycles", ExclusionsAndLinks),
            ("canceled rebuild preserves old searchable snapshot", CanceledRebuild),
            ("new generation wins over delayed old workers", OverlappingRebuilds),
            ("watcher creates, changes and deletes files", WatcherFiles),
            ("directory rename/delete updates descendants", WatcherDirectories),
            ("directory operations work without directory results", WatcherDirectoriesWithoutResults),
            ("moved-in subtree is completely indexed", MovedInSubtree),
            ("watchers respect exclusions and reparse ancestors", WatcherScope),
            ("old watcher generation cannot contaminate new roots", OldWatcherGeneration),
            ("bounded watcher overflow triggers automatic recovery", OverflowRecovery),
            ("missing roots remain dirty and preserve cached data", MissingRoot),
            ("completed request cancellation leaves live watcher", CompletedRequestCancellation),
            ("throwing notification subscribers do not stop indexing", ThrowingSubscribers),
            ("watcher error recovers with safe lifetime", WatcherError),
            ("concurrent search snapshots stay consistent during writes", ConcurrentSearchUpdates),
            ("rapid rebuilds have one staging scan and latest wins", RapidRebuilds),
            ("directory/file replacement removes stale descendants", ReplacedDirectory),
            ("unreadable subtree preserves prior records and is dirty", UnreadableSubtree),
            ("scope changes stay filtered when rebuild is canceled", ScopeChangeCancellation),
            ("async cache restore respects expected request version", AsyncCacheExpectedVersion),
            ("late async cache cannot overwrite a newer rebuild", AsyncCacheSuperseded),
            ("canceled async cache preserves prior snapshot", AsyncCacheCanceled),
            ("dispose cancels work and rejects new builds", Disposal)
        };
        foreach (var (name, test) in tests)
        {
            var timer = Stopwatch.StartNew();
            try { await test(); Console.WriteLine($"PASS {name} ({timer.ElapsedMilliseconds} ms)"); _passed++; }
            catch (SkipTestException ex) { Console.WriteLine($"SKIP {name}: {ex.Message}"); _skipped++; }
            catch (Exception ex) { Console.Error.WriteLine($"FAIL {name}: {ex}"); return 1; }
        }
        Console.WriteLine($"RESULT: passed={_passed}, skipped={_skipped}, total={tests.Length}, optional reparse fixture skips={_optionalReparseSkips}; OS={System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        return 0;
    }

    private static SearchRecord Record(int n)
    {
        var name = n % 7 == 0 ? $"資料_report_{n:D7}.txt" : $"item_{n:D7}_budget.txt";
        var path = $"C:\\fixture\\folder{n % 97}\\{name}";
        return new SearchRecord { Name = name, NameLower = name.ToLowerInvariant(), FullPath = path,
            FullPathLower = path.ToLowerInvariant(), Folder = "C:\\fixture", IsDirectory = n % 13 == 0,
            UpdatedTicks = n % 997, SizeBytes = n };
    }

    private static Task SearchEquivalence()
    {
        foreach (var size in new[] { 0, 12, 50000, 50001, 100000 })
        {
            using var service = new FileIndexService();
            service.ReplaceAll(Enumerable.Range(0, size).Select(Record));
            var snapshot = service.Snapshot().ToArray();
            foreach (var query in new[] { "", "  ", "item", "budget 000", "REPORT 資料", "folder9", "no-match", "資料", "item_0000001_budget.txt" })
            foreach (var path in new[] { true, false })
            foreach (var limit in new[] { 0, 1, 27, 500 })
            {
                var expected = Baseline(snapshot, query, path, limit);
                var actual = service.Search(query, path, limit, CancellationToken.None);
                Equal(expected.Select(x => x.FullPath), actual.Select(x => x.FullPath), $"size={size}, query={query}, path={path}, limit={limit}");
            }
        }
        return Task.CompletedTask;
    }

    private static List<SearchRecord> Baseline(SearchRecord[] values, string keyword, bool pathSearch, int limit)
    {
        keyword = keyword.Trim();
        if (string.IsNullOrWhiteSpace(keyword)) return values.OrderByDescending(x => x.UpdatedTicks).Take(limit).ToList();
        var words = keyword.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.ToLowerInvariant()).ToArray();
        bool Match(SearchRecord item) => words.All(w => (pathSearch ? item.FullPathLower : item.NameLower).Contains(w, StringComparison.Ordinal));
        int Score(SearchRecord item) => (pathSearch ? item.FullPathLower : item.NameLower).IndexOf(words[0], StringComparison.Ordinal)
            + (item.NameLower == words[0] ? -10000 : 0) + (item.NameLower.StartsWith(words[0], StringComparison.Ordinal) ? -5000 : 0) + (item.IsDirectory ? 0 : 30);
        return values.Where(Match).OrderBy(Score).ThenBy(x => x.Name).Take(Math.Max(1, limit)).ToList();
    }

    private static async Task SearchCancellation()
    {
        using var service = new FileIndexService();
        foreach (var size in new[] { 0, 32, 50000, 50001, 250000 })
        {
            service.ReplaceAll(Enumerable.Range(0, size).Select(Record));
            foreach (var query in new[] { "", "item", "absent" })
            {
                using var cts = new CancellationTokenSource();
                cts.Cancel();
                await Canceled(() => Task.Run(() => service.Search(query, true, 500, cts.Token)));
            }
        }
        foreach (var query in new[] { "", "item" })
        {
            using var cts = new CancellationTokenSource();
            cts.CancelAfter(1);
            await Canceled(() => Task.Run(() => service.Search(query, true, 200000, cts.Token)));
        }
    }

    private static Task SnapshotReuse()
    {
        using var service = new FileIndexService();
        service.ReplaceAll(new[] { Record(1), Record(2) });
        var old = service.Snapshot();
        var version = service.SnapshotVersion;
        Assert(ReferenceEquals(old, service.Snapshot()), "Snapshot should reuse its immutable wrapper");
        Assert(old is not SearchRecord[], "Do not expose mutable array");
        var mutable = old as IList<SearchRecord>;
        Assert(mutable?.IsReadOnly == true, "Published view is read-only");
        service.ReplaceAll(new[] { Record(3) });
        Assert(old.Count == 2 && service.Count == 1 && service.SnapshotVersion > version, "Old snapshot remains intact");
        return Task.CompletedTask;
    }

    private static Task CacheScope()
    {
        using var tree = new Tree();
        using var service = new FileIndexService();
        var allowed = tree.Write("ok/a.txt");
        var excluded = tree.Write("skip/deep/b.txt");
        // The excluded directory and a file with that same name need separate locations.
        var fileNamedSkip = tree.Write("ok/skip");
        var settings = tree.Settings(false, "skip");
        service.ReplaceAll(new[] { SearchRecord.FromFile(allowed), SearchRecord.FromFile(excluded), SearchRecord.FromFile(fileNamedSkip), SearchRecord.FromDirectory(Path.GetDirectoryName(allowed)!) }, settings);
        Equal(new[] { allowed, fileNamedSkip }.Order(), service.Snapshot().Select(x => x.FullPath).Order(), "scope filter");
        Assert(service.IsDirty, "Cache must be marked unverified");
        return Task.CompletedTask;
    }

    private static async Task Traversal()
    {
        using var tree = new Tree();
        for (var i = 0; i < 96; i++) for (var j = 0; j < 12; j++) tree.Write($"a/d{i}/nested/f{j}.txt");
        using var service = new FileIndexService();
        var counts = 0;
        service.CountChanged += _ => Interlocked.Increment(ref counts);
        await service.RebuildAsync(tree.Settings(), null, CancellationToken.None);
        var expected = Directory.EnumerateFileSystemEntries(tree.Root, "*", SearchOption.AllDirectories).Append(tree.Root).Order().ToArray();
        Equal(expected, service.Snapshot().Select(x => x.FullPath).Order(), "all descendants");
        Assert(counts >= 1 && counts <= 5, "Only throttled geometric previews plus final count notification");
        await IndexStateAssertions.WaitForCleanStaticSnapshot(service, expected, "Clean after static enumeration");
        // Re-enumerate after settling too: exact agreement must still hold, not just a clean flag.
        Equal(Directory.EnumerateFileSystemEntries(tree.Root, "*", SearchOption.AllDirectories).Append(tree.Root).Order(),
            service.Snapshot().Select(x => x.FullPath).Order(), "all descendants after watcher settling");
        var active = GetActive(service);
        var peak = (int)active.GetType().GetProperty("PeakWorkerCount")!.GetValue(active)!;
        Assert(peak > 1, $"A single root must actually keep multiple traversal workers active (peak={peak})");
    }

    private static async Task StaticWatcherTail()
    {
        using var tree = new Tree();
        var paths = Enumerable.Range(0, 600).Select(i => tree.Write($"static-{i:D4}.txt")).ToArray();
        using var service = new FileIndexService();
        await service.RebuildAsync(tree.Settings(false), null, CancellationToken.None);
        await IndexStateAssertions.WaitForCleanStaticSnapshot(service, paths, "initial static-tail fixture");
        var active = GetActive(service); var version = service.SnapshotVersion;
        // Reproduce a late notification tail deterministically, beyond the 512-path batch cap.
        // These are current files, so every published snapshot must remain exactly equivalent.
        IndexStateAssertions.State dirty;
        // Use the service's existing lock order so the pump cannot consume part of the injected
        // batch before its initial state is observed. No flags or records are changed directly.
        lock (typeof(FileIndexService).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!)
        lock (active.GetType().GetField("_lock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(active)!)
        {
            foreach (var path in paths) Enqueue(active, path, false);
            dirty = IndexStateAssertions.Capture(service);
        }
        Assert(dirty.IsDirty && dirty.Active!.PendingCount > 512 && !dirty.Active.NeedsRecovery,
            "Injected watcher tail must be dirty without recovery: " + IndexStateAssertions.Describe(dirty));
        await IndexStateAssertions.WaitForCleanStaticSnapshot(service, paths, "injected 600-path watcher tail");
        Assert(service.SnapshotVersion >= version + 2 && ReferenceEquals(active, GetActive(service)),
            "Two real watcher batches must publish without silently rebuilding");
        Equal(paths.Order(), service.Snapshot().Select(x => x.FullPath).Order(), "static-tail final exact equivalence");

        // Waiting must never turn a real read failure into an unobserved automatic-recovery pass.
        active.GetType().GetMethod("NoteFailure")!.Invoke(active, new object[] { tree.Root });
        try
        {
            await IndexStateAssertions.WaitForCleanStaticSnapshot(service, paths, "injected unreadable static root");
            throw new Exception("Expected the settling assertion to reject a real read failure");
        }
        catch (Exception ex) when (ex.Message.Contains("cannot treat recovery", StringComparison.Ordinal))
        {
            Assert(ex.Message.Contains("\"needsRecovery\":true", StringComparison.Ordinal)
                && ex.Message.Contains("\"failureVersion\":1", StringComparison.Ordinal)
                && ex.Message.Contains("\"failedPathCount\":1", StringComparison.Ordinal),
                "A rejected failure must expose recovery and failed-path diagnostics: " + ex.Message);
        }
    }

    private static async Task ExclusionsAndLinks()
    {
        using var tree = new Tree();
        tree.Write("safe/file.txt"); tree.Write("skip/deep/hidden.txt"); tree.Write("safe/skip");
        var link = Path.Combine(tree.Root, "safe", "cycle");
        var supported = TryLink(link, tree.Root);
        using var service = new FileIndexService();
        await service.RebuildAsync(tree.Settings(true, "skip"), null, CancellationToken.None);
        Assert(!service.Snapshot().Any(x => x.FullPath.Contains(Path.Combine("skip", "deep"))), "Excluded subtree");
        Assert(service.Snapshot().Any(x => x.FullPath == Path.Combine(tree.Root, "safe", "skip")), "File named as exclusion is allowed");
        if (supported) Assert(service.Snapshot().Count(x => x.FullPath.StartsWith(link, StringComparison.OrdinalIgnoreCase)) == 1, "Index reparse point itself without recursion");
        var nested = tree.Settings(); nested.SearchRoots = new() { Path.Combine(tree.Root, "skip", "deep") }; nested.ExcludeFolderNames = new() { "skip" };
        await service.RebuildAsync(nested, null, CancellationToken.None);
        Assert(service.Count == 0, "An explicitly selected root still respects excluded ancestors");
    }

    private static async Task CanceledRebuild()
    {
        using var tree = new Tree(); for (var i = 0; i < 100; i++) tree.Write($"a{i}/f.txt");
        using var service = new FileIndexService(); service.ReplaceAll(new[] { SearchRecord.FromFile(Path.Combine(tree.Root, "a0/f.txt")) }, tree.Settings());
        var old = service.Snapshot();
        using var cts = new CancellationTokenSource();
        await Canceled(() => service.RebuildAsync(tree.Settings(), (_, _) => cts.Cancel(), cts.Token));
        Assert(ReferenceEquals(old, service.Snapshot()), "Canceled staging generation must not replace old snapshot");
        Assert(!service.IsRebuilding && service.Search("f.txt", false, 5, CancellationToken.None).Count == 1, "Old results remain usable");
    }

    private static async Task OverlappingRebuilds()
    {
        using var first = new Tree(); using var second = new Tree();
        for (var i = 0; i < 24; i++) first.Write($"a{i}/old.txt"); second.Write("new.txt");
        using var service = new FileIndexService(); service.ReplaceAll(new[] { SearchRecord.FromFile(Path.Combine(first.Root, "a0/old.txt")) }, first.Settings());
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var old = service.RebuildAsync(first.Settings(), (_, _) => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }, CancellationToken.None);
        Assert(entered.Wait(TimeSpan.FromSeconds(5)), "Old build reached progress callback");
        Assert(service.Search("old.txt", false, 5, CancellationToken.None).Count == 1, "Old published index readable during rebuild");
        var current = service.RebuildAsync(second.Settings(), null, CancellationToken.None);
        release.Set(); await current; await Canceled(() => old);
        await Task.Delay(600);
        Assert(service.Snapshot().All(x => x.FullPath.StartsWith(second.Root, StringComparison.OrdinalIgnoreCase)), "No old worker writes into new generation");
    }

    private static async Task WatcherFiles()
    {
        using var tree = new Tree(); using var service = new FileIndexService();
        await service.RebuildAsync(tree.Settings(), null, CancellationToken.None);
        var before = service.Snapshot(); var file = tree.Write("fresh.txt", "a");
        await Until(() => Has(service, file), "created file");
        Assert(before.Count == 1, "Old reader snapshot remains immutable");
        File.WriteAllText(file, new string('x', 100));
        await Until(() => service.Snapshot().Any(x => x.FullPath == file && x.SizeBytes == 100), "file metadata");
        File.Delete(file); await Until(() => !Has(service, file), "deleted file");
    }

    private static async Task WatcherDirectories() => await DirectoryOperations(true);
    private static async Task WatcherDirectoriesWithoutResults() => await DirectoryOperations(false);
    private static async Task DirectoryOperations(bool includeDirectories)
    {
        using var tree = new Tree(); var a = tree.Write("old/deep/a.txt"); tree.Write("old/deep/b.txt"); tree.Write("oldish/keep.txt");
        using var service = new FileIndexService(); await service.RebuildAsync(tree.Settings(includeDirectories), null, CancellationToken.None);
        Directory.Move(Path.Combine(tree.Root, "old"), Path.Combine(tree.Root, "renamed"));
        await Until(() => !Has(service, a) && Has(service, Path.Combine(tree.Root, "renamed/deep/a.txt")), "renamed descendants");
        Directory.Delete(Path.Combine(tree.Root, "renamed"), true);
        await Until(() => !service.Snapshot().Any(x => x.FullPath.StartsWith(Path.Combine(tree.Root, "renamed"), StringComparison.OrdinalIgnoreCase)), "deleted descendants");
        Assert(Has(service, Path.Combine(tree.Root, "oldish/keep.txt")), "Prefix boundary keeps sibling paths");
    }

    private static async Task MovedInSubtree()
    {
        using var tree = new Tree(); using var outside = new Tree();
        for (var i = 0; i < 32; i++) outside.Write($"incoming/d{i}/f.txt");
        using var service = new FileIndexService(); await service.RebuildAsync(tree.Settings(false), null, CancellationToken.None);
        Directory.Move(Path.Combine(outside.Root, "incoming"), Path.Combine(tree.Root, "incoming"));
        await Until(() => service.Count == 32, "all files in moved-in directory");
    }

    private static async Task WatcherScope()
    {
        using var tree = new Tree(); using var outside = new Tree();
        using var service = new FileIndexService(); await service.RebuildAsync(tree.Settings(true, "skip"), null, CancellationToken.None);
        tree.Write("skip/deep/hidden.txt"); var visible = tree.Write("visible/skip");
        var link = Path.Combine(tree.Root, "linked"); var supported = TryLink(link, outside.Root);
        outside.Write("foreign.txt");
        await Until(() => Has(service, visible), "allowed file under watcher"); await Task.Delay(700);
        Assert(!service.Snapshot().Any(x => x.FullPath.Contains(Path.Combine("skip", "deep"))), "Watchers enforce excluded ancestors");
        if (supported) Assert(!service.Snapshot().Any(x => x.FullPath == Path.Combine(link, "foreign.txt")), "No traversal through reparse parent");
    }

    private static async Task OldWatcherGeneration()
    {
        using var old = new Tree(); using var current = new Tree(); using var service = new FileIndexService();
        await service.RebuildAsync(old.Settings(), null, CancellationToken.None);
        var oldSession = GetActive(service);
        await service.RebuildAsync(current.Settings(), null, CancellationToken.None);
        var path = old.Write("late.txt"); Enqueue(oldSession, path, true);
        current.Write("good.txt"); await Until(() => service.Count >= 2, "new watcher is live");
        Assert(!Has(service, path), "Queued callbacks from disposed session ignored");
    }

    private static async Task OverflowRecovery()
    {
        using var tree = new Tree(); using var service = new FileIndexService();
        await service.RebuildAsync(tree.Settings(), null, CancellationToken.None);
        var session = GetActive(service); var version = service.SnapshotVersion;
        // Fill the real bounded queue deterministically. Real OS buffer overflow is timing-specific.
        for (var i = 0; i < 9000; i++) Enqueue(session, Path.Combine(tree.Root, $"missing{i}"), true);
        Assert(service.IsDirty, "Queue saturation must make the index explicitly dirty");
        var pending = session.GetType().GetField("_pending", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        var pendingCount = (int)pending.GetType().GetProperty("Count")!.GetValue(pending)!;
        Assert(pendingCount <= 8192, "Bounded queue");
        tree.Write("recovered.txt");
        await Until(() => !ReferenceEquals(GetActive(service), session) && !service.IsDirty && Has(service, Path.Combine(tree.Root, "recovered.txt")), "automatic overflow reconciliation", 15000);
        Assert(service.SnapshotVersion > version, "Recovery publishes a fresh generation");
    }

    private static async Task MissingRoot()
    {
        using var tree = new Tree(); var file = tree.Write("keep.txt"); var settings = tree.Settings();
        using var service = new FileIndexService(); service.ReplaceAll(new[] { SearchRecord.FromFile(file) }, settings);
        Directory.Delete(tree.Root, true);
        await service.RebuildAsync(settings, null, CancellationToken.None);
        Assert(service.IsDirty && Has(service, file), "Unavailable root is not falsely clean or destructively emptied");
    }

    private static async Task CompletedRequestCancellation()
    {
        using var tree = new Tree(); using var service = new FileIndexService(); using var cts = new CancellationTokenSource();
        await service.RebuildAsync(tree.Settings(), null, cts.Token);
        cts.Cancel();
        var file = tree.Write("after-cancel.txt");
        await Until(() => Has(service, file), "committed watcher survives completed request token cancellation");
    }

    private static async Task ThrowingSubscribers()
    {
        using var tree = new Tree(); using var service = new FileIndexService();
        service.CountChanged += _ => throw new InvalidOperationException("test subscriber");
        service.StatusChanged += _ => throw new IOException("test subscriber");
        service.SnapshotChanged += _ => throw new ObjectDisposedException("test subscriber");
        await service.RebuildAsync(tree.Settings(), null, CancellationToken.None);
        var file = tree.Write("after-callback.txt");
        await Until(() => Has(service, file), "watcher starts despite subscriber failures");
    }

    private static async Task WatcherError()
    {
        using var tree = new Tree(); using var service = new FileIndexService();
        await service.RebuildAsync(tree.Settings(), null, CancellationToken.None);
        var session = GetActive(service);
        var watchers = (System.Collections.IEnumerable)session.GetType().GetField("_watchers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        var first = watchers.Cast<object>().First();
        var watcher = (FileSystemWatcher)first.GetType().GetField("Item1")!.GetValue(first)!;
        typeof(FileSystemWatcher).GetMethod("OnError", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(watcher, new object[] { new ErrorEventArgs(new IOException("injected watcher error")) });
        Assert(service.IsDirty, "Watcher error immediately marks dirty");
        await Until(() => !ReferenceEquals(GetActive(service), session) && !service.IsDirty, "watcher error recovery");
        var file = tree.Write("after-recovery.txt"); await Until(() => Has(service, file), "recovered watcher is live");
        service.Dispose(); // Regression: canceled/disposed source could throw here.
    }

    private static async Task ConcurrentSearchUpdates()
    {
        using var tree = new Tree(); using var service = new FileIndexService();
        await service.RebuildAsync(tree.Settings(false), null, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var reads = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                var snapshot = service.Snapshot(); var count = snapshot.Count;
                var result = service.Search("file", true, 500, CancellationToken.None);
                Assert(result.Select(x => x.FullPath).Distinct().Count() == result.Count, "No duplicate records in search");
                await Task.Delay(5);
                Assert(snapshot.Count == count, "Reader snapshot cannot mutate");
            }
        });
        for (var i = 0; i < 160; i++) tree.Write($"d{i % 8}/file{i}.txt");
        await Until(() => service.Count == 160, "burst update convergence");
        Directory.Delete(Path.Combine(tree.Root, "d3"), true);
        await Until(() => service.Count == 140, "subtree delete convergence during search");
        cts.Cancel(); await reads;
    }

    private static async Task RapidRebuilds()
    {
        using var first = new Tree(); using var last = new Tree(); first.Write("old/file.txt"); last.Write("latest.txt");
        using var service = new FileIndexService(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        var blocked = service.RebuildAsync(first.Settings(), (_, _) => { entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); }, CancellationToken.None);
        Assert(entered.Wait(TimeSpan.FromSeconds(5)), "First build entered");
        var queuedProgress = 0;
        var superseded = Enumerable.Range(0, 20).Select(_ => service.RebuildAsync(first.Settings(), (_, _) => Interlocked.Increment(ref queuedProgress), CancellationToken.None)).ToArray();
        var latest = service.RebuildAsync(last.Settings(), null, CancellationToken.None);
        await Task.Delay(100);
        Assert(queuedProgress == 0, "Queued generations must not allocate workers while canceled scan is blocked");
        release.Set(); await latest; await Canceled(() => blocked);
        foreach (var task in superseded) await Canceled(() => task);
        Assert(queuedProgress == 0 && Has(service, Path.Combine(last.Root, "latest.txt")), "Only newest queued build runs");
    }

    private static async Task ReplacedDirectory()
    {
        using var tree = new Tree(); var old = tree.Write("replace/deep/old.txt");
        using var service = new FileIndexService(); await service.RebuildAsync(tree.Settings(false), null, CancellationToken.None);
        var path = Path.Combine(tree.Root, "replace"); Directory.Delete(path, true); File.WriteAllText(path, "replacement");
        await Until(() => Has(service, path) && !Has(service, old), "directory replaced by file");
        File.Delete(path); var current = tree.Write("replace/deep/new.txt");
        await Until(() => Has(service, current) && !Has(service, path), "file replaced by directory");
    }

    private static async Task UnreadableSubtree()
    {
        if (OperatingSystem.IsWindows()) throw new SkipTestException("Unix permission fixture; Windows ACL test remains manual");
        using var tree = new Tree(); var file = tree.Write("denied/keep.txt"); var directory = Path.GetDirectoryName(file)!;
        using var service = new FileIndexService(); await service.RebuildAsync(tree.Settings(), null, CancellationToken.None);
        var original = File.GetUnixFileMode(directory);
        try
        {
            File.SetUnixFileMode(directory, UnixFileMode.None);
            try { Directory.GetFiles(directory); throw new SkipTestException("Current account bypasses Unix mode restrictions"); }
            catch (UnauthorizedAccessException) { }
            await service.RebuildAsync(tree.Settings(), null, CancellationToken.None);
            Assert(service.IsDirty && Has(service, file), "Denied enumeration retains prior records and is not marked clean");
        }
        finally { File.SetUnixFileMode(directory, original); }
    }

    private static async Task ScopeChangeCancellation()
    {
        using var retained = new Tree(); using var removed = new Tree();
        var keep = retained.Write("keep/a.txt"); retained.Write("skip/hidden.txt"); removed.Write("removed.txt");
        using var service = new FileIndexService(); var initial = retained.Settings(); initial.SearchRoots.Add(removed.Root);
        await service.RebuildAsync(initial, null, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var narrowed = retained.Settings(false, "skip");
        var rebuild = service.RebuildAsync(narrowed, (_, _) => cts.Cancel(), cts.Token);
        Assert(service.Snapshot().All(x => x.FullPath == keep), "Scope change immediately hides removed paths while background filtering prepares the safe subset");
        await Canceled(() => rebuild);
        removed.Write("late.txt"); await Task.Delay(650);
        Equal(new[] { keep }, service.Snapshot().Select(x => x.FullPath), "Canceled build/old watcher cannot resurrect removed scope");
        Assert(service.IsDirty, "Filtered subset awaits verification");
    }

    private static async Task AsyncCacheExpectedVersion()
    {
        using var tree = new Tree(); var file = tree.Write("cached.txt"); using var service = new FileIndexService();
        var initialVersion = service.RequestVersion;
        Assert(await service.ReplaceAllAsync(new[] { SearchRecord.FromFile(file) }, tree.Settings(), initialVersion, CancellationToken.None), "Initial async cache restore commits");
        Assert(service.RequestVersion > initialVersion && Has(service, file), "Async cache commit updates request/snapshot");
        var current = service.Snapshot();
        Assert(!await service.ReplaceAllAsync(Array.Empty<SearchRecord>(), tree.Settings(), initialVersion, CancellationToken.None), "Stale pre-load request version rejected");
        Assert(ReferenceEquals(current, service.Snapshot()), "Stale restore cannot clear current records");
    }

    private static async Task AsyncCacheSuperseded()
    {
        using var stale = new Tree(); using var current = new Tree();
        var old = stale.Write("stale.txt"); var latest = current.Write("latest.txt");
        using var service = new FileIndexService(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        IEnumerable<SearchRecord> Delayed()
        {
            entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); yield return SearchRecord.FromFile(old);
        }
        var loading = service.ReplaceAllAsync(Delayed(), stale.Settings(), service.RequestVersion, CancellationToken.None);
        Assert(entered.Wait(TimeSpan.FromSeconds(5)), "Cache preparation runs asynchronously");
        await service.RebuildAsync(current.Settings(), null, CancellationToken.None);
        release.Set(); Assert(!await loading, "Superseded asynchronous cache restore returns false");
        Assert(Has(service, latest) && !Has(service, old), "Late startup cache cannot replace new root results");
    }

    private static async Task AsyncCacheCanceled()
    {
        using var tree = new Tree(); var file = tree.Write("old.txt"); using var service = new FileIndexService();
        service.ReplaceAll(new[] { SearchRecord.FromFile(file) }, tree.Settings()); var prior = service.Snapshot();
        using var cts = new CancellationTokenSource(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        IEnumerable<SearchRecord> Delayed()
        {
            entered.Set(); release.Wait(TimeSpan.FromSeconds(5)); yield return SearchRecord.FromFile(file);
        }
        var loading = service.ReplaceAllAsync(Delayed(), tree.Settings(), service.RequestVersion, cts.Token);
        Assert(entered.Wait(TimeSpan.FromSeconds(5)), "Cache preparation entered");
        cts.Cancel(); release.Set(); await Canceled(() => loading);
        Assert(ReferenceEquals(prior, service.Snapshot()), "Canceled preparation cannot partially replace old index");
    }

    private static Task Disposal()
    {
        var service = new FileIndexService(); service.Dispose(); service.Dispose();
        try { service.RebuildAsync(new AppSettings(), null, CancellationToken.None); throw new Exception("Expected ObjectDisposedException"); }
        catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    private static object GetActive(FileIndexService service) => typeof(FileIndexService).GetField("_active", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
    private static void Enqueue(object session, string path, bool subtree) => session.GetType().GetMethod("Enqueue")!.Invoke(session, new object[] { path, subtree });
    private static bool Has(FileIndexService service, string path) => service.Snapshot().Any(x => x.FullPath.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
    private static async Task Until(Func<bool> condition, string reason, int timeoutMs = 10000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition()) { if (timer.ElapsedMilliseconds > timeoutMs) throw new Exception($"Timeout: {reason}"); await Task.Delay(50); }
    }
    private static async Task Canceled(Func<Task> operation)
    {
        try { await operation(); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) { }
    }
    private static bool TryLink(string path, string target)
    {
        try { Directory.CreateSymbolicLink(path, target); return true; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        { _optionalReparseSkips++; Console.WriteLine($"PARTIAL SKIP reparse fixture: {ex.GetType().Name}; requires platform permission"); return false; }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Equal(IEnumerable<string> expected, IEnumerable<string> actual, string message)
    {
        var a = expected.ToArray(); var b = actual.ToArray();
        if (!a.SequenceEqual(b)) throw new Exception($"{message}: expected {a.Length}, got {b.Length}; first differing pair {a.Zip(b).FirstOrDefault(x => x.First != x.Second)}");
    }
    private sealed class Tree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "dindex-core-" + Guid.NewGuid().ToString("N"));
        public Tree() => Directory.CreateDirectory(Root);
        public string Write(string relative, string value = "fixture")
        {
            var path = Path.GetFullPath(Path.Combine(Root, relative)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, value); return path;
        }
        public AppSettings Settings(bool directories = true, params string[] excludes) => new() { SearchRoots = new() { Root }, IncludeDirectories = directories, ExcludeFolderNames = excludes.ToList() };
        public void Dispose() { try { if (Directory.Exists(Root)) Directory.Delete(Root, true); } catch (IOException) { } }
    }
}
