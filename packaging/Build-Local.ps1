[CmdletBinding()]
param(
    # A fresh app/timestamp/GUID child is always created beneath this directory.
    [string]$OutputDirectory,
    [switch]$NonInteractive,
    [string]$WixBin,
    [string]$WixSourceArchive
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = Split-Path $PSScriptRoot -Parent
$app = 'DIndex'
$exitCode = 1
$transcriptStarted = $false
$buildDirectory = $null

. (Join-Path $PSScriptRoot 'Find-WixToolset.ps1')

try {
    if (-not $OutputDirectory) {
        $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
        if (-not $desktop) { $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments) }
        if (-not $desktop) { $desktop = Join-Path $repo 'out' }
        $OutputDirectory = Join-Path $desktop 'DApps-Preview-Builds'
    }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    $buildDirectory = Join-Path $OutputDirectory ($app + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $buildDirectory) { throw 'A fresh build directory could not be allocated. Retry the build.' }
    [IO.Directory]::CreateDirectory($buildDirectory) | Out-Null
    $logPath = Join-Path $buildDirectory 'build.log'
    Start-Transcript -LiteralPath $logPath -NoClobber | Out-Null
    $transcriptStarted = $true
    Write-Host "出力先: $buildDirectory"
    Write-Host '事前確認を行います。不足ツールの自動インストールは行いません。'

    $problems = [Collections.Generic.List[string]]::new()
    if ($PSVersionTable.PSVersion -lt [version]'7.2') {
        $problems.Add('PowerShell 7.2 以降が必要です。https://learn.microsoft.com/powershell/scripting/install/install-powershell-on-windows')
    }
    if (-not $IsWindows) { $problems.Add('このビルドは Windows 10/11 x64 で実行してください。') }
    if (-not [Environment]::Is64BitOperatingSystem) { $problems.Add('64-bit Windows が必要です。') }
    $dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        $problems.Add('.NET 10 SDK が見つかりません。Runtime だけではビルドできません。https://dotnet.microsoft.com/download/dotnet/10.0')
    } else {
        $sdkLines = @(& $dotnet.Source --list-sdks)
        if ($LASTEXITCODE -ne 0 -or -not ($sdkLines | Where-Object { $_ -match '^10\.0\.\d+\s' })) {
            $problems.Add('正式版の .NET 10 SDK が必要です。https://dotnet.microsoft.com/download/dotnet/10.0')
        } else { Write-Host ('検出した .NET 10 SDK: ' + (($sdkLines | Where-Object { $_ -match '^10\.0\.\d+\s' }) -join '; ')) }
    }
    $resolvedWix = Find-PreinstalledWix -WixBin $WixBin
    if (-not $resolvedWix) {
        $problems.Add('既設の WiX 3.14.1.8722 (candle.exe / light.exe / UI・Util 拡張) が必要です。https://github.com/wixtoolset/wix3/releases/tag/wix3141rtm')
    } else { Write-Host "WiX 3.14.1.8722: $resolvedWix" }
    if ($WixSourceArchive -and -not (Test-Path -LiteralPath $WixSourceArchive -PathType Leaf)) {
        $problems.Add("指定した WiX ソース ZIP がありません: $WixSourceArchive")
    }
    if ($problems.Count -gt 0) { throw ($problems -join [Environment]::NewLine) }

    Write-Host 'MSI と .NET 同梱のプレビューを作成しています。MSI の実行やアプリの起動は行いません。'
    $buildParameters = @{
        TargetFramework = 'net10.0-windows'
        PackageVersion = '1.1.0'
        OutputDirectory = $buildDirectory
        WixBin = $resolvedWix
    }
    if ($WixSourceArchive) { $buildParameters.WixSourceArchive = (Resolve-Path -LiteralPath $WixSourceArchive).Path }
    $result = & (Join-Path $PSScriptRoot 'Build-Installer.ps1') @buildParameters
    if (-not $result -or -not (Test-Path -LiteralPath $result.msiPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $result.payloadDirectory "$app.exe") -PathType Leaf)) {
        throw 'MSI または実行ファイルを確認できませんでした。build.log を確認してください。'
    }
    $relativePayload = [IO.Path]::GetRelativePath($buildDirectory, $result.payloadDirectory)
    if ($relativePayload.StartsWith('..') -or [IO.Path]::IsPathRooted($relativePayload)) {
        throw 'Payload must remain inside the newly created output directory.'
    }
    $previewProfile = Join-Path $buildDirectory 'preview-profile'
    [IO.Directory]::CreateDirectory($previewProfile) | Out-Null
    $sampleFiles = Join-Path $buildDirectory 'sample-files'
    [IO.Directory]::CreateDirectory($sampleFiles) | Out-Null
    '動作確認用です。このファイルは自由に名前を変更できます。' | Set-Content -LiteralPath (Join-Path $sampleFiles 'notes-報告書.txt') -Encoding utf8
    # Start with a tiny synthetic search root, never all user drives.
    @{
        SearchRoots = @($sampleFiles)
        SearchPath = $true
        IncludeDirectories = $true
        ResultLimit = 500
        ExcludeFolderNames = @('.git', 'node_modules', 'bin', 'obj')
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $previewProfile 'settings.json') -Encoding utf8
    # Keep all generated CMD text ASCII and paths relative to %~dp0. This avoids
    # encoding-dependent Japanese paths, and delayed expansion remains disabled.
    $launcher = @'
