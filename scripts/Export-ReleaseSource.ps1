#requires -Version 7.2
<# Read-only Git export. Filters only tracked generated bin/obj trees and the
   two historical root release ZIPs; never deletes files from the checkout. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SourceCommit,
    [Parameter(Mandatory)][string]$RepositoryDirectory,
    [Parameter(Mandatory)][string]$OutputArchive,
    [string]$ManifestPath,
    [string]$ArchivePrefix = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Select-ReleaseSourceEntries([string]$TreeOutput) {
    if ($TreeOutput.Length -gt 8388608) { throw 'Source tree exceeds the bounded inventory size.' }
    $included = [Collections.Generic.List[string]]::new()
    $excluded = [Collections.Generic.List[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $blobs = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($record in $TreeOutput.Split([char]0)) {
        if ($record.Length -eq 0) { continue }
        if ($record -cnotmatch '\A(100644|100755) blob ([0-9a-f]{40})\t([^\x00]+)\z') { throw 'Source tree contains a non-regular file or malformed entry.' }
        $blobSha = $Matches[2]
        $path = $Matches[3]
        if ($path.Length -gt 1024 -or $path -match '[\\\x00-\x1f\x7f<>:"|?*]' -or $path.StartsWith('/') -or -not $seen.Add($path)) {
            throw "Unsafe or case-ambiguous source path: $path"
        }
        $parts = $path.Split('/')
        foreach ($part in $parts) {
            if ($part -in @('', '.', '..', '.git') -or $part -match '[. ]\z' -or
                $part -match '\A(?i:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|\z)') { throw "Unsafe Windows source path: $path" }
        }
        if (@($parts | Where-Object { $_ -ieq 'bin' -or $_ -ieq 'obj' }).Count -or
            $path -ieq 'DIndex.zip' -or $path -ieq 'DIndexUpdater.zip') { $excluded.Add($path) }
        else { $included.Add($path); $blobs.Add($path, $blobSha) }
        if ($seen.Count -gt 10000) { throw 'Source tree exceeds the bounded entry count.' }
    }
    if ($included.Count -eq 0) { throw 'Source selection is empty.' }
    $included.Sort([StringComparer]::Ordinal)
    $excluded.Sort([StringComparer]::Ordinal)
    [pscustomobject]@{ Included = $included.ToArray(); Excluded = $excluded.ToArray(); Blobs = $blobs }
}
function Assert-ReleaseArchiveArgumentLimit([string[]]$Arguments) {
    # Conservative Windows quoting budget, including doubled quotes/backslashes.
    [long]$length = 1024
    foreach ($argument in $Arguments) { $length += 2L * $argument.Length + 4 }
    if ($length -ge 30000) { throw 'Selected source paths exceed the safe Windows argument-length budget; no partial archive was created.' }
}
function Invoke-SourceGit([string]$Git, [string]$Repository, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Git
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false, $true)
    $start.ArgumentList.Add('--literal-pathspecs')
    $start.ArgumentList.Add('-C'); $start.ArgumentList.Add($Repository)
    # Git archive honors core.autocrlf and can otherwise rewrite committed LF
    # blobs on Windows. These per-process overrides never change Git settings.
    $start.ArgumentList.Add('-c'); $start.ArgumentList.Add('core.autocrlf=false')
    $start.ArgumentList.Add('-c'); $start.ArgumentList.Add('core.eol=lf')
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Cannot start read-only Git export.' }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw 'Read-only Git export timed out.' }
        $result = $stdout.GetAwaiter().GetResult()
        $errorText = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Read-only Git export failed: $errorText" }
        return $result
    } finally { $process.Dispose() }
}
# Dot-sourcing exposes only the pure selector/argument tests above.
if ($MyInvocation.InvocationName -eq '.') { return }
if ($SourceCommit -cnotmatch '\A[0-9a-f]{40}\z') { throw 'SourceCommit must be a lowercase 40-hex commit.' }
if ($ArchivePrefix -and ($ArchivePrefix -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]*/\z' -or $ArchivePrefix.TrimEnd('/') -match '[. ]\z' -or
    $ArchivePrefix.TrimEnd('/') -match '\A(?i:CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|\z)')) { throw 'ArchivePrefix must be empty or one safe directory name with a trailing slash.' }
