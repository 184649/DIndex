using System.IO;
using System.Reflection;
using DIndex;

internal static class Tests
{
    private static int _checks;
    public static async Task Main()
    {
        foreach (var size in new[] { 0, 1, 57, 500, 501, 50001, 1000000 })
        {
            var records = Corpus.MakeCorpus(size);
            using var current = new FileIndexService(); using var cached = new CachedFileIndexService();
            current.ReplaceAll(records); cached.ReplaceAll(records);
            foreach (var limit in new[] { -20, 0, 1, 20, 499, 500, 501, 1000 })
            foreach (var query in new[] { "", " \t " })
                Equal(current.Search(query, false, limit, CancellationToken.None), cached.Search(query, false, limit, CancellationToken.None), $"size={size}, limit={limit}");
            var result = cached.Search("", false, 500, CancellationToken.None); result.Clear();
            Equal(current.Search("", false, 500, CancellationToken.None), cached.Search("", false, 500, CancellationToken.None), "Returned list cannot mutate cache");
        }
        using (var service = new CachedFileIndexService())
        {
            var records = Corpus.MakeCorpus(1000000); service.ReplaceAll(records);
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            foreach (var limit in new[] { 0, 1, 500, 501 }) Canceled(() => service.Search("", true, limit, canceled.Token));
            Assert(Cache(service) is null, "Canceled cold calls never publish a cache");
            using (var during = new CancellationTokenSource())
            {
                during.CancelAfter(1);
                Canceled(() => service.Search("", false, 500, during.Token));
            }
            Assert(Cache(service) is null, "Cancellation during first sort never caches a partial result");
            var first = service.Search("", false, 500, CancellationToken.None);
            Assert(Cache(service)?.Length == 500, "Bounded top-500 retained");
            Canceled(() => service.Search("", false, 500, canceled.Token));
            var prior = Snapshot(service);
            service.ReplaceAll(records.Reverse());
            Assert(Cache(service) is null, "New generation invalidates cache");
            var expected = service.Snapshot().OrderByDescending(x => x.UpdatedTicks).Take(500).ToList();
            Equal(expected, service.Search("", false, 500, CancellationToken.None), "Exact stable ties follow new generation order");
            Assert(!ReferenceEquals(prior, Snapshot(service)), "New generation owns its own cache");
            service.ReplaceAll(records);
            var gate = (SemaphoreSlim)Snapshot(service).GetType().GetProperty("RecentBuildGate")!.GetValue(Snapshot(service))!;
            gate.Wait();
            using var waiting = new CancellationTokenSource();
            var task = Task.Run(() => service.Search("", false, 500, waiting.Token));
            waiting.CancelAfter(10);
            try { await task; throw new Exception("Expected canceled waiter"); } catch (OperationCanceledException) { _checks++; }
            gate.Release();
            var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => service.Search("", false, 500, CancellationToken.None))));
            foreach (var value in concurrent) Equal(concurrent[0], value, "Concurrent first readers agree");
        }
        var root = Path.Combine(Path.GetTempPath(), "dindex-empty-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var old = Path.Combine(root, "old.txt"); var current = Path.Combine(root, "new.txt");
            File.WriteAllText(old, "old"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-2));
            File.WriteAllText(current, "new"); File.SetLastWriteTimeUtc(current, DateTime.UtcNow.AddHours(-1));
            using var service = new CachedFileIndexService();
            await service.RebuildAsync(new AppSettings { SearchRoots = new() { root }, IncludeDirectories = false, ExcludeFolderNames = new() }, null, CancellationToken.None);
            Assert(service.Search("", false, 1, CancellationToken.None)[0].FullPath == current, "Initial recent order");
            var version = service.SnapshotVersion;
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow);
            var until = DateTime.UtcNow.AddSeconds(10);
            while (service.SnapshotVersion == version && DateTime.UtcNow < until) await Task.Delay(25);
            Assert(service.SnapshotVersion > version && Cache(service) is null, "Actual watcher publication invalidates previous generation cache");
            Assert(service.Search("", false, 1, CancellationToken.None)[0].FullPath == old, "First query after update sees new order");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"PASS {_checks} focused empty-cache assertions");
    }
    private static object Snapshot(CachedFileIndexService service) => typeof(CachedFileIndexService).GetField("_snapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(service)!;
    private static SearchRecord[]? Cache(CachedFileIndexService service) => (SearchRecord[]?)Snapshot(service).GetType().GetField("RecentTop500")!.GetValue(Snapshot(service));
    private static void Canceled(Action action) { try { action(); throw new Exception("Expected cancellation"); } catch (OperationCanceledException) { _checks++; } }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); _checks++; }
    private static void Equal(List<SearchRecord> expected, List<SearchRecord> actual, string message)
        => Assert(expected.Select(x => x.FullPath).SequenceEqual(actual.Select(x => x.FullPath)), message);
}
