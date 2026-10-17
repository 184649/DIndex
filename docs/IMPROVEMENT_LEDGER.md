# 改善台帳

対象: develop `5213f4b1eae5841721a4318b07cd8bae8f72af01` を起点にした専用ブランチ。
この台帳の「実装」は実機検証完了を意味しません。Windows GUI・実ボリューム・Everything比較は別の受入条件です。

|項目|判断/状態|受入条件|
|---|---|---|
|検索ごとの全件コピー|snapshot再利用を実装、C#回帰通過|同じ世代で配列再構築なし、検索結果等価|
|検索取消|全経路に反映中|空検索/小集合/並列/更新競合で取消|
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
|安全な試用profile|DINDEX_DATA_DIR実装|通常LocalAppDataと分離|
|多重起動の保存競合|同profileのnamed mutex実装|2プロセス目が同profileへ書かない|
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

- production sourceを直接リンクする索引コア25/25、保存・更新81/81のLinux C#試験を通過。
- 本体/更新器のWindows CIは専用branchへcommit後に確認する段階。GUI/Shellの実操作は未確認。
- C#構文解析19ファイルでsyntax errorなし、XAML/project XML構造検査通過。これはWindows semantic buildの代替ではありません。
- 最新実装の速度比較を固定sourceで計測中。以前の試作倍率を製品の保証値として記載しません。
- 同じ検索範囲の再索引では旧snapshotを保持。検索範囲を変えた場合は旧世代を停止して対象を先に絞り、取消されても削除済み対象を復活させません。
- 同期ファイルシステムI/OはOSが返すまで即中断できません。走査枠は1世代に制限し、連打による並列世代/メモリ増殖を防止します。
- 索引変更が検索より速く続いても、実行中snapshotの結果を更新中表示で返し、その後再検索します。古い入力の検索結果は表示しません。
