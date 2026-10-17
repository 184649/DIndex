using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using DIndex;

internal static partial class Program
{
    private static void Main(string[] args)
    {
        var output = args[0];
        var samples = args.Length > 1 ? int.Parse(args[1]) : 30;
        var sizes = args.Length > 2 ? args[2].Split(',').Select(int.Parse).ToArray() : new[] { 100000, 1000000 };
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ja-JP");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.CurrentCulture;
        var cases = new[] { ("empty", "", false), ("name_broad", "report", false), ("name_selective", "invoice_007", false),
            ("multiword_name", "budget 2026", false), ("path_all", "documents", true), ("path_selective", "item0007777", true),
            ("multiword_path", "report item0007781", true), ("japanese", "資料", false) };
        var results = new List<object>();
        var frozenSource = Path.Combine(output, "frozen", "FileIndexService.cs");
        var original = File.ReadAllText(Path.Combine(output, "frozen", "FileIndexService.cs.original"));
        var baselineSource = File.ReadAllText(Path.Combine(output, "frozen", "BaselineSearch.cs"));
        static string Method(string value)
        {
            var start = value.IndexOf("    public List<SearchRecord> Search(", StringComparison.Ordinal);
            var end = value.IndexOf("\n    public static void Open(", start, StringComparison.Ordinal);
            if (end < 0) end = value.LastIndexOf("\n}", StringComparison.Ordinal);
            return value[start..end].TrimEnd('\n', '\r');
        }
        if (Method(original) != Method(baselineSource)) throw new Exception("Baseline search differs from supplied develop source");
        var metadata = new
        {
            started_utc = DateTimeOffset.UtcNow,
            runtime = RuntimeInformation.FrameworkDescription, os = RuntimeInformation.OSDescription,
            processor_count = Environment.ProcessorCount, culture = CultureInfo.CurrentCulture.Name,
            core_sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(frozenSource))).ToLowerInvariant(),
            baseline_source_commit = "5213f4b1eae5841721a4318b07cd8bae8f72af01", baseline_method_verbatim_verified = true,
            samples_per_case_engine = samples, warmups = 4, result_limit = 500,
            cancellation = "Both engines receive an active uncanceled CancellationTokenSource.Token, as in the UI; CTS construction is outside timings.",
            corpus = "Deterministic cycle-4 arithmetic corpus: 20 filename patterns, Japanese/accented/repeated names, unique item paths; repeated updated ticks; no disk I/O.",
            scope = "Production FileIndexService.Search versus unchanged develop Search. Linux portable core only; not Windows UI, disk enumeration, update throughput, or Everything.",
            timing = "Alternating paired engines; source frozen before compilation; fixture/preprocessing/validation excluded. Nearest-rank p95 over 30 samples is noisy.",
            allocation = "GC.GetTotalAllocatedBytes(true) delta includes PLINQ workers. Both engines retain same record objects; snapshot is built outside per-query timing.",
            validation = "Every sample must match predicate and ordered score/name rank (updated-tick rank for blank), count and unique identity. Exact ID order reported, not required for equal-rank PLINQ ties.",
            tiered_compilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation")
        };
        foreach (var size in sizes)
        {
            var corpus = MakeCorpus(size);
            var build = Stopwatch.StartNew(); var baseline = new BaselineSearch(corpus); var baselineBuildMs = build.Elapsed.TotalMilliseconds;
            var snapshot = baseline.Snapshot();
            using var production = new FileIndexService(); build.Restart(); production.ReplaceAll(snapshot); var productionSnapshotMs = build.Elapsed.TotalMilliseconds;
            var engines = new Func<string, bool, int, CancellationToken, List<SearchRecord>>[] { baseline.Search, production.Search };
            foreach (var (category, query, path) in cases)
            {
                using var request = new CancellationTokenSource();
                var words = Words(query);
                var expected = words.Length == 0 ? snapshot.OrderByDescending(x => x.UpdatedTicks).Take(500).ToList()
                    : snapshot.Where(x => IsMatch(x, path, words)).OrderBy(x => Score(x, path, words)).ThenBy(x => x.Name).Take(500).ToList();
                var raw = new List<Sample>();
                for (var warm = 0; warm < 4; warm++) foreach (var engine in engines) engine(query, path, 500, request.Token);
                GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                for (var iteration = 0; iteration < samples; iteration++)
                for (var slot = 0; slot < 2; slot++)
                {
                    var engine = (iteration + slot) % 2;
                    var allocation = GC.GetTotalAllocatedBytes(true); var start = Stopwatch.GetTimestamp();
                    var actual = engines[engine](query, path, 500, request.Token);
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds; var allocated = GC.GetTotalAllocatedBytes(true) - allocation;
                    if (actual.Count != expected.Count || actual.Select(x => x.FullPath).Distinct().Count() != actual.Count) throw new Exception("Count/duplicate mismatch");
                    for (var i = 0; i < actual.Count; i++)
                    {
                        if (!IsMatch(actual[i], path, words)) throw new Exception("Predicate mismatch");
                        if (words.Length == 0 ? actual[i].UpdatedTicks != expected[i].UpdatedTicks
                            : Score(actual[i], path, words) != Score(expected[i], path, words) || Comparer<string>.Default.Compare(actual[i].Name, expected[i].Name) != 0)
                            throw new Exception($"Rank mismatch: {size}/{category}/{engine}/{i}");
                    }
                    raw.Add(new Sample(engine == 0 ? "develop" : "production", iteration, elapsed, allocated,
                        actual.Select(x => x.FullPath).SequenceEqual(expected.Select(x => x.FullPath))));
                }
                var summary = raw.GroupBy(x => x.Engine).Select(group => new
                {
                    engine = group.Key, median_ms = Median(group.Select(x => x.Ms)), p95_ms = Percentile(group.Select(x => x.Ms), .95),
                    median_allocated_bytes = Median(group.Select(x => (double)x.AllocatedBytes)), exact_id_order_samples = group.Count(x => x.ExactIdOrder),
                    rank_equivalent_samples = group.Count()
                }).ToArray();
                results.Add(new { size, category, query, path_search = path, baseline_build_ms = baselineBuildMs, production_snapshot_ms = productionSnapshotMs, summary, raw });
                Console.WriteLine($"{size} {category}: " + string.Join("; ", summary.Select(x => $"{x.engine} {x.median_ms:F3} ms p95={x.p95_ms:F3}, alloc={x.median_allocated_bytes:N0}")));
                File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { metadata, results }, new JsonSerializerOptions { WriteIndented = true }));
            }
            GC.KeepAlive(corpus); GC.KeepAlive(baseline); GC.KeepAlive(snapshot);
        }
    }
    private static double Median(IEnumerable<double> source) { var a = source.Order().ToArray(); return (a[(a.Length - 1) / 2] + a[a.Length / 2]) / 2; }
    private static double Percentile(IEnumerable<double> source, double p) { var a = source.Order().ToArray(); return a[(int)Math.Ceiling(a.Length * p) - 1]; }
    private sealed record Sample(string Engine, int Iteration, double Ms, long AllocatedBytes, bool ExactIdOrder);
}
