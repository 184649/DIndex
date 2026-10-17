// Standalone driver compiled by run-initial-results-benchmark.sh; not part of the test executable.
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DIndex;

internal static class InitialResultsBenchmark
{
    public static async Task<int> Main(string[] args)
    {
        var root = Path.GetFullPath(args[0]);
        var retainedCount = int.Parse(args[1]);
        var verifySearch = args.Length < 3 || bool.Parse(args[2]);
        using var service = new FileIndexService();
        var settings = new AppSettings { SearchRoots = new() { root }, IncludeDirectories = false, ExcludeFolderNames = new() };
        if (retainedCount > 0)
        {
            service.ReplaceAll(Enumerable.Range(0, retainedCount).Select(i =>
            {
                var name = $"cached-{i}.txt"; var path = Path.Combine(root, name);
                return new SearchRecord { FullPath = path, FullPathLower = path.ToLowerInvariant(), Name = name, NameLower = name, Folder = root, Extension = "txt" };
            }), settings);
        }
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var initialManagedBytes = GC.GetTotalMemory(false);
        var initialAllocatedBytes = GC.GetTotalAllocatedBytes(true);
        var initialWorkingSetBytes = Process.GetCurrentProcess().WorkingSet64;
        var timer = Stopwatch.StartNew();
        double firstPublishedMs = -1, firstSearchableMs = -1, firstSearchCostMs = -1;
        int notifications = 0, firstCount = 0;
        var firstWasRebuilding = false;
        service.SnapshotChanged += _ =>
        {
            Interlocked.Increment(ref notifications);
            if (firstPublishedMs >= 0 || service.Count == 0) return;
            firstPublishedMs = timer.Elapsed.TotalMilliseconds;
            firstCount = service.Count;
            firstWasRebuilding = service.IsRebuilding;
            if (!verifySearch) return;
            var search = Stopwatch.StartNew();
            if (service.Search("file-", false, 1, CancellationToken.None).Count != 1) throw new Exception("No discovered file was searchable");
            firstSearchCostMs = search.Elapsed.TotalMilliseconds;
            firstSearchableMs = timer.Elapsed.TotalMilliseconds;
        };
        await service.RebuildAsync(settings, null, CancellationToken.None);
        var totalMs = timer.Elapsed.TotalMilliseconds;
        if (firstPublishedMs < 0 || verifySearch && firstSearchableMs < 0) throw new Exception("Benchmark did not establish searchable results");
        var allocatedBytes = GC.GetTotalAllocatedBytes(true) - initialAllocatedBytes;
        var finalManagedBytes = GC.GetTotalMemory(false);
        var process = Process.GetCurrentProcess();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            retainedCount, verifySearch, firstPublishedMs, firstSearchableMs, firstSearchCostMs, totalMs,
            firstCount, finalCount = service.Count, notifications, firstWasRebuilding,
            initialManagedBytes, finalManagedBytes, allocatedBytes,
            initialWorkingSetBytes, finalWorkingSetBytes = process.WorkingSet64, peakWorkingSetBytes = process.PeakWorkingSet64,
            dirty = service.IsDirty, os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription
        }));
        return 0;
    }
}
