param(
    [Parameter(Mandatory=$true)][string]$ReleaseDir,
    [Parameter(Mandatory=$true)][string]$Version,
    [Parameter(Mandatory=$true)][string]$DownloadUrl,
    [Parameter(Mandatory=$true)][string]$UpdaterUrl,
    [Parameter(Mandatory=$true)][string]$ReleaseNotesUrl
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null

$versionObj = [ordered]@{
    version = $Version
    downloadUrl = $DownloadUrl
    updaterUrl = $UpdaterUrl
    releaseNotes = $ReleaseNotesUrl
}
$versionObj | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $ReleaseDir 'version.json') -Encoding UTF8

$notesObj = [ordered]@{
    version = $Version
    notes = @(
        ('DIndex v' + $Version),
        'タスクトレイ常駐起動',
        'リアルタイム索引と高速メモリ検索',
        'UI改善: フォルダ追加、右クリック操作、件数/速度表示',
        'GitHub mainブランチを使った安全な自動アップデート'
    )
}
$notesObj | ConvertTo-Json -Depth 5 | Set-Content -Path (Join-Path $ReleaseDir 'release-notes.json') -Encoding UTF8