@echo off
setlocal EnableExtensions DisableDelayedExpansion
if not exist "%~dp0__PAYLOAD__\DIndex.exe" goto missing
set "DINDEX_DATA_DIR=%~dp0preview-profile"
echo DIndex Preview is running. Use its tray menu to exit.
echo Test data: "%~dp0preview-profile"
start "" /wait "%~dp0__PAYLOAD__\DIndex.exe"
set "APP_EXIT=%ERRORLEVEL%"
if "%APP_EXIT%"=="0" exit /b 0
echo The preview exited with code %APP_EXIT%. Check preview-profile and build.log.
pause
exit /b %APP_EXIT%
:missing
echo The preview payload is missing. Rebuild with Build.cmd.
pause
exit /b 2
'@
    $launcher = $launcher.Replace('__PAYLOAD__', $relativePayload)
    $launcherPath = Join-Path $buildDirectory 'Launch-Preview.cmd'
    [IO.File]::WriteAllText($launcherPath, ($launcher -replace '\r?\n', "`r`n") + "`r`n", [Text.Encoding]::ASCII)
    $instructions = @"
$app プレビュー: ローカル動作確認

1. Launch-Preview.cmd をダブルクリックすると、インストールせず起動できます。
   通常版の設定とは別の preview-profile を使用します。
   初期画面が出ない場合は、タスクトレイの隠れているアイコンも確認してください。
2. sample-files にあるテストファイルで検索・登録・再起動後の保存を確認してください。
3. 終了はアプリのトレイメニューから行います。ウィンドウの×だけでは常駐する場合があります。

MSI (手動確認用):
$($result.msiPath)

プレビュー起動:
$launcherPath

テストデータ:
$previewProfile

ビルドログ:
$logPath

SHA-256:
$($result.sha256)

ビルドは MSI の実行、アプリ起動、ショートカット・自動起動登録を行いません。
インストーラ画面を確認する場合だけ、上の MSI を自分で開いてください。
MSI は未署名の Preview です。インストールせず中止すればセットアップ確認で止められます。
Launch-Preview.cmd の確認用データは、MSI から起動した場合の既定データとは別です。
MSI 用 payload をそのまま起動するため「MSI管理」と表示されますが、この起動方法ではインストールされません。
EXE 単独更新を止めるための installed-by-msi.json は削除しないでください。
MSI の導入・修復・更新・削除の動作は、この起動確認だけでは検証できません。

詳細なチェック一覧: ソースの docs\LOCAL_CHECK.md
"@
    $instructionsPath = Join-Path $buildDirectory 'はじめに.txt'
    $instructions | Set-Content -LiteralPath $instructionsPath -Encoding utf8BOM
    [ordered]@{
        app = $app
        mode = 'build-only'
        nonInteractive = [bool]$NonInteractive
        outputDirectory = $buildDirectory
        launcherPath = $launcherPath
        previewProfile = $previewProfile
        logPath = $logPath
        instructionsPath = $instructionsPath
        build = $result
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $buildDirectory 'local-build.json') -Encoding utf8
    Write-Host ''
    Write-Host 'ビルド完了。インストールやアプリ起動はまだ行っていません。'
    Write-Host "手動で試す: $launcherPath"
    Write-Host "MSI: $($result.msiPath)"
    Write-Host "手順: $instructionsPath"
    Write-Host "ログ: $logPath"
    $exitCode = 0
} catch {
    $message = $_.Exception.Message
    Write-Host ''
    Write-Host ('ビルドを中止しました: ' + $message) -ForegroundColor Red
    Write-Host '不足ツール・通信・保存先を確認してから Build.cmd をもう一度開いてください。'
    Write-Host '実行ポリシーや Windows のセキュリティ設定は変更していません。'
    if ($buildDirectory -and (Test-Path -LiteralPath $buildDirectory -PathType Container)) {
        try {
            @('ビルドは完了していません。', $message, '詳細は build.log と Build.cmd が表示したログを確認してください。') |
                Set-Content -LiteralPath (Join-Path $buildDirectory 'BUILD-FAILED.txt') -Encoding utf8BOM
        } catch { Write-Host ('失敗記録を書き込めませんでした: ' + $_.Exception.Message) }
    }
} finally {
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
}
exit $exitCode
