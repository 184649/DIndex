# 改善台帳

対象: develop `5213f4b1eae5841721a4318b07cd8bae8f72af01` を起点にした専用ブランチ。
この台帳の「実装」は実機検証完了を意味しません。Windows GUI・実ボリューム・Everything比較は別の受入条件です。

|項目|判断/状態|受入条件|
|---|---|---|
|検索ごとの全件コピー|snapshot再利用を実装、C#回帰通過|同じ世代で配列再構築なし、検索結果等価|
|検索取消|全経路に実装、回帰通過|空検索/小集合/並列/更新競合で取消|
|古い検索結果の上書き|UI request世代照合、snapshot更新を表示して再検索|古い入力の結果で上書きしない。ファイル更新中は一致したsnapshot結果を表示し、更新中表示と再検索で飢餓を防止|
|UI通知/結果一括反映|150ms通知集約とItemsSource一括交換実装|ワーカーがDispatcher.Invokeで待たない|
|初回単一root並列化|実装、C#回帰通過|キュー一時空でworkerが終了しない|
|再索引中の検索/世代隔離|実装、C#回帰通過|旧snapshot保持、取消済みgenerationは公開しない|
|監視除外/rename/delete/moved-in|実装、C#回帰通過|子孫と除外ancestor、移入subtreeの整合|
|監視overflow/不鮮明な状態|実装、C#回帰通過|再整合またはdirty表示、正常に見せない|
|再解析ポイント循環|実装、C#回帰通過|リンク対象無制限追跡なし|
|cache文字列破損|実装、C#回帰通過|Windowsパス、制御文字の往復|
|cache取消/部分書込|実装、C#回帰通過|旧cache保持、完全検証後commit、失敗伝達|
|設定保存/backup|実装、C#回帰通過|破損原本保持、正常backup回復|
|更新ZIP/置換rollback|実装、C#回帰通過|ハッシュ/構造検証、置換失敗で旧本体復旧|
|安全な試用profile|preview既定DIndex-Preview、DINDEX_DATA_DIR実装|通常LocalAppDataと分離|
|多重起動の保存競合|同profileのnamed mutexとファイルlock実装|パス別名/別sessionでも同profileへ同時書込しない|
|bounded trigram|既定導入見送り|選択的条件は高速だが追加88MiB/100万件、構築約5.85秒。更新時増幅/メモリ上限/適応plannerを実データ検証後に選択|
|heap top-k|一律採用しない|48条件中25で遅化した試作を既定にしない|
|入力refinement cache|一律採用しない|6系列中2で遅化、初回検索遅化。80ms debounce込みで次段階評価|
|MFT/USN native索引|未実装、Windows検証が先行条件|JournalID/volumeID/checkpoint、hardlink、journal欠落の回復、権限を守る|
|Everything SDK連携|候補、未実装|依存アプリ/ライセンス/実配布条件を整理。ユーザーPCへ依存ソフト無断導入しない|
|全文/メタデータ追加索引|候補、未実装|名前検索の速さを損なわない独立・容量制限方式、実需要確認|
|Everythingより上位/速い主張|未判定|同一Windows、対象範囲、条件、ソートで入力→表示/正確性/メモリ比較|

## 検証の段階

1. ソース、実C#コアの合成ファイル試験
2. public repository の標準 windows-2022 runner で本体/更新器ビルドとcore test。専用branchのみ、15分上限、read-only権限、artifact/cacheなし。
3. Windows GUI・Shell・監視・高DPI・実ファイル負荷の手動確認
4. 同条件Everything比較

現段階で未実施の段階を通過済みと表記しない。既存配布ZIPは旧版であり、このbranchの実装を含まない。

## 2026-10-17 初期実装検証

- production sourceを直接リンクする索引コア29/29、保存・更新93/93のLinux C#試験を通過。
- commit 3d5a9ce のWindows CIは本体/更新器ビルド（警告0・エラー0）、索引27実行＋ACL 1 skip、保存93試験を通過。空検索cacheの追加分は最終commitのCIで再確認。GUI/Shellの実操作は未確認。
- C#構文解析19ファイルでsyntax errorなし、XAML/project XML構造検査通過。これはWindows semantic buildの代替ではありません。
- 固定sourceの100万件・30反復比較では選択的検索が約3倍。一方、全path一致の中央値7.5%遅化と空検索p95悪化を記録。詳細と採否は PERFORMANCE.md を参照。実アプリ体感の保証値ではありません。
- 同じ検索範囲の再索引では旧snapshotを保持。検索範囲を変えた場合は旧世代を停止して対象を先に絞り、取消されても削除済み対象を復活させません。
- 同期ファイルシステムI/OはOSが返すまで即中断できません。走査枠は1世代に制限し、連打による並列世代/メモリ増殖を防止します。
- 索引変更が検索より速く続いても、実行中snapshotの結果を更新中表示で返し、その後再検索します。古い入力の検索結果は表示しません。

