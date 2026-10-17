# 実装検索コアの性能検証（2026-10-17）

対象はLinux .NET 8.0.32上の実C# `FileIndexService.Search`。基準はdevelop `5213f4b1eae5841721a4318b07cd8bae8f72af01` の変更していないSearchメソッドです。CPUはAMD EPYC 9V74、実行環境9論理CPU。100,000/1,000,000件の決定的合成レコード、8検索条件、各engine30回、4warmup、交互実行。検索上限500、ja-JP、実UI同様activeな未取消tokenを使用。

## 100万件（snapshot再利用導入時、空検索cache追加前）

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
- 空検索は中央値が改善してもp95が悪化。この不利な測定を保持したまま、以下の限定検証により世代内の要求件数cacheを採用しました。改善後の結果でこの測定を消しません。
- snapshot構築/fixture準備/結果検査は検索時間外。ファイル更新はこの測定中にありません。したがって、この測定の遅い空検索を初回snapshot構築や実watcher更新の時間と説明することはできません。この元測定にはsample別GC回数がないため、原因をGCと断定できません。以下の限定計測ではsortを省ける再検索と世代更新直後を分け、sample別GC回数も記録しています。
- Windowsの画面表示、実ディスク初回索引、更新反映、アプリ単独peak memory、Everything比較は未測定です。
- 初回試行に別の正確性試験とのCPU競合があったため、別ファイルのまま保管して採用結果とは分けました。本表はその競合終了後のclean runです。

## 再現

`tests/DIndex.Core.Benchmarks/run-benchmark.sh` を任意で使用します。通常CIには含めません。Linuxに公式.NET 8 SDK、Python 3、gitが必要です。

```bash
DINDEX_DOTNET_ROOT=/path/to/dotnet-sdk DINDEX_BENCH_OUTPUT=/path/to/output bash tests/DIndex.Core.Benchmarks/run-benchmark.sh 30 100000,1000000
```

基準sourceはgit historyから抽出し、基準Searchが同じことを実行時に検査します。実行時のsourceを出力先へ固定コピーし、全試行と等価性検査結果をresults.jsonに保存します。測定中に別のCPU-heavy taskを並行実行しないでください。


## 空検索の限定追加検証と採用（v1 / v2）

元の全条件測定を置き換えず、100万件の空検索だけを別の固定sourceで検証しました。生データ・凍結source・再現手順は [`tests/DIndex.EmptyCache.Benchmarks`](../tests/DIndex.EmptyCache.Benchmarks/README.md) に保存しています。比較表のmedianは中央2値の平均、p95はnearest-rankです。特に10 sampleのp95は最大値であり、大規模なtail推定ではありません。

### 条件

- first/cold: 新しいsnapshotを発行し、計測外でGCを実行した後、その世代の最初の検索を計測。cache作成のsort費用を含みます。プロセス起動直後やディスクcacheのcoldを意味しません。v1は候補を100件でJIT warmup済み、develop基準は既存100万件辞書で一度warmup済み。v2のcold群には初回呼出しも含めています。
- warm: 世代を変更せず同じ空検索を反復。v1は最初の1回を `warm_seed` として別途保持し、v2は直前cold群で計算済みの500件を再利用。初回sortをwarm成績に混ぜて隠していません。
- new-generation: 実コアの `CopyRecords` と `PublishedSnapshot` で再発行し、直後の初回検索を計測。再発行自体は検索計測外、強制GCなし。v1では再発行の時間/割当も別欄で保存（候補の中央値約110–117ms、約8MB）。実ファイル書込やwatcher通知は計測中に発生させていません。
- 同一プロセスで交互実行し、sampleごとに経過時間・process CPU・割当・Gen0/1/2回数と正確な結果順を記録。共有GCの影響はengine間で独立ではありません。非空検索のコードは今回変更していません。

### v1: 常に先頭500件を計算する候補（そのままでは不採用）

各engineのfirst/warm/new-generationは30回。小さい上限1/50は各5回。単位ms。

|条件|develop median / p95|cache前 median / p95|v1 median / p95|
|---|---:|---:|---:|
|first 500|102.356 / 121.853|36.627 / 42.130|37.131 / 41.942|
|warm 500|103.951 / 406.019|38.651 / 40.770|0.0042 / 0.0166|
|new-generation 500|100.394 / 398.489|36.047 / 43.467|36.408 / 375.829|
|first 1|未測定|29.244 / 30.351|37.762 / 40.386|
|first 50|未測定|36.283 / 39.683|36.044 / 40.534|

