param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$DownloadUrl,
    [Parameter(Mandatory = $true)][string]$ExeUrl,
    [Parameter(Mandatory = $true)][string]$ReleaseNotesUrl,
    [Parameter(Mandatory = $true)][string]$ReleaseDir
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ReleaseDir)) {
    New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null
}

$versionObj = [ordered]@{
    version      = $Version
    downloadUrl  = $DownloadUrl
    exeUrl       = $ExeUrl
    releaseNotes = $ReleaseNotesUrl
    notes        = "DIndex v$Version"
}

$notesObj = [ordered]@{
    version = $Version
    notes   = @("DIndex v$Version")
}

$versionJson = $versionObj | ConvertTo-Json -Depth 10
$notesJson = $notesObj | ConvertTo-Json -Depth 10

Set-Content -LiteralPath (Join-Path $ReleaseDir 'version.json') -Value $versionJson -Encoding UTF8
Set-Content -LiteralPath (Join-Path $ReleaseDir 'release-notes.json') -Value $notesJson -Encoding UTF8

Write-Host "[OK] version.json generated."
Write-Host "[OK] release-notes.json generated."
