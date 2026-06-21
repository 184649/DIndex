# DIndex

Windows向けの高速ファイル検索アプリです。

## 主な機能

- 初回起動はタスクトレイのみ
- タスクトレイダブルクリック、またはメニューの「開く」で画面表示
- 指定フォルダ配下をメモリ索引化して高速検索
- FileSystemWatcher によるリアルタイム索引更新
- 右クリックで「開く」「フォルダを開く」「パスをコピー」
- GitHub main ブランチの `version.json` を確認して自動アップデート
- 更新時は `DIndexUpdater.exe` が本体終了後に安全に置換

## ビルド

通常ビルド:

```bat
build.bat
```

配布用ビルド:

```bat
Build.bat
```

## GitHub main に配置するファイル

`Build.bat` 実行後、`release` フォルダ内の以下を GitHub の main ブランチ直下に配置してください。

```text
DIndex.zip
DIndexUpdater.zip
version.json
release-notes.json
```

## 自動アップデートの流れ

1. DIndex が `https://raw.githubusercontent.com/Chairman-bits/DIndex/main/version.json` を確認
2. 新しいバージョンがあれば `DIndexUpdater.zip` を取得
3. `DIndexUpdater.exe` が起動
4. DIndex 本体を終了
5. `DIndex.zip` から新しい `DIndex.exe` を取り出して置換
6. DIndex を再起動
