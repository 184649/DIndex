function Assert-PerUserMsiPolicy {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$MsiPath,
          [Parameter(Mandatory)][string]$ProductSourcePath,
          [Parameter(Mandatory)][string]$ExpectedInstallFolder)

    # ICE91 is harmless only for an exclusively per-user package. Validate the
    # actual linked MSI before accepting that warning, not merely its source label.
    # https://learn.microsoft.com/en-us/windows/win32/msi/ice91
    [xml]$source = Get-Content -LiteralPath $ProductSourcePath -Raw
    $ns = [Xml.XmlNamespaceManager]::new($source.NameTable)
    $ns.AddNamespace('w', 'http://schemas.microsoft.com/wix/2006/wi')
    $package = $source.SelectSingleNode('/w:Wix/w:Product/w:Package', $ns)
    if ($null -eq $package -or $package.InstallScope -ne 'perUser' -or $package.InstallPrivileges -ne 'limited') {
        throw 'ICE91 policy requires explicit perUser and limited package authoring.'
    }
    $installer = New-Object -ComObject WindowsInstaller.Installer
    $database = $null
    try {
        $database = $installer.OpenDatabase($MsiPath, 0)
        function Read-PolicyRows([string]$Query, [int]$Columns) {
            $view = $database.OpenView($Query)
            try {
                # COM void methods may return values through the PowerShell adapter.
                # Only the explicit row objects below may enter the success stream.
                $null = $view.Execute()
                while ($null -ne ($record = $view.Fetch())) {
                    try {
                        $values = @()
                        for ($column = 1; $column -le $Columns; $column++) { $values += [string]$record.StringData($column) }
                        [pscustomobject]@{ Values = $values }
                    } finally { $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($record) }
                }
            } finally {
                $null = $view.Close()
                $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($view)
            }
        }
        $properties = @{}
        foreach ($row in (Read-PolicyRows 'SELECT `Property`, `Value` FROM `Property`' 2)) { $properties[$row.Values[0]] = $row.Values[1] }
        foreach ($name in @('ALLUSERS', 'MSIINSTALLPERUSER')) {
            if ($properties.ContainsKey($name) -and $properties[$name]) { throw "Per-user MSI must not author $name." }
        }
        $conditions = @((Read-PolicyRows 'SELECT `Condition` FROM `LaunchCondition`' 1) | ForEach-Object { $_.Values[0].Trim() })
        foreach ($required in @('NOT ALLUSERS', 'NOT MSIINSTALLPERUSER')) {
            if ($required -notin $conditions) { throw "Missing scope-override rejection: $required" }
        }
        $summary = $database.SummaryInformation(0)
        try {
            if (([int]($summary.Property(15)) -band 8) -ne 8) { throw 'MSI does not set the limited-privilege summary bit.' }
        } finally { $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($summary) }
        $directories = @{}
        foreach ($row in (Read-PolicyRows 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`' 3)) {
            # MSI DefaultDir may encode short|long names and target:source names.
            $targetName = ($row.Values[2].Split(':')[0].Split('|'))[-1]
            $directories[$row.Values[0]] = @{ Parent = $row.Values[1]; Name = $targetName }
        }
        if (-not $directories.ContainsKey('AppInstallFolder') -or
            $directories['AppInstallFolder'].Parent -ne 'UserProgramsFolder' -or
            $directories['AppInstallFolder'].Name -ne $ExpectedInstallFolder -or
            $directories['UserProgramsFolder'].Parent -ne 'LocalAppDataFolder' -or
            $directories['UserProgramsFolder'].Name -ne 'Programs') {
            throw 'MSI installation directory is not the fixed current-user Programs/app path.'
        }
        # Private mixed-case directory properties cannot be overridden from the
        # msiexec public-property command line. No public INSTALLFOLDER is authored.
        foreach ($id in $directories.Keys) {
            if ($id -ceq $id.ToUpperInvariant() -and $id -ne 'TARGETDIR') { throw "Public destination directory is forbidden: $id" }
        }
        if ($properties.ContainsKey('INSTALLFOLDER')) { throw 'Public INSTALLFOLDER override is forbidden.' }
        $componentCount = 0
        foreach ($row in (Read-PolicyRows 'SELECT `Component`, `Directory_` FROM `Component`' 2)) {
            $componentCount++
            $directory = $row.Values[1]
            $seen = [Collections.Generic.HashSet[string]]::new()
            while ($directory -notin @('AppInstallFolder', 'ProgramMenuFolder', 'DesktopFolder', 'StartupFolder')) {
                if (-not $directories.ContainsKey($directory) -or -not $seen.Add($directory)) {
                    throw "Component $($row.Values[0]) is outside the permitted per-user directories."
                }
                $directory = $directories[$directory].Parent
            }
        }
        if ($componentCount -eq 0) { throw 'MSI has no components.' }
        foreach ($row in (Read-PolicyRows 'SELECT `Root` FROM `Registry`' 1)) {
            if ($row.Values[0] -ne '1') { throw 'MSI has a registry write outside HKCU.' }
        }
        [pscustomobject]@{
            perUserOnly = $true; limitedPrivileges = $true; rejectsAllUsers = $true
            rejectsMsiInstallPerUserOverride = $true; privateFixedInstallDirectory = $true
            componentDirectoriesValidated = $componentCount; registryWrites = 'HKCU only'
        }
    } finally {
        if ($null -ne $database) { $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($database) }
        $null = [Runtime.InteropServices.Marshal]::ReleaseComObject($installer)
    }
}

function Get-AllowedWixWarnings {
    [CmdletBinding()]
    param([string[]]$Lines)
    $allowed = [Collections.Generic.List[string]]::new()
    foreach ($line in $Lines) {
        if ($line -match '(?i)\bwarning\b') {
            # Deliberately permit only the documented per-user FILE warning,
            # not every LGHT1076 diagnostic and not arbitrary ICE91 text.
            if ($line -notmatch ": warning LGHT1076 : ICE91: The file '[^']+' will be installed to the per user directory '[^']+' that (doesn't|does not) vary based on ALLUSERS value\. This file won't be copied to each user's profile even if a per machine installation is desired\.$") {
                throw "Unapproved WiX warning: $line"
            }
            $allowed.Add($line)
        }
    }
    [pscustomobject]@{ count = $allowed.Count; allowlist = @('LGHT1076/ICE91 per-user file warning'); source = 'https://learn.microsoft.com/en-us/windows/win32/msi/ice91' }
}
