# DIndex を自分の PC で動作確認する

## 最短の手順

1. 受け取ったソース ZIP をすべて展開します。ZIP 内から直接実行せず、`Build.cmd` と `packaging`・`src` などが同じフォルダーにある状態にします。Git や clone 操作は不要です。
2. `Build.cmd` をダブルクリックします。管理者としての実行は不要です。古い `Build.bat` / `build.bat` とは別の入口です。
3. 完了した画面の「出力先」を開きます。既定ではデスクトップの `DApps-Preview-Builds` 内に、アプリ名・日時・GUID の新しいフォルダーができます。以前のビルドは上書きしません。
4. 出力先の `はじめに.txt` を開き、`Launch-Preview.cmd` をダブルクリックします。MSI をインストールせず、そのビルドのアプリを試せます。
5. アプリのトレイメニューから終了します。同じ `Launch-Preview.cmd` で再起動すると、同じテストデータを読み込みます。

ビルド中は数分かかることがあります。画面に出るログファイルで経過を確認できます。成功・失敗のどちらでもウィンドウを閉じる前に結果を読めます。

## PC に必要なもの

- Windows 10/11 x64
- [Microsoft 公式 .NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。Desktop Runtime だけではビルドできません
- [Microsoft 公式 PowerShell 7.2 以降](https://learn.microsoft.com/powershell/scripting/install/install-powershell-on-windows)。Windows 標準の PowerShell 5.1 とは異なります
- [WiX Toolset 3.14.1](https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm)。`candle.exe` と `light.exe` の FileVersion 文字列が **3.14.1.8722** で、`WixUIExtension.dll`・`WixUtilExtension.dll` が必要です。WiX のネイティブ数値欄は `3.14.8722.0` になるため、版の確認には文字列欄を使います
- NuGet の公式 .NET ランタイム依存ファイルと、ライセンス用 WiX ソース ZIP を取得できるインターネット接続

不足している場合は、公式案内へのリンクとログを残して中止します。ツールを自動インストールしたり、実行ポリシーを変更したり、管理者へ昇格したりしません。PC のポリシーでスクリプト実行が禁止されている場合は、管理者へ確認してください。警告やポリシーを回避する操作は案内しません。

## 試せること

1. トレイから画面を開き、`notes`・`報告書`・空検索・名前/パス切替を試します。初回は同梱の `sample-files` だけを検索する設定です。
2. `sample-files` にテキストファイルを追加し、名前変更・削除が検索へ反映されることを確認します。
3. 入力中の再索引、連続入力、同じ起動ファイルの二重起動で状態が崩れないことを確認します。
4. トレイから終了して再起動し、設定と索引が読み込まれることを確認します。詳しい項目は [WINDOWS_ACCEPTANCE.md](WINDOWS_ACCEPTANCE.md) を参照してください。

- 保存先は出力先内の `preview-profile` です。通常版や別のプレビューの保存先とは別です。別ビルドを作ると新しい確認用データになります。
- 出力先内の `sample-files` はテスト用です。最初は大切な実データを登録・移動せず、このフォルダーで確認してください。
- `DINDEX_DATA_DIR` を起動したプロセスだけに設定します。Windows 全体の環境変数は変更しません。生成後に出力フォルダーを移動した場合は、設定の検索先を移動後の `sample-files` に変更してください
- 直接 `payload\DIndex.exe` を開かず、確認用データを指定する `Launch-Preview.cmd` から起動してください。
- 初期画面が表示されない場合は、隠れているタスクトレイアイコンも確認します。ウィンドウの × では常駐が続く場合があります。
- MSI に梱包するものと同じ payload なので、直接起動でも「MSI管理」と表示され、EXE 単体の更新を止めます。`installed-by-msi.json` は残してください。この表示は、この PC に MSI をインストール済みであることの証明ではありません。
- 実行用の .NET は同梱されています。ビルド済み payload の実行に、別途 Desktop Runtime は必要ありません。

## インストーラ画面も確認したい場合

`はじめに.txt` の「MSI」欄に実ファイルのフルパスが書かれます。出力先の `build-<GUID>` の中にも `.msi` があります。自分で開くまで実行されません。

未署名の Preview 用 MSI です。画面だけを確認する場合は、インストールを確定せずキャンセルしてください。導入する場合の保存先・修復・更新・アンインストールの確認項目は [INSTALLER.md](INSTALLER.md) を参照してください。MSI 起動後のアプリは既定のプレビュー用データを使い、`Launch-Preview.cmd` の `preview-profile` とは別になります。

`Launch-Preview.cmd` の動作確認だけでは、MSI の導入・修復・更新・削除・ショートカットを検証したことにはなりません。ビルドの成功も GUI 動作確認の合格を意味しません。

## 出力先の指定・CI

通常のコマンドプロンプト:

```bat
Build.cmd -OutputDirectory "C:\Users\yourname\Documents\DApps Preview"
```

同じ入口を CI で実行する場合は、最初に `--ci` を指定します。ウィンドウのキー待ちを省き、成功は 0、失敗は 0 以外の終了コードをそのまま返します。

```bat
Build.cmd --ci -OutputDirectory "C:\ci\local-builds"
```

既設 WiX の場所を明示する場合:

```bat
Build.cmd --ci -OutputDirectory "C:\ci\local-builds" -WixBin "C:\Program Files (x86)\WiX Toolset v3.14\bin"
```

ライセンス用 WiX ソース ZIP が手元にある場合は `-WixSourceArchive "C:\path\wix3-source.zip"` も指定できます。これはツールのインストーラではありません。正しいソースの検証は `Build-Installer.ps1` が行います。

`-OutputDirectory` は親フォルダーです。指定先が既に存在しても、その下に毎回新しい子フォルダーを作ります。相対パスは呼び出し元の現在のフォルダーを基準にします。ソース・出力先の空白と日本語を含むパスに対応しています。

PowerShell 7 から直接呼ぶ場合も同じ処理です:

```powershell
pwsh -NoProfile -File .\packaging\Build-Local.ps1 -NonInteractive -OutputDirectory 'C:\ci\local-builds'
```

`Build.cmd --ci` は `Build-Local.ps1 -NonInteractive` に渡します。ビルド側の対象は `net10.0-windows`、MSI バージョンは `1.1.0`、アプリは `1.1.0-preview.1` です。CI の受入試験では直接 `Build-Installer.ps1` を呼ぶだけでなく、この `Build.cmd --ci` を使用してください。

## 失敗したとき

- 最初に表示される起動ログ: `%LOCALAPPDATA%\DApps-Preview-Builds\logs\DIndex-bootstrap-*.log`。PowerShell 不足やスクリプト読み込み前のエラーも残します
- 出力先内の `build.log`: 事前確認とビルドの詳細。失敗時は `BUILD-FAILED.txt` も作成します。WiX 検出では、確認したフルパス・不足ファイル・FileVersion / ProductVersion 文字列・数値版を記録します
- 出力先内の `local-build.json`: 成功時の MSI・payload・確認用データ・ログのパスと、ビルド情報
- `build-<GUID>\build-manifest.json`: SDK / WiX / ランタイム版と MSI の SHA-256

ログに出る不足ツールや通信エラーを確認し、解消後に `Build.cmd` をもう一度実行してください。失敗したフォルダーを再利用・削除せず、新しい出力先を作ります。セキュリティ警告が出たときは、実行を止めて出所を確認してください。
