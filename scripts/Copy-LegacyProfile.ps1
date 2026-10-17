#requires -Version 7.2
<#
Copies a legacy profile into a NEW profile after retaining and verifying a full
snapshot. Windows only; installs nothing. See ../docs/PROFILE_IMPORT.md.
Dot-sourcing exposes functions for the synthetic Windows regression fixture.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Source,
    [Parameter(Mandatory)][string]$Destination,
    [Parameter(Mandatory)][string]$BackupDirectory,
    [ValidateRange(1,100000)][int]$MaxFiles = 10000,
    [ValidateRange(1,1099511627776)][long]$MaxBytes = 2147483648,
    [ValidateRange(1,1099511627776)][long]$MaxFileBytes = 536870912,
    [ValidateRange(1,268435456)][long]$MaxJsonBytes = 33554432
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$script:ProfileImportApp = 'DIndex'

function Initialize-ProfilePathInspector {
    if ('ProfileImport.NativePaths' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace ProfileImport {
    public static class NativePaths {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle,
            StringBuilder path, uint length, uint flags);
        public static string FinalDirectory(string path) {
            using (var handle = CreateFileW(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero)) {
                if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                var result = new StringBuilder(32768);
                uint length = GetFinalPathNameByHandleW(handle, result, (uint)result.Capacity, 0);
                if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                if (length >= result.Capacity) throw new InvalidOperationException("Path is too long.");
                string final = result.ToString();
                if (!final.StartsWith(@"\\?\") || final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Only local filesystem paths are supported.");
                return final.Substring(4);
            }
        }
    }
}
'@
}

function Assert-NoReparseAncestors([string]$Path) {
    $current = $Path
    while ($current) {
        try {
            $attributes = [IO.File]::GetAttributes($current)
            if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Reparse points, junctions and symbolic links are not supported: $current"
            }
        }
        catch [IO.FileNotFoundException] { }
        catch [IO.DirectoryNotFoundException] { }
        $parent = [IO.Directory]::GetParent($current)
        $current = if ($parent) { $parent.FullName } else { $null }
    }
}

function Resolve-ProfilePath([string]$Path) {
    if ($Path -notmatch '^[A-Za-z]:[\\/]') {
        throw "Use an absolute local drive path; UNC, device, provider and relative paths are unsupported: $Path"
    }
    $parts = $Path.Substring(3) -split '[\\/]'
    foreach ($part in $parts) {
        if ($part -eq '') { continue }
        if ($part -in @('.', '..') -or $part -match '[. ]$' -or $part.Contains('~') -or
            $part.IndexOfAny([char[]]'<>:"|?*') -ge 0 -or $part -match '[\x00-\x1f]' -or
            $part -match '^(?i:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)') {
            throw "Unsafe or ambiguous path component: $part"
        }
    }
    $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Path))
    if ($full -eq [IO.Path]::GetPathRoot($full)) { throw 'A drive root cannot be a profile or backup directory.' }
    if ([IO.DriveInfo]::new([IO.Path]::GetPathRoot($full)).DriveType -ne [IO.DriveType]::Fixed) {
        throw 'Use a local fixed drive. Network and removable profiles are unsupported.'
    }
    Assert-NoReparseAncestors $full
    $ancestor = $full
    $suffix = [Collections.Generic.List[string]]::new()
    while (-not [IO.Directory]::Exists($ancestor)) {
        if ([IO.File]::Exists($ancestor)) { throw "Expected a directory, found a file: $ancestor" }
        $suffix.Insert(0, [IO.Path]::GetFileName($ancestor))
        $parent = [IO.Directory]::GetParent($ancestor)
        if (-not $parent) { throw "No accessible parent directory: $full" }
        $ancestor = $parent.FullName
    }
    Initialize-ProfilePathInspector
    # Resolves SUBST and other DOS-drive aliases before overlap comparisons.
    $resolved = [IO.Path]::TrimEndingDirectorySeparator([ProfileImport.NativePaths]::FinalDirectory($ancestor))
    foreach ($part in $suffix) { $resolved = [IO.Path]::Combine($resolved, $part) }
    Assert-NoReparseAncestors $resolved
    return $resolved
}