- v1の上限1は中央値約29%遅化し、上限50もp95が悪化。first 500の中央値もわずかに遅化しました。要求が1件でも500件を選ぶ費用があるため、v1はそのまま採用しませんでした。
- v1でも新世代直後のp95 375.829msが残りました。200msを超えた全9 sampleでGen2 GCが同時に記録されました（develop warm/new-generation各3、v1 new-generation 3）。これは関連を示しますが、GCの寄与時間を独立に測っていないため因果の証明ではありません。元の全条件測定のp95原因も遡って確定できません。
- 割当の中央値はcache前の500件検索20,004,392 bytesに対し、v1初回は約20,012,504 bytes、warmは4,096 bytes。process CPU中央値はcoldのcache前/v1が約40ms、develop約100ms、warm v1は測定粒度上0msでした。0msはCPU消費ゼロの保証ではありません。

### v2: 要求された件数だけ保存し、最大500件へ段階的に拡張（採用）

|条件|各engine sample数|cache前 median / p95 (ms)|v2 median / p95 (ms)|cache前 / v2 割当中央値 (bytes)|
|---|---:|---:|---:|---:|
|first 1|10|30.701 / 40.523|30.909 / 35.823|20,000,400 / 20,000,528|
|first 50|10|36.449 / 38.374|36.122 / 44.879|20,000,792 / 20,001,704|
|first 500|10|35.701 / 39.297|35.463 / 39.285|20,004,584 / 20,012,696|
|warm 500|30|39.799 / 46.739|0.0049 / 0.0145|20,004,392 / 4,096|
|new-generation 500|10|37.878 / 44.432|39.644 / 45.858|20,004,392 / 20,012,504|

- v2でも悪化は残ります。first 1中央値は約0.7%遅化、first 50 p95は約17.0%遅化、新世代直後は中央値約4.7%・p95約3.2%遅化しました。これらの負の結果とv1の全記録を保持しています。
- v2で大きい改善があるのは同一世代の反復検索です。計算済みprefixをコピーして返すため、warm 500は中央値0.0049ms、4,096 bytesでした。cold/new-generationのsort費用や約20MBの一時割当は残ります。要求件数を増やすと再計算が必要です。
- v2 probeではcache前warmの1 sampleが359.823msとなりGen2を伴いました（30件中1件なのでp95 46.739msには現れません）。v2 warmおよび両engineのnew-generation群にGen2はありませんでしたが、少数試行から更新直後tailの解消を保証できません。
- process CPU中央値はfirst 1が両方約30ms、first 500/new-generationが両方約40ms、warmがcache前約40ms、v2は測定粒度上0ms。全sampleのGC回数、CPU、最大値は生データと `evidence/summary.csv` にあります。
- **140/140** はv2の時間測定sampleが正確な更新日時順・同日時tie順を満たした数です。**146** は別の隔離版正確性試験のassertion数で、測定回数ではありません。最終の実製品sourceにリンクする通常CI回帰にはさらに契約を追加し、**151 assertions**を実行します（一覧上は1テスト群）。最終Linux全体は29テスト群成功、skip 0。Windows結果は別途CIで確認します。

### 境界と実装保証

各immutable snapshotで完成したprefixだけを保持し、最大**500個のSearchRecord参照**（x64で約4KBとarray/gateの固定費）に制限します。0以下の上限では保持せず、500超は元のsortへfallbackします。返却Listはコピーなので呼出し側の変更でcacheは壊れません。新しい世代への発行でcacheは無効化されます。取消可能なgateで同じ世代のcold計算を1つに抑え、取消/失敗の途中結果は公開しません。既存の小さい完成prefixは、拡張の取消後も維持します。

**初回および更新直後の高速化やp95上限は保証しません。** 頻繁な更新ではcacheを再利用できない場合があります。同時cold検索は最初の計算を待つ場合があります。旧snapshotは進行中の検索が参照している間だけ生存し、cacheはその世代に従属します。実watcherで更新日時が変わった後の順序更新・取消・同時実行・上限・同日時tie・返却List変更・世代切替を通常CIの回帰へ組み込みました。元の非空 `path_all` 中央値7.5%悪化はこの採用後も未解決のままです。Windows UI/実データでの性能保証や新しいWindows計測の主張は行いません。