$RepositoryDirectory = (Resolve-Path -LiteralPath $RepositoryDirectory).Path
$OutputArchive = [IO.Path]::GetFullPath($OutputArchive)
if ([IO.File]::Exists($OutputArchive) -or [IO.Directory]::Exists($OutputArchive)) { throw 'Output archive already exists; no file will be replaced.' }
if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($OutputArchive))) { throw 'Output archive parent directory must already exist.' }
if ($ManifestPath) {
    $ManifestPath = [IO.Path]::GetFullPath($ManifestPath)
    if ($ManifestPath -ieq $OutputArchive -or [IO.File]::Exists($ManifestPath) -or [IO.Directory]::Exists($ManifestPath) -or
        -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($ManifestPath))) { throw 'ManifestPath must be a new file in an existing directory.' }
}
$git = (Get-Command git -ErrorAction Stop).Source
$commit = (Invoke-SourceGit $git $RepositoryDirectory @('rev-parse', "${SourceCommit}^{commit}")).Trim()
if ($commit -cne $SourceCommit) { throw 'SourceCommit does not identify the exact requested commit.' }
$tree = (Invoke-SourceGit $git $RepositoryDirectory @('rev-parse', "${SourceCommit}^{tree}")).Trim()
if ($tree -cnotmatch '\A[0-9a-f]{40}\z') { throw 'Malformed source tree identity.' }
$selection = Select-ReleaseSourceEntries (Invoke-SourceGit $git $RepositoryDirectory @('ls-tree', '-r', '-z', '--full-tree', $SourceCommit))
$temporary = Join-Path ([IO.Path]::GetDirectoryName($OutputArchive)) ('source-export-' + [guid]::NewGuid().ToString('N') + '.zip')
$arguments = @('archive', '--format=zip', "--prefix=$ArchivePrefix", "--output=$temporary", $SourceCommit, '--') + $selection.Included
Assert-ReleaseArchiveArgumentLimit (@('--literal-pathspecs', '-C', $RepositoryDirectory) + $arguments)
$reserved = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
$reserved.Dispose()
try {
    $null = Invoke-SourceGit $git $RepositoryDirectory $arguments
    $zip = [IO.Compression.ZipFile]::OpenRead($temporary)
    try {
        $actual = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        [long]$totalBytes = 0
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            if (-not $entry.FullName.StartsWith($ArchivePrefix, [StringComparison]::Ordinal) -or
                -not $actual.Add($entry.FullName.Substring($ArchivePrefix.Length))) { throw 'Unexpected or duplicate archived path.' }
            $relative = $entry.FullName.Substring($ArchivePrefix.Length)
            $totalBytes += $entry.Length
            if (-not $selection.Blobs.ContainsKey($relative) -or $entry.Length -gt 268435456 -or $totalBytes -gt 536870912) { throw 'Unexpected or oversized source archive content.' }
            # Git archive may honor export-subst attributes. Verify each entry's
            # exact Git blob identity, not just its name, before accepting it.
            $hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA1)
            $inputStream = $entry.Open()
            try {
                $hash.AppendData([Text.Encoding]::ASCII.GetBytes("blob $($entry.Length)" + [char]0))
                $buffer = [byte[]]::new(65536)
                while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) { $hash.AppendData($buffer, 0, $read) }
                if ([Convert]::ToHexString($hash.GetHashAndReset()) -ine $selection.Blobs[$relative]) { throw "Source archive bytes differ from committed blob: $relative" }
            } finally { $inputStream.Dispose(); $hash.Dispose() }
        }
        if ($actual.Count -ne $selection.Included.Count -or -not $actual.SetEquals($selection.Included)) { throw 'Git archive omitted or added selected source files; export attributes are unsupported.' }
    } finally { $zip.Dispose() }
    [IO.File]::Move($temporary, $OutputArchive, $false)
} finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
$pathBytes = [Text.Encoding]::UTF8.GetBytes(($selection.Included -join "`n") + "`n")
$manifest = [ordered]@{
    schemaVersion = 1; sourceCommit = $SourceCommit; sourceTree = $tree; filterId = 'exclude-generated-v1'
    filterDescription = 'Exclude case-insensitive bin/obj path segments and the root DIndex.zip/DIndexUpdater.zip only. Preserve all other regular tracked source files.'
    archivePrefix = $ArchivePrefix; includedCount = $selection.Included.Count; excludedCount = $selection.Excluded.Count
    includedPathsSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pathBytes))
    includedPaths = $selection.Included; excludedPaths = $selection.Excluded
    includedBlobs = @($selection.Included | ForEach-Object { [ordered]@{ path = $_; sha = $selection.Blobs[$_] } })
    archiveSha256 = (Get-FileHash -LiteralPath $OutputArchive -Algorithm SHA256).Hash
}
if ($ManifestPath) {
    $stream = [IO.File]::Open($ManifestPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Depth 5) + "`n"); $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose() }
}
[pscustomobject]$manifest