function Test-ProfilePathOverlap([string]$Left, [string]$Right) {
    $comparison = [StringComparison]::OrdinalIgnoreCase
    return $Left.Equals($Right, $comparison) -or
        $Left.StartsWith($Right.TrimEnd('\') + '\', $comparison) -or
        $Right.StartsWith($Left.TrimEnd('\') + '\', $comparison)
}

function Test-ProfileApplicationProcess($Process) {
    $name = [string]$Process.Name
    if ($name -match '^(?i:DIndex|DHub|ShortcutList)(?:[._-].*)?\.exe$' -or $name -ieq 'Updater.exe') { return $true }
    if ($name -ieq 'dotnet.exe') {
        # A hosted application with an unreadable command line cannot be ruled out.
        return [string]::IsNullOrWhiteSpace([string]$Process.CommandLine) -or
            [string]$Process.CommandLine -match '(?i:DIndex|DHub|ShortcutList)'
    }
    return $false
}

function Assert-ProfileApplicationsStopped {
    # Do not stop processes or fall back when process enumeration is unavailable.
    $processes = @(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)
    foreach ($process in $processes) {
        if (Test-ProfileApplicationProcess $process) {
            throw "Close DIndex, DHub, their tray instances and updaters before importing (detected $($process.Name), PID $($process.ProcessId))."
        }
    }
}

function Assert-NoProfileAlternateStreams([string]$Path) {
    $streams = @(Get-Item -LiteralPath $Path -Stream * -ErrorAction Stop)
    if (@($streams | Where-Object { $_.Stream -ne ':$DATA' }).Count) {
        throw "Alternate data streams need manual archival: $Path"
    }
}

function Get-ProfileManifest {
    param([string]$Root, [int]$MaxFiles, [long]$MaxBytes, [long]$MaxFileBytes, [switch]$HoldFiles)
    Assert-NoReparseAncestors $Root
    if (-not [IO.Directory]::Exists($Root)) { throw "Source directory is missing: $Root" }
    Assert-NoProfileAlternateStreams $Root
    $entries = [Collections.Generic.List[object]]::new()
    $handles = [Collections.Generic.List[IO.FileStream]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    [long]$total = 0
    [int]$files = 0
    try {
        while ($pending.Count) {
            $directory = $pending.Pop()
            Assert-NoReparseAncestors $directory
            foreach ($path in [IO.Directory]::EnumerateFileSystemEntries($directory)) {
                $relative = [IO.Path]::GetRelativePath($Root, $path)
                if ([IO.Path]::IsPathRooted($relative) -or $relative -match '(^|[\\/])\.\.([\\/]|$)') {
                    throw "Traversal in source inventory: $path"
                }
                if (-not $seen.Add($relative)) { throw "Case-ambiguous source path: $relative" }
                foreach ($part in ($relative -split '[\\/]')) {
                    if ($part -in @('.', '..') -or $part -match '[. ]$' -or $part.Contains('~') -or
                        $part.IndexOfAny([char[]]'<>:"|?*') -ge 0 -or $part -match '[\x00-\x1f]' -or
                        $part -match '^(?i:CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(?:\.|$)') {
                        throw "Unsafe source path component: $part"
                    }
                }
                Assert-NoReparseAncestors $path
                $attributes = [IO.File]::GetAttributes($path)
                if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Reparse point: $path" }
                if ($entries.Count -ge ($MaxFiles * 4L)) { throw 'Profile entry-count limit exceeded.' }
                Assert-NoProfileAlternateStreams $path
                if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) {
                    $entries.Add([pscustomobject]@{ Path = $relative; Kind = 'Directory'; Length = 0L; SHA256 = ''; LastWriteUtcTicks = 0L })
                    $pending.Push($path)
                    continue
                }
                $files++
                if ($files -gt $MaxFiles) { throw 'Profile file-count limit exceeded.' }
                # Never silently omit NTFS alternate streams from a claimed full snapshot.
                $input = [IO.FileStream]::new($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                try {
                    $length = $input.Length
                    if ($length -gt $MaxFileBytes -or $length -gt ($MaxBytes - $total)) { throw 'Profile byte-size limit exceeded.' }
                    $total += $length
                    $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($input))
                    $ticks = [IO.File]::GetLastWriteTimeUtc($path).Ticks
                    $entries.Add([pscustomobject]@{ Path = $relative; Kind = 'File'; Length = $length; SHA256 = $hash; LastWriteUtcTicks = $ticks })
                    if ($HoldFiles) { $handles.Add($input); $input = $null }
                }
                finally { if ($input) { $input.Dispose() } }
            }
        }
        return [pscustomobject]@{ Entries = @($entries | Sort-Object -Property Path); TotalBytes = $total; Handles = $handles }
    }
    catch {
        foreach ($handle in $handles) { $handle.Dispose() }
        throw
    }
}

function Assert-ProfileManifestsMatch($Expected, $Actual, [switch]$IncludeWriteTimes) {
    if ($Expected.Entries.Count -ne $Actual.Entries.Count -or $Expected.TotalBytes -ne $Actual.TotalBytes) {
        throw 'Profile snapshot changed: file/directory inventory or total bytes differ.'
    }
    for ($i = 0; $i -lt $Expected.Entries.Count; $i++) {
        $left = $Expected.Entries[$i]; $right = $Actual.Entries[$i]
        if ($left.Path -cne $right.Path -or $left.Kind -cne $right.Kind -or
            $left.Length -ne $right.Length -or $left.SHA256 -cne $right.SHA256 -or
            ($IncludeWriteTimes -and $left.LastWriteUtcTicks -ne $right.LastWriteUtcTicks)) {
            throw "Profile snapshot changed: $($left.Path)"
        }
    }
}

function Copy-ProfileSnapshot([string]$SourceRoot, [string]$TargetRoot, $Manifest) {
    Assert-NoReparseAncestors $SourceRoot
    Assert-NoReparseAncestors $TargetRoot
    foreach ($entry in $Manifest.Entries) {
        $sourcePath = [IO.Path]::Combine($SourceRoot, $entry.Path)
        $targetPath = [IO.Path]::Combine($TargetRoot, $entry.Path)
        Assert-NoReparseAncestors $sourcePath
        Assert-NoReparseAncestors $targetPath
        if ($entry.Kind -eq 'Directory') {
            [void][IO.Directory]::CreateDirectory($targetPath)
            continue
        }
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($targetPath))
        $input = [IO.FileStream]::new($sourcePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try {
            $output = [IO.FileStream]::new($targetPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $input.CopyTo($output); $output.Flush($true) }
            finally { $output.Dispose() }
        }
        finally { $input.Dispose() }
    }
}

function Write-NewProfileText([string]$Path, [string]$Text) {
    Assert-NoReparseAncestors $Path
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    }
    finally { $stream.Dispose() }
}

function Assert-JsonNoDuplicates([Text.Json.JsonElement]$Element) {
    if ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($property in $Element.EnumerateObject()) {
            if (-not $names.Add($property.Name)) { throw "Duplicate or case-ambiguous JSON property: $($property.Name)" }
            Assert-JsonNoDuplicates $property.Value
        }
    }
    elseif ($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array) {
        foreach ($item in $Element.EnumerateArray()) { Assert-JsonNoDuplicates $item }
    }
}

function Read-StrictProfileJson([string]$Path, [long]$MaxJsonBytes) {
    Assert-NoReparseAncestors $Path
    if (-not [IO.File]::Exists($Path)) { throw "Required canonical file is missing; no recovery is attempted: $Path" }
    if ([IO.FileInfo]::new($Path).Length -gt $MaxJsonBytes) { throw "JSON size limit exceeded: $Path" }
    $bytes = [IO.File]::ReadAllBytes($Path)
    $text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    if ($text.Length -gt 0 -and $text[0] -eq [char]0xfeff) { $text = $text.Substring(1) }
    $options = [Text.Json.JsonDocumentOptions]::new()
    $options.MaxDepth = 64
    $options.AllowTrailingCommas = $false
    $options.CommentHandling = [Text.Json.JsonCommentHandling]::Disallow
    $document = [Text.Json.JsonDocument]::Parse($text, $options)
    try {
        Assert-JsonNoDuplicates $document.RootElement
        return $document.RootElement.Clone()
    }
    finally { $document.Dispose() }
}

function Assert-JsonShape([Text.Json.JsonElement]$Element, [hashtable]$Fields, [string[]]$Required, [string]$Context, [switch]$AllowUnknown) {
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw "Expected a JSON object: $Context" }
    foreach ($field in $Required) {
        $value = [Text.Json.JsonElement]::new()
        if (-not $Element.TryGetProperty($field, [ref]$value)) { throw "Missing canonical field $Context.$field; manual review required." }
    }
    foreach ($property in $Element.EnumerateObject()) {
        if ($AllowUnknown -and -not $Fields.ContainsKey($property.Name)) { continue }
        if (-not $Fields.ContainsKey($property.Name) -or $property.Name -cnotin @($Fields.Keys)) {
            throw "Unsupported or incorrectly cased field $Context.$($property.Name); manual review required."
        }
        $kind = $property.Value.ValueKind
        $valid = switch ($Fields[$property.Name]) {
            'String' { $kind -eq [Text.Json.JsonValueKind]::String }
            'Boolean' { $kind -in @([Text.Json.JsonValueKind]::True, [Text.Json.JsonValueKind]::False) }
            'Object' { $kind -eq [Text.Json.JsonValueKind]::Object }
            'Array' { $kind -eq [Text.Json.JsonValueKind]::Array }
            'Integer' { $n = 0; $kind -eq [Text.Json.JsonValueKind]::Number -and $property.Value.TryGetInt32([ref]$n) }
            'DateTime' { $d = [datetime]::MinValue; $kind -eq [Text.Json.JsonValueKind]::String -and $property.Value.TryGetDateTime([ref]$d) }
            'NullableDateTime' { $d = [datetime]::MinValue; $kind -eq [Text.Json.JsonValueKind]::Null -or ($kind -eq [Text.Json.JsonValueKind]::String -and $property.Value.TryGetDateTime([ref]$d)) }
            default { throw "Unknown validator type: $($Fields[$property.Name])" }
        }
        if (-not $valid) { throw "Invalid field type: $Context.$($property.Name)" }
    }
}

function Assert-JsonStringArray([Text.Json.JsonElement]$Element, [string]$Context, [switch]$AllowEmptyStrings) {
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Array) { throw "Expected an array: $Context" }
    foreach ($value in $Element.EnumerateArray()) {
        if ($value.ValueKind -ne [Text.Json.JsonValueKind]::String -or
            (-not $AllowEmptyStrings -and [string]::IsNullOrWhiteSpace($value.GetString()))) {
            throw "Invalid string list: $Context"
        }
    }
}

