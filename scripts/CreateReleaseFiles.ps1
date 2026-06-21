param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$ReleaseDir,
    [Parameter(Mandatory = $true)][string]$ExeName
)

$ErrorActionPreference = 'Stop'

$appExe = Join-Path $PublishDir $ExeName
if (-not (Test-Path -LiteralPath $appExe)) {
    throw "Application exe was not found: $appExe"
}

if (-not (Test-Path -LiteralPath $ReleaseDir)) {
    New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null
}

$releaseExe = Join-Path $ReleaseDir $ExeName
$releaseZip = Join-Path $ReleaseDir 'DIndex.zip'

Copy-Item -LiteralPath $appExe -Destination $releaseExe -Force

if (Test-Path -LiteralPath $releaseZip) {
    Remove-Item -LiteralPath $releaseZip -Force
}

Compress-Archive -LiteralPath $appExe -DestinationPath $releaseZip -Force

Write-Host "[OK] release exe: $releaseExe"
Write-Host "[OK] release zip: $releaseZip"
