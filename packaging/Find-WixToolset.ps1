# Discovery only: never downloads, installs, or executes a candidate tool.
# WiX's native resource deliberately uses 3.14.8722.0 in its numeric fields,
# but 3.14.1.8722 in the FileVersion string. Do not compare FileBuildPart to 1.
# Source: wixtoolset/wix3 b40e9a32c24033e11b77baf2c91a704382f898ed,
# src/common/wix.rc and tools/WixBuild.Version.targets.
function Find-PreinstalledWix {
    [CmdletBinding()]
    param([string]$WixBin)

    $expectedVersion = '3.14.1.8722'
    $candidates = [Collections.Generic.List[object]]::new()
    if ($WixBin) {
        $candidates.Add([pscustomobject]@{ Path = $WixBin; Source = '-WixBin' })
    }
    # The normal installation exports the root as WIX. Also accept an existing
    # bin directory value, and inspect Machine/User scope when PATH is stale.
    foreach ($scope in @('Process', 'Machine', 'User')) {
        try { $root = [Environment]::GetEnvironmentVariable('WIX', $scope) }
        catch { Write-Host "WiX discovery: cannot read WIX ($scope): $($_.Exception.Message)"; continue }
        Write-Host "WiX discovery: WIX ($scope) = [$root]"
        if (-not [string]::IsNullOrWhiteSpace($root)) {
            $root = $root.Trim().Trim('"')
            $candidates.Add([pscustomobject]@{ Path = (Join-Path $root 'bin'); Source = "WIX ($scope) + bin" })
            $candidates.Add([pscustomobject]@{ Path = $root; Source = "WIX ($scope) directly" })
        }
    }
    foreach ($name in @('candle.exe', 'light.exe')) {
        $commands = @(Get-Command $name -CommandType Application -All -ErrorAction SilentlyContinue)
        if ($commands.Count -eq 0) { Write-Host "WiX discovery: $name is not on PATH." }
        foreach ($command in $commands) {
            $candidates.Add([pscustomobject]@{ Path = (Split-Path $command.Source -Parent); Source = "PATH: $($command.Source)" })
        }
    }
    foreach ($programFiles in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ($programFiles) {
            $candidates.Add([pscustomobject]@{ Path = (Join-Path $programFiles 'WiX Toolset v3.14\bin'); Source = 'standard installation' })
        }
    }

    $checked = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $candidates) {
        try { $candidate = [IO.Path]::GetFullPath($entry.Path) }
        catch { Write-Host "WiX discovery: invalid path [$($entry.Path)] from $($entry.Source): $($_.Exception.Message)"; continue }
        if (-not $checked.Add($candidate)) { continue }
        Write-Host "WiX discovery: checking [$candidate] from $($entry.Source)"
        $valid = $true
        foreach ($name in @('candle.exe', 'light.exe', 'WixUIExtension.dll', 'WixUtilExtension.dll')) {
            $path = Join-Path $candidate $name
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                Write-Host "  MISSING: $path"
                $valid = $false
                continue
            }
            try {
                $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
                $fileVersion = ([string]$info.FileVersion).Trim()
                $numericVersion = '{0}.{1}.{2}.{3}' -f $info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart
                Write-Host "  FOUND: $path"
                Write-Host "    FileVersion=[$fileVersion]; ProductVersion=[$($info.ProductVersion)]; NumericFileVersion=[$numericVersion]"
                # Both executables must report the exact pinned release string.
                # Extensions are required and their metadata is logged as well.
                if ($name.EndsWith('.exe', [StringComparison]::OrdinalIgnoreCase) -and $fileVersion -cne $expectedVersion) {
                    Write-Host "    REJECTED: expected FileVersion string [$expectedVersion]."
                    $valid = $false
                }
            } catch {
                Write-Host "  REJECTED: cannot read version metadata for [$path]: $($_.Exception.Message)"
                $valid = $false
            }
        }
        if ($valid) {
            Write-Host "WiX discovery: selected [$candidate], release $expectedVersion."
            return (Resolve-Path -LiteralPath $candidate).Path
        }
    }
    Write-Host "WiX discovery: no complete preinstalled release $expectedVersion was found. No tools were installed."
    return $null
}
