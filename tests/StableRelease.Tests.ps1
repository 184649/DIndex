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
$exporter = Join-Path $PSScriptRoot '../scripts/Export-ReleaseSource.ps1'
[void][Management.Automation.Language.Parser]::ParseFile($exporter, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
. $exporter -SourceCommit $commit -RepositoryDirectory $root -OutputArchive (Join-Path $root 'unused.zip')
$blob = '2222222222222222222222222222222222222222'
$records = @('src/DIndex/MainWindow.xaml.cs', 'src/DIndex/bin/old.exe', 'src/DIndex/obj/old.AssemblyInfo.cs', 'src/DIndexUpdater/OBJ/stale.cs', 'DIndex.zip', 'DIndexUpdater.zip', 'docs/my [literal] file.md') | ForEach-Object { "100644 blob $blob`t$_" + [char]0 }
$selection = Select-ReleaseSourceEntries ($records -join '')
if ($selection.Included.Count -ne 2 -or $selection.Excluded.Count -ne 5 -or -not $selection.Blobs.ContainsKey('docs/my [literal] file.md')) { throw 'Generated-only source filter did not preserve the exact expected paths.' }
$exportPassed = 1
foreach ($path in @('../escape.cs', 'src/../escape.cs', 'C:/escape.cs', 'src\escape.cs', 'src/CON.cs', 'src/trailing. ', "src/control`nname.cs")) {
    Assert-Reject { Select-ReleaseSourceEntries ("100644 blob $blob`t$path" + [char]0) } 'Unsafe|malformed'
    $exportPassed++
}
Assert-Reject { Select-ReleaseSourceEntries ("120000 blob $blob`tlink.cs" + [char]0) } 'non-regular'; $exportPassed++
Assert-Reject { Select-ReleaseSourceEntries ("160000 commit $blob`tmodule" + [char]0) } 'non-regular'; $exportPassed++
Assert-Reject { Select-ReleaseSourceEntries (("100644 blob $blob`tApp.cs" + [char]0) + ("100644 blob $blob`tapp.cs" + [char]0)) } 'case-ambiguous'; $exportPassed++
Assert-Reject { Assert-ReleaseArchiveArgumentLimit @(('x' * 16000)) } 'argument-length'; $exportPassed++
Write-Host "PASS $exportPassed pure source-export path/filter tests; no Git mutation or profile access."
# A bounded disposable repository checks the real native-process/NUL output and
# literal pathspec/archive behavior before any production application is built.
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('dindex-source-export-tests-' + [guid]::NewGuid().ToString('N'))
$fixtureRepo = Join-Path $fixture 'repo'
$fixtureOutput = Join-Path $fixture 'outputs'
foreach ($path in @($fixtureRepo, $fixtureOutput, (Join-Path $fixtureRepo 'src/bin'), (Join-Path $fixtureRepo 'src/obj'))) { [void][IO.Directory]::CreateDirectory($path) }
function Invoke-FixtureGit([string[]]$Arguments) {
    $result = @(& git -C $fixtureRepo @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Synthetic Git fixture failed: $($Arguments -join ' ')" }
    return ($result -join "`n").Trim()
}
try {
    $null = Invoke-FixtureGit @('init', '--quiet')
    # Reproduce Windows runner conversion without changing user/global settings.
    $null = Invoke-FixtureGit @('config', '--local', 'core.autocrlf', 'true')
    foreach ($name in @('source.txt', 'my [literal] file.cs', 'DIndex.zip', 'DIndexUpdater.zip', 'src/bin/stale.dll', 'src/obj/stale.cs')) {
        [IO.File]::WriteAllText((Join-Path $fixtureRepo $name), "fixture bytes: $name`n", [Text.UTF8Encoding]::new($false))
    }
    $null = Invoke-FixtureGit @('-c','core.autocrlf=false','add','--all')
    $null = Invoke-FixtureGit @('-c','user.name=Release Test','-c','user.email=release-test@example.invalid','commit','--quiet','-m','Synthetic export fixture')
    $sha = Invoke-FixtureGit @('rev-parse','HEAD')
    $archive = Join-Path $fixtureOutput 'source.zip'
    $manifest = & $exporter -SourceCommit $sha -RepositoryDirectory $fixtureRepo -OutputArchive $archive -ManifestPath (Join-Path $fixtureOutput 'manifest.json') -ArchivePrefix 'DIndex-source/'
    if ($manifest.includedCount -ne 2 -or $manifest.excludedCount -ne 4 -or $manifest.sourceCommit -cne $sha -or
        $manifest.archiveSha256 -ine (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash) { throw 'Real Git export fixture inventory/hash mismatch.' }
    if (-not [IO.File]::Exists((Join-Path $fixtureRepo 'src/bin/stale.dll'))) { throw 'Original generated files were removed.' }
    Write-Host 'PASS real source export preserves literals, excludes only generated files, and retains originals.'
    Assert-Reject { & $exporter -SourceCommit $sha -RepositoryDirectory $fixtureRepo -OutputArchive $archive } 'already exists'
    [IO.File]::WriteAllText((Join-Path $fixtureRepo 'source.txt'), '$Format:%H$', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $fixtureRepo '.gitattributes'), "source.txt export-subst`n", [Text.UTF8Encoding]::new($false))
    $null = Invoke-FixtureGit @('-c','core.autocrlf=false','add','--all')
    $null = Invoke-FixtureGit @('-c','user.name=Release Test','-c','user.email=release-test@example.invalid','commit','--quiet','-m','Synthetic export substitution')
    $sha = Invoke-FixtureGit @('rev-parse','HEAD')
    Assert-Reject { & $exporter -SourceCommit $sha -RepositoryDirectory $fixtureRepo -OutputArchive (Join-Path $fixtureOutput 'subst.zip') } 'differ from committed blob'
    [IO.File]::WriteAllText((Join-Path $fixtureRepo '.gitattributes'), "source.txt export-ignore`n", [Text.UTF8Encoding]::new($false))
    $null = Invoke-FixtureGit @('-c','core.autocrlf=false','add','--all')
    $null = Invoke-FixtureGit @('-c','user.name=Release Test','-c','user.email=release-test@example.invalid','commit','--quiet','-m','Synthetic export omission')
    $sha = Invoke-FixtureGit @('rev-parse','HEAD')
    Assert-Reject { & $exporter -SourceCommit $sha -RepositoryDirectory $fixtureRepo -OutputArchive (Join-Path $fixtureOutput 'ignored.zip') } 'omitted or added'
    Write-Host 'PASS source export rejects existing output, export-subst byte changes and export-ignore omissions.'
} finally {
    # Git object files may be read-only on Windows; this is our disposable fixture.
    Get-ChildItem -LiteralPath $fixture -File -Recurse -Force | ForEach-Object { $_.Attributes = [IO.FileAttributes]::Normal }
    [IO.Directory]::Delete($fixture, $true)
}
