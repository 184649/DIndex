#requires -Version 7.2
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot '../scripts/Build-StableRelease.ps1'
$tokens = $null; $errors = $null
[void][Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$root = Join-Path ([IO.Path]::GetTempPath()) ('dindex-release-input-tests-' + [guid]::NewGuid().ToString('N'))
$commit = '1111111111111111111111111111111111111111'
$passed = 0
function Assert-Reject([scriptblock]$Run, [string]$Pattern) {
    $message = $null
    try { & $Run | Out-Null } catch { $message = $_.Exception.Message }
    if (-not $message -or $message -notmatch $Pattern) { throw "Expected rejection '$Pattern'; received '$message'" }
}
foreach ($version in @('1.1', '1.1.2-preview.1', '1.1.2.0', '01.1.2', '65535.1.2', "1.1.2`n")) {
    Assert-Reject { & $script -Version $version -OutputDirectory $root -SourceCommit $commit } 'canonical stable'
    $passed++
}
foreach ($url in @('https://github.com/184649/DIndex/releases/latest/download', 'https://github.com/184649/DIndex/releases/download/v1.1.1', 'https://github.com/184649/DIndex/releases/download/v1.1.2?x=1')) {
    Assert-Reject { & $script -OutputDirectory $root -SourceCommit $commit -ReleaseBaseUrl $url } 'must be exactly'
    $passed++
}
Assert-Reject { & $script -OutputDirectory $root -SourceCommit 'not-a-commit' } '40-hex'; $passed++
Assert-Reject { & $script -OutputDirectory $root -SourceCommit ($commit + "`n") } '40-hex'; $passed++
Assert-Reject { & $script -OutputDirectory $root -SourceCommit $commit -RuntimeFrameworkVersion '8.0.32' } 'stable .NET 10'; $passed++
Assert-Reject { & $script -OutputDirectory $root -SourceCommit $commit -RuntimeFrameworkVersion "10.0.13`n" } 'stable .NET 10'; $passed++
[void][IO.Directory]::CreateDirectory($root)
try {
    $sentinel = Join-Path $root 'keep.txt'
    Set-Content -LiteralPath $sentinel -Value 'preserve'
    Assert-Reject { & $script -OutputDirectory $root -SourceCommit $commit } 'already exists'
    if ((Get-Content -LiteralPath $sentinel -Raw).Trim() -cne 'preserve') { throw 'Existing output was altered.' }
    $passed++
} finally { [IO.Directory]::Delete($root, $true) }
Write-Host "PASS $passed stable packaging input/preflight tests; no build or profile access."
