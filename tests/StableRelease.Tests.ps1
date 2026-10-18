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

# Publisher GitHub blob UTF-8/BOM regression. Extract only the pure decoder
# function from its AST; never invoke/dot-source the publisher or any GitHub call.
$publisherPath = Join-Path $PSScriptRoot '../scripts/Publish-StableRelease.ps1'
$publisherAst = [Management.Automation.Language.Parser]::ParseFile($publisherPath, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$decoderDefinitions = @($publisherAst.FindAll({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'ConvertFrom-ReleaseUtf8Bytes'
}, $false))
if ($decoderDefinitions.Count -ne 1) { throw 'Expected one pure publisher UTF-8 decoder.' }
. ([scriptblock]::Create($decoderDefinitions[0].Extent.Text))
# Exact public main/version.json bytes at commit 3a90f4cb5a0dd3361fc55865feedb804f70e14f3.
# Preserve the original BOM and bytes; this fixture is read-only and makes no network request.
$legacyFeedBytes = [Convert]::FromBase64String('77u/ewogICAgInZlcnNpb24iOiAgIjEuMC4xIiwKICAgICJkb3dubG9hZFVybCI6ICAiaHR0cHM6Ly9yYXcuZ2l0aHVidXNlcmNvbnRlbnQuY29tL0NoYWlybWFuLWJpdHMvREluZGV4L21haW4vREluZGV4LnppcCIsCiAgICAidXBkYXRlclVybCI6ICAiaHR0cHM6Ly9yYXcuZ2l0aHVidXNlcmNvbnRlbnQuY29tL0NoYWlybWFuLWJpdHMvREluZGV4L21haW4vREluZGV4VXBkYXRlci56aXAiLAogICAgInJlbGVhc2VOb3RlcyI6ICAiaHR0cHM6Ly9yYXcuZ2l0aHVidXNlcmNvbnRlbnQuY29tL0NoYWlybWFuLWJpdHMvREluZGV4L21haW4vcmVsZWFzZS1ub3Rlcy5qc29uIgp9Cg==')
$legacyFeedHash = 'E9D2A7C566FB68EA785D6C12D8718FBA7F6A642305497BC38339A311841E03B4'
if ($legacyFeedBytes.Length -ne 328 -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($legacyFeedBytes)) -cne $legacyFeedHash) { throw 'Immutable legacy feed fixture bytes differ.' }
$legacyFeedText = ConvertFrom-ReleaseUtf8Bytes $legacyFeedBytes
$legacyFeed = $legacyFeedText | ConvertFrom-Json
if ($legacyFeed.version -cne '1.0.1' -or
    $legacyFeed.downloadUrl -cne 'https://raw.githubusercontent.com/Chairman-bits/DIndex/main/DIndex.zip' -or
    $legacyFeed.updaterUrl -cne 'https://raw.githubusercontent.com/Chairman-bits/DIndex/main/DIndexUpdater.zip' -or
    $legacyFeed.releaseNotes -cne 'https://raw.githubusercontent.com/Chairman-bits/DIndex/main/release-notes.json') { throw 'Actual BOM-prefixed legacy feed was not decoded faithfully.' }
if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($legacyFeedBytes)) -cne $legacyFeedHash) { throw 'Decoder changed original legacy feed bytes.' }
$withoutBom = [byte[]]$legacyFeedBytes[3..($legacyFeedBytes.Length - 1)]
if ((ConvertFrom-ReleaseUtf8Bytes $withoutBom) -cne $legacyFeedText) { throw 'BOM-free and BOM-prefixed UTF-8 differ after decoding.' }
$metadataJson = '{"version":"1.1.2","sourceCommit":"1111111111111111111111111111111111111111"}'
$metadataBytes = [Text.Encoding]::UTF8.GetBytes($metadataJson)
$prefixedMetadata = [byte[]](@(0xef,0xbb,0xbf) + $metadataBytes)
$metadata = (ConvertFrom-ReleaseUtf8Bytes $prefixedMetadata) | ConvertFrom-Json
if ($metadata.version -cne '1.1.2' -or $metadata.sourceCommit -cne $commit) { throw 'BOM-prefixed distribution metadata did not parse.' }
$embeddedJson = '{"value":"snow: ' + [char]0x96ea + '; embedded BOM: ' + [char]0xfeff + '"}'
if ((ConvertFrom-ReleaseUtf8Bytes ([Text.Encoding]::UTF8.GetBytes($embeddedJson))) -cne $embeddedJson) { throw 'Decoder altered valid non-ASCII or embedded U+FEFF text.' }
$metadataRejects = 0
function Assert-MetadataReject([scriptblock]$Run) {
    $rejected = $false
    try { & $Run | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Malformed release metadata was unexpectedly accepted.' }
}
foreach ($invalid in @([byte[]]@(), [byte[]]@(0xef,0xbb,0xbf), [byte[]]@(0xff,0xfe,0x7b,0x00), [byte[]]@(0xc3,0x28))) {
    Assert-MetadataReject { ConvertFrom-ReleaseUtf8Bytes $invalid }
    $metadataRejects++
}
Assert-MetadataReject { ConvertFrom-ReleaseUtf8Bytes ([byte[]]::new(1048577)) }; $metadataRejects++
Assert-MetadataReject { (ConvertFrom-ReleaseUtf8Bytes ([byte[]](@(0xef,0xbb,0xbf) + $prefixedMetadata))) | ConvertFrom-Json }; $metadataRejects++
Assert-MetadataReject { (ConvertFrom-ReleaseUtf8Bytes ([byte[]](@(0x20) + $prefixedMetadata))) | ConvertFrom-Json }; $metadataRejects++
Write-Host "PASS publisher metadata: exact immutable legacy BOM fixture, unchanged input bytes, BOM-free UTF-8, distribution BOM, embedded Unicode and $metadataRejects malformed/oversized rejection cases; no publication invoked."