function Get-ValidatedProfileEntries([string]$Root, $Manifest, [long]$MaxJsonBytes) {
    $settings = Read-StrictProfileJson ([IO.Path]::Combine($Root, 'settings.json')) $MaxJsonBytes
    $fields = @{ SearchRoots = 'Array'; SearchPath = 'Boolean'; IncludeDirectories = 'Boolean'; ResultLimit = 'Integer'; ExcludeFolderNames = 'Array'; SchemaVersion = 'Integer' }
    Assert-JsonShape $settings $fields @('SearchRoots', 'SearchPath', 'IncludeDirectories', 'ResultLimit', 'ExcludeFolderNames') 'settings.json' -AllowUnknown
    Assert-JsonStringArray ($settings.GetProperty('SearchRoots')) 'SearchRoots'
    Assert-JsonStringArray ($settings.GetProperty('ExcludeFolderNames')) 'ExcludeFolderNames'
    $limit = $settings.GetProperty('ResultLimit').GetInt32()
    if ($limit -lt 1 -or $limit -gt 100000) { throw 'ResultLimit must be between 1 and 100000.' }
    $schema = [Text.Json.JsonElement]::new()
    if ($settings.TryGetProperty('SchemaVersion', [ref]$schema) -and ($schema.GetInt32() -lt 0 -or $schema.GetInt32() -gt 1)) {
        throw 'Unsupported DIndex settings schema; no fallback or downgrade is attempted.'
    }
    # Unknown properties are retained in the original bytes, never reserialized.
    return @($Manifest.Entries | Where-Object { $_.Kind -eq 'File' -and $_.Path -ieq 'settings.json' })
}