### 追加レビューで修正

- 起動失敗時は明示終了。不可視プロセスと保持されたprofile lockを残さない。
- 設定/cacheの復旧・失敗警告を一時statusとは別に表示。
- 開発版は本番profileから分離し、自動更新対象外。通常版のsilent更新確認はballoonにとどめる。
- 更新器は検証・同volume stagingが終わってreadyを返した後、proceedをcommit pointとして本体終了。準備失敗なら本体を閉じない。
- 設定ファイルの外部編集を終了時に上書きしない。保存前の外部変更・削除・競合検知を実装し、回帰試験を通過。

### 空検索の最終採用

- 世代内の要求prefixを最大500件だけ保持するcacheを採用。通常CIに151 assertionsの回帰群を追加。Linuxは29群成功。
- 同世代の反復検索を大幅に短縮する一方、初回・更新直後のsortと一時割当は残ります。v1/v2の不利結果も PERFORMANCE.md と再現データへ保存しました。

## 実機フィードバック対応: 初回結果表示（1.1.1-preview.1）

- 本人確認で C:\\ など広い対象を指定してもすぐ結果が出ないことが判明。既存の検索コア倍率は、索引完成後の検索だけを測定しており、この問題を解決した証拠ではありません。
- 原因: `BuildAndPublishAsync` はディレクトリー走査、監視差分反映、全レコードコピーが完了するまで新しいsnapshotを公開していませんでした。250ms間隔の進捗通知は文字列だけでした。
- 実装: 空の索引または対象変更時に、未完了であることを示した段階的な検索可能snapshot。同一対象で既存結果がある再走査では巨大な途中コピーを省きます。最終索引の正確性、取消/世代/対象除外、既存結果保持を含む41コアケースがLinuxで通過。詳細と不利な測定も tests/DIndex.Core.Tests/INITIAL_RESULTS_BENCHMARK.md に保存。Windows/GUI確認とは区別します。
- 未達: 未走査の場所にあるファイルを直ちに検索すること、Cドライブ全体の索引完了時間、Windows描画を含む初表示、Everythingとの同条件比較。
- EverythingのNTFS索引は低レベルアクセスとUSN Journalを使用する方式で、サービスまたは管理者権限が必要です。現在の通常フォルダー列挙＋FileSystemWatcherとは仕組みが異なります。段階表示は全走査を不要にするものではありません。公式根拠: https://www.voidtools.com/faq/

## 2026-10-18 実機フィードバック対応: 閉じる際の常駐通知

- ウィンドウを閉じるたびに表示していた「タスクトレイで動作中」の通知呼出しだけを削除。閉じる操作の取消と非表示、トレイからの再表示・終了、更新通知は維持します。
- `python tests/verify_window_close.py` で閉じるイベントの接続、通知なしの取消・非表示、トレイの再表示・終了、更新通知保持を検査します。これはソース契約の検査であり、Windows GUI実操作の代替ではありません。
- Linux検証: 新規ソース契約4/4（修正前は通知なし契約が失敗）、索引コア41/41、保存・更新安全性106/106、packaging契約17/17が通過。C#構文39ファイルにエラーなし。Windows本体ビルド・MSI・GUI受入は別途確認が必要です。
- Windowsでの反復close/reopenとAlt+F4は `WINDOWS_ACCEPTANCE.md` の受入条件に追加。version・更新channelの判断は別に行います。

## 通常公開版からの更新互換（1.1.2）

- 公開1.0.1の実EXEを実行せず静的解析し、固定更新feedが184649/DIndex/main/version.json、旧Updater引数がprocessId/appPath/appZip URL/restartであることを確認。
- 旧版は毎回Updaterを取得するため、新Updaterに今回release専用のURL/SHA-256/versionを埋め込む互換入口を追加。hashなしで任意packageを受け付けるfallbackは追加しない。
- 通常版は従来のLocalAppData/DIndexを使用。preview/MSIを通常portableへ無断移行しない。旧版の準備前終了は変えられず、元プロセスの同一性を確証できない失敗時は自動再起動を控え、旧EXEを保持する。
- 追加回帰は旧引数/固定metadata/改ざん/版比較/現行handshake/保存先継承/プロセス同一性を対象とする。Windows生成packageと公開URL経由の旧EXE置換は独立したリリースゲート。
- 通常CIはread-onlyを維持。今回の公開だけ承認済みのjob-scoped書込権限を使用し、公開後撤去する。配布tagと実ソースcommitを区別し、実ソースarchiveと出典を公開する。
