using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DIndex;

internal static class Program
{
    private static void Main(string[] args)
    {
        var root = args[0]; var samples = 30;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
        var records = Corpus.MakeCorpus(1000000);
        var baseline = new BaselineSearch(records);
        using var uncached = new FileIndexService(); using var cached = new CachedFileIndexService();
        var engines = new Func<string, bool, int, CancellationToken, List<SearchRecord>>[] { baseline.Search, uncached.Search, cached.Search };
        var names = new[] { "develop", "current_uncached", "lazy_top500" };
        Action[] publish = { () => { }, () => uncached.PublishBenchmarkRecords(baseline.BenchmarkItems), () => cached.PublishBenchmarkRecords(baseline.BenchmarkItems) };
        var rows = new List<Row>(); using var process = Process.GetCurrentProcess(); using var request = new CancellationTokenSource();
        // Small-fixture JIT warmup only; every first full-size generation query is reported separately.
        uncached.ReplaceAll(records.Take(100)); cached.ReplaceAll(records.Take(100));
        foreach (var engine in engines) engine("", false, 500, request.Token);
        var metadata = new
        {
            started_utc = DateTimeOffset.UtcNow, runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            processor_count = Environment.ProcessorCount, size = records.Length, samples_per_phase_engine = samples,
            expected_retained_cache_bytes = "At most 500 record references (~4000 bytes on x64) plus fixed gate/array overhead per live immutable generation; no global cache.",
            publication_fixture = "Candidate/current use the existing production CopyRecords against the same frozen ConcurrentDictionary, then the actual PublishedSnapshot constructor. No filesystem changes or watchers during timed benchmark. Develop dictionary is unchanged and has no publication step.",
            cold_first_generation = "Republish, force GC outside timing, then measure first full-size empty query; no full-size warmup. Gen-specific lazy sort cost is included.",
            warm_repeated = "One first query per engine is separately recorded as warm_seed, then 30 alternating repeated queries without publication or forced GC.",
            after_publication = "Publish a fresh immutable snapshot outside query timing, then immediately measure its first query, without forced GC. Publication metrics recorded separately.",
            limits = "Portable Linux synthetic search core only, not Windows or UI. Per-sample GC counts establish coincidence, not proof of causation. CPU is process-wide/coarse. Previous adverse raw benchmark is retained unchanged.",
            frozen_hashes = Directory.GetFiles(Path.Combine(root, "frozen"), "*.cs").ToDictionary(Path.GetFileName, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant())
        };
        var expected = baseline.Snapshot().OrderByDescending(x => x.UpdatedTicks).Take(500).Select(x => x.FullPath).ToArray();
        Row Measure(int engine, string phase, int iteration, int limit, bool publication, bool forceGc)
        {
            double publicationMs = 0; long publicationBytes = 0;
            if (publication)
            {
                var pa = GC.GetTotalAllocatedBytes(true); var ps = Stopwatch.GetTimestamp(); publish[engine]();
                publicationMs = Stopwatch.GetElapsedTime(ps).TotalMilliseconds; publicationBytes = GC.GetTotalAllocatedBytes(true) - pa;
            }
            if (forceGc) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
            var gc0 = GC.CollectionCount(0); var gc1 = GC.CollectionCount(1); var gc2 = GC.CollectionCount(2);
            var cpu = process.TotalProcessorTime.TotalMilliseconds; var allocation = GC.GetTotalAllocatedBytes(true); var start = Stopwatch.GetTimestamp();
            var actual = engines[engine]("", false, limit, request.Token);
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds; var allocated = GC.GetTotalAllocatedBytes(true) - allocation;
            var cpuMs = process.TotalProcessorTime.TotalMilliseconds - cpu;
            var row = new Row(phase, names[engine], iteration, limit, elapsed, allocated, cpuMs, GC.CollectionCount(0) - gc0, GC.CollectionCount(1) - gc1, GC.CollectionCount(2) - gc2, publicationMs, publicationBytes);
            if (!actual.Select(x => x.FullPath).SequenceEqual(expected.Take(limit))) throw new Exception($"Exact stable-empty-order mismatch: {phase}/{engine}/{iteration}");
            rows.Add(row); return row;
        }
        void Save() => File.WriteAllText(Path.Combine(root, "results/focused.json"), JsonSerializer.Serialize(new { metadata, rows }, new JsonSerializerOptions { WriteIndented = true }));
        for (var i = 0; i < samples; i++) for (var slot = 0; slot < 3; slot++) Measure((i + slot) % 3, "cold_first_generation", i, 500, true, true);
        Save(); Console.WriteLine("Cold first-generation complete");
        for (var engine = 0; engine < 3; engine++) Measure(engine, "warm_seed", 0, 500, true, true);
        for (var i = 0; i < samples; i++) for (var slot = 0; slot < 3; slot++) Measure((i + slot) % 3, "warm_repeated", i, 500, false, false);
        Save(); Console.WriteLine("Warm repeated complete");
        for (var i = 0; i < samples; i++) for (var slot = 0; slot < 3; slot++) Measure((i + slot) % 3, "after_publication", i, 500, true, false);
        // Report the cold limit=1/50 cost explicitly: computing a bounded top500 can cost more than Take(1).
        foreach (var limit in new[] { 1, 50 })
            for (var i = 0; i < 5; i++) foreach (var engine in new[] { 1, 2 }) Measure(engine, "cold_small_limit", i, limit, true, true);
        Save();
        foreach (var group in rows.GroupBy(x => (x.Phase, x.Engine, x.Limit)))
        {
            var times = group.Select(x => x.Ms).Order().ToArray();
            Console.WriteLine($"{group.Key}: median={times[times.Length / 2]:F3} ms; p95={times[(int)Math.Ceiling(times.Length * .95) - 1]:F3} ms; alloc={group.Average(x => x.Allocated):N0}; gc2_samples={group.Count(x => x.Gen2 > 0)}");
        }
    }
    private sealed record Row(string Phase, string Engine, int Iteration, int Limit, double Ms, long Allocated, double CpuMs, int Gen0, int Gen1, int Gen2, double PublicationMs, long PublicationAllocated);
}