function Invoke-LegacyProfileImport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$BackupDirectory,
        [ValidateRange(1,100000)][int]$MaxFiles = 10000,
        [ValidateRange(1,1099511627776)][long]$MaxBytes = 2147483648,
        [ValidateRange(1,1099511627776)][long]$MaxFileBytes = 536870912,
        [ValidateRange(1,268435456)][long]$MaxJsonBytes = 33554432
    )
    if (-not $IsWindows) { throw 'Run this helper in PowerShell 7.2 or later on Windows. It installs no tools.' }
    $Source = Resolve-ProfilePath $Source
    $Destination = Resolve-ProfilePath $Destination
    $BackupDirectory = Resolve-ProfilePath $BackupDirectory
    if ((Test-ProfilePathOverlap $Source $Destination) -or
        (Test-ProfilePathOverlap $Source $BackupDirectory) -or
        (Test-ProfilePathOverlap $Destination $BackupDirectory)) {
        throw 'Source, Destination and BackupDirectory must be separate, non-overlapping directory trees.'
    }
    if (-not [IO.Directory]::Exists($Source)) { throw "Source directory is missing: $Source" }
    if ([IO.Directory]::Exists($Destination) -or [IO.File]::Exists($Destination)) {
        throw 'Destination must not exist, even if empty. Choose a NEW preview profile; this helper never merges or replaces.'
    }
    $destinationParent = [IO.Path]::GetDirectoryName($Destination)
    if (-not [IO.Directory]::Exists($destinationParent)) { throw 'Destination parent must already exist.' }
    if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($BackupDirectory))) { throw 'BackupDirectory parent must already exist.' }
    Assert-ProfileApplicationsStopped
    $limits = @{ MaxFiles = $MaxFiles; MaxBytes = $MaxBytes; MaxFileBytes = $MaxFileBytes }
    $sourceManifest = $null
    $backupRun = $null
    $stage = $null
    $published = $false
    try {
        # Acquire read handles without write/delete sharing for every source file.
        # Keep them until publication; detect additions/removals using fresh scans.
        $sourceManifest = Get-ProfileManifest -Root $Source @limits -HoldFiles
        Assert-NoReparseAncestors $BackupDirectory
        [void][IO.Directory]::CreateDirectory($BackupDirectory)
        $runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ') + '-' + [Guid]::NewGuid().ToString('N')
        $backupRun = [IO.Path]::Combine($BackupDirectory, "$script:ProfileImportApp-$runId")
        if ([IO.Directory]::Exists($backupRun) -or [IO.File]::Exists($backupRun)) { throw 'Backup path collision.' }
        $snapshot = [IO.Path]::Combine($backupRun, 'snapshot')
        [void][IO.Directory]::CreateDirectory($snapshot)
        $manifestText = [ordered]@{ Application = $script:ProfileImportApp; Source = $Source; Destination = $Destination; CreatedUtc = [DateTime]::UtcNow.ToString('O'); TotalBytes = $sourceManifest.TotalBytes; Entries = $sourceManifest.Entries } | ConvertTo-Json -Depth 8
        Write-NewProfileText ([IO.Path]::Combine($backupRun, 'manifest.json')) $manifestText
        Copy-ProfileSnapshot $Source $snapshot $sourceManifest
        $backupManifest = Get-ProfileManifest -Root $snapshot @limits
        Assert-ProfileManifestsMatch $sourceManifest $backupManifest
        $afterBackup = Get-ProfileManifest -Root $Source @limits
        Assert-ProfileManifestsMatch $sourceManifest $afterBackup -IncludeWriteTimes
        Write-NewProfileText ([IO.Path]::Combine($backupRun, 'SNAPSHOT_VERIFIED.txt')) 'The complete inventory and SHA-256 file hashes matched the unchanged source after backup.'

        # Validate only the verified backup, never application recovery/load paths.
        $selected = @(Get-ValidatedProfileEntries $snapshot $backupManifest $MaxJsonBytes)
        if (-not $selected.Count) { throw 'No canonical profile data was selected.' }
        [long]$selectedBytes = 0
        foreach ($entry in $selected) { if ($entry.Kind -eq 'File') { $selectedBytes += $entry.Length } }
        $selectedManifest = [pscustomobject]@{ Entries = $selected; TotalBytes = $selectedBytes }
        $stage = [IO.Path]::Combine($destinationParent, ".$script:ProfileImportApp-import-$runId")
        Assert-NoReparseAncestors $stage
        if ([IO.Directory]::Exists($stage) -or [IO.File]::Exists($stage)) { throw 'Staging path collision.' }
        [void][IO.Directory]::CreateDirectory($stage)
        Copy-ProfileSnapshot $snapshot $stage $selectedManifest
        $stagedManifest = Get-ProfileManifest -Root $stage @limits
        Assert-ProfileManifestsMatch $selectedManifest $stagedManifest
        # Verify the original complete backup again, including history not imported.
        Assert-ProfileManifestsMatch $backupManifest (Get-ProfileManifest -Root $snapshot @limits)
        Assert-ProfileManifestsMatch $sourceManifest (Get-ProfileManifest -Root $Source @limits) -IncludeWriteTimes
        Assert-ProfileApplicationsStopped
        Assert-NoReparseAncestors $Destination
        Assert-NoReparseAncestors $stage
        if ([IO.Directory]::Exists($Destination) -or [IO.File]::Exists($Destination)) { throw 'Destination appeared during import; publication cancelled.' }
        Assert-ProfileManifestsMatch $selectedManifest (Get-ProfileManifest -Root $stage @limits)
        # Same-parent rename; no existing directory is ever removed or replaced.
        [IO.Directory]::Move($stage, $Destination)
        $published = $true
        try { Write-NewProfileText ([IO.Path]::Combine($backupRun, 'IMPORT_SUCCEEDED.txt')) "Published to: $Destination" }
        catch { Write-Warning "Profile was published, but the backup success marker could not be written: $($_.Exception.Message)" }
        return [pscustomobject]@{ Application = $script:ProfileImportApp; Destination = $Destination; Backup = $backupRun; SourceUnchanged = $true; Published = $true }
    }
    catch {
        $reason = $_.Exception.Message
        if ($backupRun -and [IO.Directory]::Exists($backupRun)) {
            try { Write-NewProfileText ([IO.Path]::Combine($backupRun, 'IMPORT_FAILED.txt')) "No destination was published: $(-not $published)`nReason: $reason`nRetained staging directory: $stage" }
            catch { Write-Warning 'Could not write the failure marker; all backup and staging files are still retained.' }
        }
        throw "Import stopped. $reason Backup retained at: $backupRun. Staging retained at: $stage. No source files were written or deleted."
    }
    finally {
        if ($sourceManifest) { foreach ($handle in $sourceManifest.Handles) { $handle.Dispose() } }
    }
}

if ($MyInvocation.InvocationName -ne '.') {
    Invoke-LegacyProfileImport @PSBoundParameters
}
