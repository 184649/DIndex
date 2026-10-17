# Progressive index publication: regression checks and synthetic timing

Date: 2026-10-17. Baseline source: published commit `9dfa31626f37530f9e0215b19bf42610fec3982a` before this change. Exact source SHA-256 values and every measured run are in `initial-results-benchmark-results.json`. The rejected always-preview candidate's raw results remain in `initial-results-geometric-benchmark-results.json`.

## Final behavior and bounds

- An unchanged scope with a nonempty baseline already supplies searchable results. It skips preview copying and keeps its existing active watcher publishing until the final rebuild commits. Files missed by that baseline and not discovered through its watcher wait for final enumeration.
- An empty baseline or changed/expanded scope starts a separate preview publisher alongside the initial scan, after watcher setup. Its first check is after 250 ms; this is not a promise of 250 ms from the user's click.
- First preview includes at most 512 discovered records, merged with all allowed prior records. Searches reuse the immutable preview. `IsRebuilding` and `IsDirty` remain true.
- Later previews include all then-discovered entries, no more often than once per second, and only after discovered count reaches twice the prior discovered count or twice the retained baseline count, whichever is larger. The minimum next threshold is 512.
- This growth policy bounds aggregate preview copying to O(retained + scanned), rather than copying a million-entry snapshot on every progress tick. A slow tail below the next threshold waits for final publication. After a scope change retaining 1M records, fewer than 2M scanned records means only the first 512 discovered records are merged before completion. A preview is not a guarantee that every already-discovered match is visible yet.
- The final snapshot still comes after complete traversal, unreadable-record preservation, and a bounded watcher replay. Remaining queued/failed watcher work keeps the index dirty as before.
- Cancellation/failure restores the safe pre-scan baseline with a newer snapshot version. Scope narrowing remains filtered. Superseded work cannot publish or restore over a newer request. When previews are enabled, an older active watcher keeps reconciling but cannot publish over a preview; it publishes staged work if the rebuild is canceled.

## Deterministic regressions

The final core executable has 39 passing cases, 0 skipped on this host: the existing 29 cases plus ten preview/selective-policy cases. The new cases use an explicit progress-callback barrier rather than guessing whether disk traversal is still running.

1. First 512 results are searchable while the scan task is blocked; the next preview includes all 1,400 discovered records; prior readers remain immutable; final create/update/delete replay matches the filesystem without duplicate paths.
2. Cancellation after preview restores cached records and the correct dirty state.
3. A new scope hides the old preview immediately; a late old publisher cannot overwrite it.
4. Canceling a queued same-scope successor restores the true baseline, not the previous partial snapshot.
5. An injected unreadable-path marker preserves allowed fallback during and after previews, while narrowed scope and exclusions remain enforced. The existing real Unix-permission test also passes.
6. With an empty active baseline, older watcher publication cannot overwrite a preview; its staged changes publish after cancellation without another event.
7. Scan callback failure after preview restores the baseline. Throwing snapshot/count subscribers cannot strand a preview.
8. A canceled async cache restore that supersedes a preview cannot leave that canceled preview published.
9. An unchanged, nonempty scope performs no redundant preview publication; its old watcher still exposes changes while the rebuild is blocked.
10. Scope expansion preserves old results and makes new-root matches searchable before traversal completes.

`initial-results-core-test.log` contains the final run. Use `run-core-tests.sh` for the existing .NET 8 core-only runner or the normal .NET 10 test project where installed. Linux core verification does not validate WPF, Windows ACLs, or the production Windows installer.

## Timing method

Fixture: 400,000 real files, 800 subdirectories, 500 seven-byte files per directory, one root, directory results disabled. Debian 13, .NET 8.0.32, Linux tmpfs. The host reports AMD EPYC 9V74 80-Core Processor, x64; .NET Environment.ProcessorCount is 9, giving 8 scan workers. CPU/count were checked after timing, not independently sampled in each run. Metadata was warm from fixture creation and repeated scans; “empty index” does not mean a cold disk cache. Each run uses a fresh process. Initial three-pair before/after orders alternate. The eight extra retained-baseline confirmation pairs use balanced, seeded-randomized order.

Latency runs perform a real `file-` filename search, limit 1, from the first publication callback. The old code searches 400K records only after finishing; the new empty-index code searches the first 512. Throughput runs perform no search, measuring scan overhead independently of query cost that can overlap traversal. These are descriptive small-sample measurements, not a Windows C-drive or GUI speed claim. Do not use latency-run totals to claim traversal throughput gains.

The 1M-retained stress baseline contains synthetic cached records in the same root, absent from the current fixture; final results correctly contain exactly the current 400K files. Both versions make the cached baseline searchable immediately. “First publication” in that stress case means the next publication after rebuild starts, not the first time old matches become available.

## Final selective policy: initial three-pair sample

| Median | Before | After |
| --- | ---: | ---: |
| Empty index: first snapshot publication, latency runs | 1,215 ms | 427 ms |
| Empty index: first verified search response | 1,775 ms | 450 ms |
| Empty index: total traversal, no search | 1,201 ms | 1,221 ms |
| Empty index: allocated bytes, no search | 415.48 MB | 415.52 MB |
| Empty index: process peak working set, no search | 271.26 MB | 272.76 MB |
| 1M retained: total traversal, no search | 1,025 ms | 1,129 ms |
| 1M retained: allocated bytes, no search | 415.36 MB | 415.51 MB |
| 1M retained: process peak working set, no search | 592.64 MB | 591.17 MB |

Raw three-run timing values and nearest-rank p95 are below. With n=3, this p95 is simply the observed maximum, not a reliable tail-latency estimate.

