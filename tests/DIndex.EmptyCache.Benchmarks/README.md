# Focused empty-query cache experiment

These are frozen historical measurement fixtures, not additional app implementations. The app uses `src/DIndex/FileIndexService.cs`; CI regression tests link that real source through `tests/DIndex.Core.Tests`. No application references this directory.

- `evidence/focused-v1.json`: unchanged 293 recorded samples, fixed-top-500 candidate v1 versus uncached implementation and unchanged develop Search. v1 was **not** adopted unchanged.
- `evidence/v2-probe.json`: unchanged 140 samples, requested-prefix candidate v2 versus uncached implementation. v2 is adopted.
- `evidence/summary.csv`: derived median (mean of the two middle observations), nearest-rank p95, maximum, median allocated bytes, median process CPU and all GC counts. v1's original console log used the upper-middle observation for its `median`; use the raw JSON or this derived CSV for comparisons in PERFORMANCE.md.
- `evidence/tests-v2.log`: 146 separate correctness assertions against the isolated v2 candidate. This is not a count of performance samples. The final production-linked CI fixture adds further contracts.
- `frozen/`: exact v2 evaluation sources, including uncached core, actual Models, corpus and unchanged develop Search method. The only evaluation hook on each core invokes production `CopyRecords` and constructs a real `PublishedSnapshot` from an already frozen dictionary. The hook is absent from the application.
- `variants/v1/`: preserved v1 implementation and 143-assertion fixture. The default frozen candidate is v2.

The v1 benchmark runs 30 cold-generation, 30 warm-repeated, 30 post-publication samples per engine, plus one separately reported warm seed per engine and five limit-1/50 samples per candidate. All first full-generation cache calculations are measured. It warms the uncached/cached candidates with 100 records beforehand; its baseline delegate is already bound to the million-record dictionary, so baseline JIT warmup searches that full dictionary once. Thus “cold” means a fresh **snapshot's cache**, not process startup or disk-cache coldness. v2 uses 10 fresh-generation samples each for limits 1/50/500 per candidate, 30 warm-limit-500 samples each, and 10 post-publication samples each. No v2 warmups are omitted from the cold groups.

Both run on one million synthetic records, limit 500 unless stated, ja-JP and an active uncanceled token, alternating engines in one process. Cold groups force GC outside query timing. Warm groups reuse the same generation. Post-publication groups call actual snapshot copying/publication outside query timing and immediately search without forced GC. They do not simulate a filesystem write or watcher callback during the timed query. v1 reports publication time/allocation separately; v2 does not. CPU is process-wide and coarse. GC is shared across engines, so per-sample counters show coincidence, not causal isolation. All output lists are checked for exact stable timestamp order, including tied records.

## Reproduce (optional Linux .NET 8 SDK and Python 3)

Use a fresh output directory per run; the script refuses to overwrite captured results. Do not run other CPU-heavy work at the same time. This focused harness uses historical frozen sources rather than current production, so future edits cannot silently change the evidence. To test current production, run the Core.Tests project/runner.

```bash
DINDEX_DOTNET_ROOT=/path/to/dotnet-sdk DINDEX_EMPTY_BENCH_OUTPUT=/tmp/empty-v1 bash tests/DIndex.EmptyCache.Benchmarks/run-focused.sh v1
DINDEX_DOTNET_ROOT=/path/to/dotnet-sdk DINDEX_EMPTY_BENCH_OUTPUT=/tmp/empty-v2 bash tests/DIndex.EmptyCache.Benchmarks/run-focused.sh v2
```

No timings are Windows/UI claims. Initial and immediately post-update latency are not guaranteed by a per-generation cache. Adverse full-matrix, v1 and v2 results are retained and discussed in `docs/PERFORMANCE.md`.
