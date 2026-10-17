#requires -Version 7.2
<# Dependency-free synthetic Windows tests. Never reads an actual user profile. #>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Profile import fixtures must run on Windows with PowerShell 7.2+.' }
$script:FixtureRoot = Join-Path ([IO.Path]::GetTempPath()) ('profile-import-tests-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($script:FixtureRoot)
$script:Passed = 0
$script:Failed = [Collections.Generic.List[string]]::new()
$helper = Join-Path $PSScriptRoot '../scripts/Copy-LegacyProfile.ps1'
$parseErrors = $null; $tokens = $null
[void][Management.Automation.Language.Parser]::ParseFile($helper, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
. $helper -Source (Join-Path $script:FixtureRoot 'unused-source') -Destination (Join-Path $script:FixtureRoot 'unused-destination') -BackupDirectory (Join-Path $script:FixtureRoot 'unused-backup')

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern = '.') {
    $caught = $null
    try { & $Action | Out-Null }
    catch { $caught = $_.Exception.Message }
    if (-not $caught -or $caught -notmatch $Pattern) { throw "Expected failure matching '$Pattern'; actual: $caught" }
}
function Write-FixtureJson([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 32), [Text.UTF8Encoding]::new($false))
}
function Get-FixtureHash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function New-Fixture {
    $root = Join-Path $script:FixtureRoot ([Guid]::NewGuid().ToString('N'))
    $source = Join-Path $root 'legacy'
    [void][IO.Directory]::CreateDirectory($source)
    [IO.File]::WriteAllText((Join-Path $source 'index-cache.tsv'), 'old cache must not become a new index')
    [IO.File]::WriteAllText((Join-Path $source 'error.log'), 'synthetic diagnostic evidence')
    [IO.File]::WriteAllText((Join-Path $source '.instance.lock'), '')
    [void][IO.Directory]::CreateDirectory((Join-Path $source 'empty-directory'))
    [void][IO.Directory]::CreateDirectory((Join-Path $source 'Backups'))
    [IO.File]::WriteAllText((Join-Path $source 'Backups/history.raw'), 'synthetic history retained byte for byte')
    $fixture = [pscustomobject]@{ Root = $root; Source = $source; Destination = (Join-Path $root 'preview'); Backup = (Join-Path $root 'archive') }
    Set-FixtureCanonicalData $fixture
    return $fixture
}
function Import-Fixture($Fixture) {
    return Invoke-LegacyProfileImport -Source $Fixture.Source -Destination $Fixture.Destination -BackupDirectory $Fixture.Backup
}
function Get-FixtureBackup($Fixture) {
    $runs = @(Get-ChildItem -LiteralPath $Fixture.Backup -Directory -Force)
    Assert-True ($runs.Count -eq 1) 'Expected exactly one retained backup run.'
    return $runs[0].FullName
}
function Invoke-Case([string]$Name, [scriptblock]$Action) {
    $fixture = New-Fixture
    try {
        & $Action $fixture
        $script:Passed++
        Write-Host "PASS $Name"
    }
    catch {
        $script:Failed.Add("${Name}: $($_.Exception.Message)")
        Write-Host "FAIL ${Name}: $($_.Exception.Message)"
    }
}
$script:CanonicalName = 'settings.json'
function Set-FixtureCanonicalData($Fixture) {
    Write-FixtureJson (Join-Path $Fixture.Source 'settings.json') ([ordered]@{
        SearchRoots = @('C:\Synthetic\Documents'); SearchPath = $true; IncludeDirectories = $true;
        ResultLimit = 500; ExcludeFolderNames = @('node_modules'); FuturePreference = @{ Keep = 'untouched' }
    })
}
function Assert-AppFixtureResult($Fixture, $Result) {
    $files = @(Get-ChildItem -LiteralPath $Fixture.Destination -Recurse -Force)
    Assert-True ($files.Count -eq 1 -and $files[0].Name -eq 'settings.json') 'DIndex must import only settings.json.'
    Assert-True ((Get-FixtureHash (Join-Path $Fixture.Source 'settings.json')) -eq (Get-FixtureHash (Join-Path $Fixture.Destination 'settings.json'))) 'Settings bytes or unknown properties changed.'
}
try {
    Invoke-Case 'complete backup matches and import preserves bytes' {
        param($f)
        $limits = @{ MaxFiles = 10000; MaxBytes = 2147483648L; MaxFileBytes = 536870912L }
        $before = Get-ProfileManifest -Root $f.Source @limits
        $result = Import-Fixture $f
        Assert-True $result.Published 'Import was not published.'
        Assert-ProfileManifestsMatch $before (Get-ProfileManifest -Root $f.Source @limits) -IncludeWriteTimes
        Assert-ProfileManifestsMatch $before (Get-ProfileManifest -Root (Join-Path $result.Backup 'snapshot') @limits)
        Assert-True ([IO.File]::Exists((Join-Path $result.Backup 'SNAPSHOT_VERIFIED.txt'))) 'Verified backup marker missing.'
        Assert-True ([IO.File]::Exists((Join-Path $result.Backup 'manifest.json'))) 'Hash inventory missing.'
        Assert-AppFixtureResult $f $result
    }
    Invoke-Case 'nonempty destination is never overwritten' {
        param($f)
        [void][IO.Directory]::CreateDirectory($f.Destination)
        $marker = Join-Path $f.Destination 'keep.txt'
        [IO.File]::WriteAllText($marker, 'existing-data')
        Assert-Throws { Import-Fixture $f } 'Destination must not exist'
        Assert-True ([IO.File]::ReadAllText($marker) -eq 'existing-data') 'Existing data was changed.'
        Assert-True (-not [IO.Directory]::Exists($f.Backup)) 'Rejection should happen before backup creation.'
    }
    Invoke-Case 'empty destination is also rejected for atomic publication' {
        param($f)
        [void][IO.Directory]::CreateDirectory($f.Destination)
        Assert-Throws { Import-Fixture $f } 'Destination must not exist'
    }
    Invoke-Case 'missing source and overlapping paths fail' {
        param($f)
        Assert-Throws { Invoke-LegacyProfileImport -Source (Join-Path $f.Root 'missing') -Destination $f.Destination -BackupDirectory $f.Backup } 'Source directory is missing'
        Assert-Throws { Invoke-LegacyProfileImport -Source $f.Source -Destination $f.Source -BackupDirectory $f.Backup } 'non-overlapping'
        Assert-Throws { Invoke-LegacyProfileImport -Source $f.Source -Destination (Join-Path $f.Source 'nested') -BackupDirectory $f.Backup } 'non-overlapping'
        Assert-Throws { Invoke-LegacyProfileImport -Source $f.Source -Destination $f.Root -BackupDirectory $f.Backup } 'non-overlapping'
        Assert-Throws { Invoke-LegacyProfileImport -Source $f.Source -Destination $f.Destination -BackupDirectory (Join-Path $f.Source 'backup') } 'non-overlapping'
    }
    Invoke-Case 'traversal, device and relative paths fail' {
        param($f)
        Assert-Throws { Invoke-LegacyProfileImport -Source ($f.Source + '\..\legacy') -Destination $f.Destination -BackupDirectory $f.Backup } 'Unsafe'
        Assert-Throws { Invoke-LegacyProfileImport -Source '.\legacy' -Destination $f.Destination -BackupDirectory $f.Backup } 'absolute local'
        Assert-Throws { Invoke-LegacyProfileImport -Source ('\\?\' + $f.Source) -Destination $f.Destination -BackupDirectory $f.Backup } 'absolute local'
    }
    Invoke-Case 'source and destination-parent junctions fail' {
        param($f)
        $outside = Join-Path $f.Root 'outside'
        [void][IO.Directory]::CreateDirectory($outside)
        [IO.File]::WriteAllText((Join-Path $outside 'keep.txt'), 'outside data')
        $link = Join-Path $f.Source 'linked'
        New-Item -ItemType Junction -Path $link -Target $outside | Out-Null
        Assert-Throws { Import-Fixture $f } 'Reparse'
        [IO.Directory]::Delete($link)
        $parentLink = Join-Path $f.Root 'linked-parent'
        New-Item -ItemType Junction -Path $parentLink -Target $outside | Out-Null
        Assert-Throws { Invoke-LegacyProfileImport -Source $f.Source -Destination (Join-Path $parentLink 'preview') -BackupDirectory $f.Backup } 'Reparse'
        Assert-True ([IO.File]::ReadAllText((Join-Path $outside 'keep.txt')) -eq 'outside data') 'Junction target changed.'
    }
    Invoke-Case 'alternate streams are rejected without discarding evidence' {
        param($f)
        $file = Join-Path $f.Source $script:CanonicalName
        Set-Content -LiteralPath $file -Stream 'fixture-extra' -Value 'must not be dropped'
        Assert-Throws { Import-Fixture $f } 'Alternate data streams'
        Assert-True (-not [IO.Directory]::Exists($f.Destination)) 'ADS profile was published.'
    }
    Invoke-Case 'read handles deny writes and are released after inspection' {
        param($f)
        $manifest = Get-ProfileManifest -Root $f.Source -MaxFiles 10000 -MaxBytes 2147483648 -MaxFileBytes 536870912 -HoldFiles
        $path = Join-Path $f.Source 'error.log'
        try { Assert-Throws { [IO.File]::WriteAllText($path, 'blocked mutation') } 'used by another process|being used|cannot access|sharing|access.*denied' }
        finally { foreach ($handle in $manifest.Handles) { $handle.Dispose() } }
        [IO.File]::WriteAllText($path, 'write allowed after handles close')
    }
    Invoke-Case 'source addition during backup is detected and backup retained' {
        param($f)
        $script:OriginalCopy = (Get-Command Copy-ProfileSnapshot).ScriptBlock
        $script:MutationSource = $f.Source
        function script:Copy-ProfileSnapshot([string]$SourceRoot, [string]$TargetRoot, $Manifest) {
            & $script:OriginalCopy $SourceRoot $TargetRoot $Manifest
            if ($SourceRoot -eq $script:MutationSource) { [IO.File]::WriteAllText((Join-Path $SourceRoot 'concurrent-new.txt'), 'external mutation') }
        }
        try { Assert-Throws { Import-Fixture $f } 'snapshot changed' }
        finally { Set-Item -Path Function:script:Copy-ProfileSnapshot -Value $script:OriginalCopy }
        Assert-True (-not [IO.Directory]::Exists($f.Destination)) 'Changed source was published.'
        $backup = Get-FixtureBackup $f
        Assert-True ([IO.File]::Exists((Join-Path $backup 'IMPORT_FAILED.txt'))) 'Failed backup was not retained.'
        Assert-True ([IO.File]::Exists((Join-Path $backup ('snapshot/' + $script:CanonicalName)))) 'Backup data disappeared.'
    }
    Invoke-Case 'corrupt canonical JSON fails after retaining verified raw backup' {
        param($f)
        $path = Join-Path $f.Source $script:CanonicalName
        [IO.File]::WriteAllText($path, '{ corrupt canonical data')
        $hash = Get-FixtureHash $path
        Assert-Throws { Import-Fixture $f } 'Import stopped'
        Assert-True (-not [IO.Directory]::Exists($f.Destination)) 'Corrupt data was published.'
        $backup = Get-FixtureBackup $f
        Assert-True ((Get-FixtureHash (Join-Path $backup ('snapshot/' + $script:CanonicalName))) -eq $hash) 'Corrupt source bytes were not retained.'
        Assert-True ([IO.File]::Exists((Join-Path $backup 'SNAPSHOT_VERIFIED.txt'))) 'Backup should be verified before schema parsing.'
    }
    Invoke-Case 'duplicate JSON properties fail' {
        param($f)
        [IO.File]::WriteAllText((Join-Path $f.Source $script:CanonicalName), '{"Items":[],"items":[]}')
        Assert-Throws { Import-Fixture $f } 'Duplicate or case-ambiguous'
    }
    Invoke-Case 'missing canonical file never recovers from backup' {
        param($f)
        $canonical = Join-Path $f.Source $script:CanonicalName
        [IO.File]::Move($canonical, $canonical + '.bak-only')
        Assert-Throws { Import-Fixture $f } 'Required canonical file is missing'
        Assert-True (-not [IO.Directory]::Exists($f.Destination)) 'Missing primary was silently recovered.'
    }
    Invoke-Case 'future schema never falls back or publishes' {
        param($f)
        $path = Join-Path $f.Source $script:CanonicalName
        $data = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable
        $data.SchemaVersion = 999
        Write-FixtureJson $path $data
        Assert-Throws { Import-Fixture $f } 'schema'
        Assert-True (-not [IO.Directory]::Exists($f.Destination)) 'Future schema was published.'
    }
    Invoke-Case 'staged corruption cannot publish and staging is retained' {
        param($f)
        $script:OriginalCopy = (Get-Command Copy-ProfileSnapshot).ScriptBlock
        function script:Copy-ProfileSnapshot([string]$SourceRoot, [string]$TargetRoot, $Manifest) {
            & $script:OriginalCopy $SourceRoot $TargetRoot $Manifest
            if ([IO.Path]::GetFileName($TargetRoot).StartsWith('.')) {
                [IO.File]::WriteAllText((Join-Path $TargetRoot $script:CanonicalName), 'staged corruption')
            }
        }
        try { Assert-Throws { Import-Fixture $f } 'snapshot changed' }
        finally { Set-Item -Path Function:script:Copy-ProfileSnapshot -Value $script:OriginalCopy }
        Assert-True (-not [IO.Directory]::Exists($f.Destination)) 'Incomplete stage was published.'
        Assert-True (@(Get-ChildItem -LiteralPath $f.Root -Force -Directory | Where-Object Name -Like '.*-import-*').Count -eq 1) 'Failed staging evidence was removed.'
        [void](Get-FixtureBackup $f)
    }
    Invoke-Case 'destination appearing before publish is left intact' {
        param($f)
        $script:OriginalCopy = (Get-Command Copy-ProfileSnapshot).ScriptBlock
        $script:RaceDestination = $f.Destination
        function script:Copy-ProfileSnapshot([string]$SourceRoot, [string]$TargetRoot, $Manifest) {
            & $script:OriginalCopy $SourceRoot $TargetRoot $Manifest
            if ([IO.Path]::GetFileName($TargetRoot).StartsWith('.')) {
                [void][IO.Directory]::CreateDirectory($script:RaceDestination)
                [IO.File]::WriteAllText((Join-Path $script:RaceDestination 'keep.txt'), 'concurrent existing profile')
            }
        }
        try { Assert-Throws { Import-Fixture $f } 'Destination appeared' }
        finally { Set-Item -Path Function:script:Copy-ProfileSnapshot -Value $script:OriginalCopy }
        Assert-True ([IO.File]::ReadAllText((Join-Path $f.Destination 'keep.txt')) -eq 'concurrent existing profile') 'Concurrent profile was overwritten.'
    }
    Invoke-Case 'configured file and byte limits fail closed' {
        param($f)
        Assert-Throws { Invoke-LegacyProfileImport -Source $f.Source -Destination $f.Destination -BackupDirectory $f.Backup -MaxFiles 1 } 'file-count|entry-count'
        Assert-Throws { Invoke-LegacyProfileImport -Source $f.Source -Destination $f.Destination -BackupDirectory $f.Backup -MaxBytes 1 } 'byte-size'
        Assert-True (-not [IO.Directory]::Exists($f.Destination)) 'Over-limit profile was published.'
    }
    Invoke-Case 'process detector includes tray aliases updaters and hosted apps' {
        param($f)
        foreach ($name in @('DIndex.exe','DHub.exe','ShortcutList.exe','DHub.Updater.exe','Updater.exe')) {
            Assert-True (Test-ProfileApplicationProcess ([pscustomobject]@{ Name = $name; CommandLine = ''; ProcessId = 1 })) "Missed process $name"
        }
        Assert-True (Test-ProfileApplicationProcess ([pscustomobject]@{ Name = 'dotnet.exe'; CommandLine = 'dotnet C:\Apps\DHub.dll'; ProcessId = 1 })) 'Missed hosted DHub.'
        Assert-True (Test-ProfileApplicationProcess ([pscustomobject]@{ Name = 'dotnet.exe'; CommandLine = $null; ProcessId = 1 })) 'Unreadable hosted process should block.'
        Assert-True (-not (Test-ProfileApplicationProcess ([pscustomobject]@{ Name = 'notepad.exe'; CommandLine = 'notepad'; ProcessId = 1 }))) 'Unrelated process blocked.'
    }
    Invoke-Case 'schema-less legacy and supported explicit schema retain exact bytes' {
        param($f)
        $path = Join-Path $f.Source 'settings.json'
        $data = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable
        $data.SchemaVersion = 1
        Write-FixtureJson $path $data
        $result = Import-Fixture $f
        Assert-AppFixtureResult $f $result
    }
    Invoke-Case 'known settings types and ranges are enforced' {
        param($f)
        $path = Join-Path $f.Source 'settings.json'
        $data = [IO.File]::ReadAllText($path) | ConvertFrom-Json -AsHashtable
        $data.SearchPath = 'true'
        Write-FixtureJson $path $data
        Assert-Throws { Import-Fixture $f } 'Invalid field type'
    }
}
finally {
    # Only the synthetic temp tree is deleted. Production helper never deletes.
    if ([IO.Directory]::Exists($script:FixtureRoot)) { [IO.Directory]::Delete($script:FixtureRoot, $true) }
}
if ($script:Failed.Count) { throw ($script:Failed -join "`n") }
Write-Host "All $script:Passed $script:ProfileImportApp synthetic profile-import tests passed."