| Metric | Before runs, ms | After runs, ms | Before / after sample p95 |
| --- | --- | --- | --- |
| Empty-index first verified search | 1939.804, 1737.935, 1774.618 | 373.404, 468.595, 449.926 | 1939.804 / 468.595 ms |
| Empty-index traversal without search | 1200.579, 1290.019, 1183.201 | 1138.751, 1268.132, 1221.337 | 1290.019 / 1268.132 ms |
| 1M-retained traversal without search | 967.495, 1024.887, 1076.996 | 1055.550, 1180.818, 1128.756 | 1076.996 / 1180.818 ms |

Empty-index traversal increased about 1.7% in this sample. The retained sample still showed a 10.1% median increase despite eliminating preview merging, so it was not treated as proof that the timing penalty had disappeared.

## Eight-pair retained-baseline confirmation

- Before traversal times, ms: 1169.061, 1063.796, 1076.054, 1076.340, 1234.364, 1177.968, 1146.468, 1082.445.
- After traversal times, ms: 1213.662, 1111.214, 1248.457, 1170.895, 1222.470, 1152.997, 1084.582, 1013.945.
- Median: 1114.457 → 1161.946 ms, a 4.3% increase. Median of paired percentage changes: +1.4%.
- Ranges overlap: 1063.796–1234.364 → 1013.945–1248.457 ms.
- Nearest-rank p95: 1234.364 → 1248.457 ms; with n=8 this is again the sample maximum, not a robust tail estimate.
- Median allocated bytes: 415.412 → 415.415 MB. Every retained run emitted exactly one final snapshot, with no preview merge.
- Median process peak working set: 595.32 → 590.18 MB. This includes the baseline, runtime and GC; it is not isolated snapshot memory.

The selective policy removes the extra retained-preview allocation. Timing varies and a positive median delta remains; these runs do not establish statistically zero overhead or a general traversal speedup.

## Rejected candidate and retained cost

The first candidate also merged a preview during unchanged-scope rebuilds. Its three-pair retained median was 1035.366 → 1144.360 ms (+10.5%), allocations 415.41 → 423.35 MB (+7.94 MB), and process peak working set 588.30 → 603.70 MB (+15.40 MB). All its raw runs and source hashes remain in `initial-results-geometric-benchmark-results.json`; they are not overwritten by the final policy's results.

That candidate was rejected for unchanged-scope baselines because the first 512 updates did not justify repeatedly duplicating a large already-searchable baseline. Changed/expanded scope still uses a bounded merge to preserve old allowed results while exposing new discoveries, so this cost has not been declared universally eliminated.

## Reproduction and exact inputs/outputs

The repository root was `/workspace/shared/dindex-implementation-20261017`; fixture root was `/tmp/dindex-initial-result-fixture`. Each fixture path is `d000` through `d799` containing `file-000.txt` through `file-499.txt`, with the seven UTF-8 bytes `fixture` in every file. The temporary fixture was removed after measurements.

Input implementation files were `/tmp/DIndex.FileIndexService.before-previews.cs` and `src/DIndex/FileIndexService.cs`. The shared driver was `tests/DIndex.Core.Tests/InitialResultsBenchmark.cs`, linked with `src/DIndex/Models.cs`, `GlobalUsings.cs`, and `ClipboardStub.cs`. Compiled outputs were `/tmp/dindex-initial-benchmark-before/InitialResultsBenchmark.dll` and `/tmp/dindex-initial-benchmark-after/InitialResultsBenchmark.dll`. Persisted outputs are the two JSON reports, this document, and `initial-results-core-test.log` alongside the driver.

Pinned baseline source: [FileIndexService.cs at 9dfa316](https://github.com/184649/DIndex/blob/9dfa31626f37530f9e0215b19bf42610fec3982a/src/DIndex/FileIndexService.cs). From an authenticated checkout containing that commit, extract and verify it without relying on a moving branch:

```sh
git show 9dfa31626f37530f9e0215b19bf42610fec3982a:src/DIndex/FileIndexService.cs > /tmp/DIndex.FileIndexService.before-previews.cs
sha256sum /tmp/DIndex.FileIndexService.before-previews.cs
# Expected: 076eb86bcb2fa47068538e21aa4fa4bffe612d765e77d61a0ae9c9a67792b9d6
```

The final selective implementation SHA-256 is `739d466e19eb6f269f71f5142f119f9c5bd19430f25e005e4c1ffdc645f38a37`. The rejected candidate's hash is retained in its raw JSON report.

Create a new isolated fixture (400K files; allow roughly 1.6 GB on a 4 KiB-block filesystem):

```sh
python3 - <<'PYFIXTURE'
from pathlib import Path
import tempfile
root = Path(tempfile.mkdtemp(prefix="dindex-initial-results-"))
for directory in range(800):
    parent = root / f"d{directory:03}"
    parent.mkdir()
    for number in range(500):
        (parent / f"file-{number:03}.txt").write_bytes(b"fixture")
print(root)
PYFIXTURE
```

With that prepared fixture and the existing .NET 8 SDK:

```sh
bash tests/DIndex.Core.Tests/run-initial-results-benchmark.sh FIXTURE_DIRECTORY 0 /path/to/FileIndexService.cs /tmp/dindex-benchmark true
bash tests/DIndex.Core.Tests/run-initial-results-benchmark.sh FIXTURE_DIRECTORY 1000000 /path/to/FileIndexService.cs /tmp/dindex-benchmark false
```

The standalone driver is excluded from the core test project and does not modify fixture files. `DINDEX_DOTNET_ROOT` overrides the existing SDK path.
