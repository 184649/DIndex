# DIndex

Windows用ファイル名・パス検索アプリ。これは速度・信頼性改善の開発ブランチです。mainの配布ZIPとは別物です。

## このブランチ

検索snapshotの再利用、取消/世代管理、結果の一括表示、旧索引を保持する再構築、監視整合、cacheと設定の保存保護を改善しています。詳細と残件は [改善台帳](docs/IMPROVEMENT_LEDGER.md) を参照してください。

Everythingより速いという実測結果はありません。LinuxのC#コア検証・Windowsビルド・実機GUI検証は別の検証段階です。最終の検査結果は台帳に記録します。

## 必要なもの

- Windows 10/11 x64
- ビルドにはMicrosoft公式 .NET 10 SDK
- 通常ビルド版の実行には .NET 10 Desktop Runtime。self-contained配布版は不要

## ソースから起動

PowerShellで、既存作業フォルダーと別の場所へcloneします。

```powershell
git clone --branch improve/performance-reliability-20261017 --single-branch https://github.com/184649/DIndex.git DIndex-preview
cd DIndex-preview
dotnet build DIndex.sln -c Release --no-incremental
```

最初の確認では通常のDIndex設定を使わず、専用のテストデータフォルダーと小さな検索rootを設定してください。下記は新規の一時フォルダーだけにファイルを作成します。

```powershell
$testHome = Join-Path $env:TEMP ('DIndex-preview-' + [guid]::NewGuid())
$env:DINDEX_DATA_DIR = Join-Path $testHome 'profile'
$testRoot = Join-Path $testHome 'files'
New-Item -ItemType Directory -Path $env:DINDEX_DATA_DIR, $testRoot | Out-Null
Set-Content -LiteralPath (Join-Path $testRoot 'notes-報告書.txt') -Value 'test'
@{ SearchRoots=@($testRoot); SearchPath=$true; IncludeDirectories=$true; ResultLimit=500; ExcludeFolderNames=@('.git','node_modules','bin','obj') } |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $env:DINDEX_DATA_DIR 'settings.json') -Encoding utf8
& '.\src\DIndex\bin\Release\net10.0-windows\DIndex.exe'
```

起動直後は画面を表示せず、タスクトレイに常駐します。隠れているアイコンも確認し、DIndexをダブルクリックして開いてください。ウィンドウを閉じても常駐します。終了はトレイの「終了」です。

この開発版の既定保存先は `%LOCALAPPDATA%\DIndex-Preview`、通常版は `%LOCALAPPDATA%\DIndex` です。`DINDEX_DATA_DIR` はそのプロセスの保存先を明示的に変更します。上のPowerShellを閉じると子プロセス用設定は引き継がれなくなります。既存データへ切り替える前に正常なbackupを保管し、アプリを終了してください。

## 確認方法

1. `notes`、`報告書`、空検索、パス/名前切替を試す。
2. テストrootにファイルを追加し、rename・削除・subfolder移入が検索へ反映されることを確認する。
3. 入力と再索引を連打して、古い結果が後から戻らないことを確認する。
4. トレイ終了→再起動でcache読込、その後のバックグラウンド再整合を確認する。
5. [Windows受入確認](docs/WINDOWS_ACCEPTANCE.md) の取消・破損・除外・リンク・高DPI・負荷試験を行う。

実装を直接リンクする自動テスト:

```powershell
dotnet run --project tests/DIndex.Core.Tests/DIndex.Core.Tests.csproj -c Release
dotnet run --project tests/StorageSafetyTests/StorageSafetyTests.csproj -c Release
```

実行済み結果は改善台帳を参照してください。GUIで未確認の項目は完了扱いにしません。

## 配布用ビルドと更新

既存の `build.bat` はバージョン指定・self-contained publish・ZIP作成という流れを維持しています。新しいversion manifestにはZIPのSHA-256を追加します。ハッシュのない旧manifestからの自動実行は拒否します。SHA-256は転送内容の整合性確認であり、署名や配布元の独立した認証の代わりではありません。

開発版 `1.1.0-preview.1` は通常リリースからの自動更新対象外です。更新はbranchから再ビルドします。

このブランチの検証でmainへの配置、Release公開、自動mergeは行いません。リポジトリ直下の既存 `DIndex.zip` と `DIndexUpdater.zip` は旧成果物なので、変更確認にはソースからビルドしたものを使ってください。

## ローカル確認用インストーラ

.NET 10 を同梱するユーザー単位のMSIを生成します。インストール先は通常profileと別の `%LOCALAPPDATA%\Programs\DIndex-Preview` です。新規導入、同版の修復、上位版への更新、データを残すアンインストールに対応する構成です。旧portable版のデータは自動移動しません。

- [MSIの作成・試用・更新と制約](docs/INSTALLER.md)
- [既存profileからの任意の非破壊コピー](docs/PROFILE_IMPORT.md)

インストーラは署名していません。Windowsのセキュリティ警告を回避する設定変更は案内しません。一般公開・自動更新配布はこの検証には含めません。ソースからの生成には既設.NET10 SDK、PowerShell7、WiX3.14.1が必要です。ビルドscriptは不足toolを自動インストールしません。実行するアプリには.NETの別途導入は不要です。
