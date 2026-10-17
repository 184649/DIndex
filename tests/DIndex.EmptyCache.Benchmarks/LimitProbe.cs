using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using DIndex;

internal static class LimitProbe
{
    private static void Main(string[] args)
    {
        var root = args[0]; CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
        var records = Corpus.MakeCorpus(1000000); var baseline = new BaselineSearch(records);
        using var current = new FileIndexService(); using var cached = new CachedFileIndexService(); using var token = new CancellationTokenSource();
        var engines = new Func<string, bool, int, CancellationToken, List<SearchRecord>>[] { current.Search, cached.Search };
        Action[] publish = { () => current.PublishBenchmarkRecords(baseline.BenchmarkItems), () => cached.PublishBenchmarkRecords(baseline.BenchmarkItems) };
        var expected = baseline.Snapshot().OrderByDescending(x => x.UpdatedTicks).Take(500).Select(x => x.FullPath).ToArray();
        var rows = new List<object>();
        foreach (var limit in new[] { 1, 50, 500 })
        for (var iteration = 0; iteration < 10; iteration++)
        for (var slot = 0; slot < 2; slot++)
        {
            var engine = (iteration + slot) % 2; publish[engine]();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Measure("cold_requested_limit", engine, iteration, limit);
        }
        for (var iteration = 0; iteration < 30; iteration++) for (var slot = 0; slot < 2; slot++) Measure("warm_repeated_500", (iteration + slot) % 2, iteration, 500);
        for (var iteration = 0; iteration < 10; iteration++) for (var slot = 0; slot < 2; slot++)
        {
            var engine = (iteration + slot) % 2; publish[engine](); Measure("after_publication_500", engine, iteration, 500);
        }
        File.WriteAllText(Path.Combine(root, "results/v2-probe.json"), JsonSerializer.Serialize(new
        {
            metadata = new { size = 1000000, culture = "ja-JP", cold_samples = 10, warm_samples = 30, post_publication_samples = 10,
                behavior = "Lazy requested top count, at most 500 per generation; an increased limit upgrades under the same cancelable gate. First small-limit penalty from fixed500 v1 is preserved in previous raw data.",
                warning = "Focused probe, not replacement for previous full matrix; no Windows claim. Cold phase force-GCs outside timing. Publication is outside query timing and uses production CopyRecords." }, rows
        }, new JsonSerializerOptions { WriteIndented = true }));
        void Measure(string phase, int engine, int iteration, int limit)
        {
            using var process = Process.GetCurrentProcess(); var cpu = process.TotalProcessorTime.TotalMilliseconds;
            var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
            var allocated = GC.GetTotalAllocatedBytes(true); var start = Stopwatch.GetTimestamp();
            var actual = engines[engine]("", false, limit, token.Token);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds; var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
            var cpuMs = process.TotalProcessorTime.TotalMilliseconds - cpu;
            var gen0 = GC.CollectionCount(0) - gc0; var gen1 = GC.CollectionCount(1) - gc1; var gen2 = GC.CollectionCount(2) - gc2;
            if (!actual.Select(x => x.FullPath).SequenceEqual(expected.Take(limit))) throw new Exception("Exact order mismatch");
            rows.Add(new { phase, engine = engine == 0 ? "current_uncached" : "requested_top500_v2", iteration, limit, elapsed_ms = elapsed, allocated_bytes = bytes, cpu_ms = cpuMs, gen0, gen1, gen2 });
        }
        Console.WriteLine("PASS v2 probe: 140 timed samples, exact order equivalent");
    }
}
