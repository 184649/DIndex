# 実装検索コアの性能検証（2026-10-17）

対象はLinux .NET 8.0.32上の実C# `FileIndexService.Search`。基準はdevelop `5213f4b1eae5841721a4318b07cd8bae8f72af01` の変更していないSearchメソッドです。CPUはAMD EPYC 9V74、実行環境9論理CPU。100,000/1,000,000件の決定的合成レコード、8検索条件、各engine30回、4warmup、交互実行。検索上限500、ja-JP、実UI同様activeな未取消tokenを使用。

## 100万件

単位ms。割当はprocess全体のGC.GetTotalAllocatedBytes差分でPLINQ workerを含みます。

|条件|旧median|新median|旧p95|新p95|旧割当MiB|新割当MiB|
|---|---:|---:|---:|---:|---:|---:|
|empty|110.66|41.40|128.55|311.43|26.71|19.08|
|name_broad|321.61|263.92|656.53|592.67|61.72|54.09|
|name_selective|107.79|35.33|120.71|44.73|8.06|0.43|
|multiword_name|137.80|63.10|155.02|71.13|15.53|7.90|
|path_all|980.28|1053.62|1501.21|1373.16|151.91|144.28|
|path_selective|104.77|29.72|128.74|40.49|7.83|0.20|
|multiword_path|110.98|36.71|127.65|60.02|7.83|0.20|
|japanese|166.11|87.91|479.20|142.23|23.40|15.77|

## 採否と限界

- 16条件中14条件で中央値が改善、p95は15条件で改善。960/960測定で一致条件と順序付き順位（score/name、空検索は更新日時）を確認。同順位の完全同順IDは全条件で保証していません。
- 全path一致は中央値7.5%遅化。無条件の「全検索高速化」は達成していません。正確な取消処理とsnapshot再利用を基礎実装とし、広い検索の選択的な別方式は未採用です。
- 空検索は中央値が改善してもp95が悪化。この不利な測定を保持し、世代内結果cacheの限定追加検証を進めています。改善後の結果でこの測定を消しません。
- snapshot構築/fixture準備/結果検査は検索時間外。ファイル更新はこの測定中にありません。したがって、この測定の遅い空検索を初回snapshot構築や実watcher更新の時間と説明することはできません。GCとsortの寄与は追加計測で分離します。
- Windowsの画面表示、実ディスク初回索引、更新反映、アプリ単独peak memory、Everything比較は未測定です。
- 初回試行に別の正確性試験とのCPU競合があったため、別ファイルのまま保管して採用結果とは分けました。本表はその競合終了後のclean runです。

## 再現

`tests/DIndex.Core.Benchmarks/run-benchmark.sh` を任意で使用します。通常CIには含めません。Linuxに公式.NET 8 SDK、Python 3、gitが必要です。

```bash
DINDEX_DOTNET_ROOT=/path/to/dotnet-sdk DINDEX_BENCH_OUTPUT=/path/to/output bash tests/DIndex.Core.Benchmarks/run-benchmark.sh 30 100000,1000000
```

基準sourceはgit historyから抽出し、基準Searchが同じことを実行時に検査します。実行時のsourceを出力先へ固定コピーし、全試行と等価性検査結果をresults.jsonに保存します。測定中に別のCPU-heavy taskを並行実行しないでください。
