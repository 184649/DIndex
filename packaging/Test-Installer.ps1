#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^net(8|10)\.0-windows(?:\.[0-9.]+)?$')][string]$TargetFramework,
    [ValidatePattern('^(8|10)\.0\.\d+$')][string]$RuntimeFrameworkVersion,
    [string]$OutputDirectory,
    [string]$WixBin,
    [string]$WixSourceArchive,
    [switch]$AllowDisposableUserProfile
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'MSI integration tests require Windows.' }
if (-not $AllowDisposableUserProfile) { throw 'Use only in a disposable Windows VM/runner user profile, with -AllowDisposableUserProfile. Never run this on a real user profile.' }
$config = Get-Content (Join-Path $PSScriptRoot 'Product.json') -Raw | ConvertFrom-Json
$fixtureId = [guid]::NewGuid().ToString('N')
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "out/tests/$fixtureId" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$logs = Join-Path $OutputDirectory "logs-$fixtureId"
[IO.Directory]::CreateDirectory($logs) | Out-Null
$results = [Collections.Generic.List[object]]::new()
$packages = [Collections.Generic.List[object]]::new()
$registrationChecks = [Collections.Generic.List[object]]::new()
$runningFixture = $null
$success = $false
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentUserSid = $identity.User.Value
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$identity.Dispose()
. (Join-Path $PSScriptRoot 'MsiRegistration.ps1')

function Assert-True([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Record-Pass([string]$Name) {
    $results.Add([pscustomobject]@{ name = $Name; result = 'passed' })
    Write-Host "PASS $Name"
}
function Write-MsiFailureDiagnostics([string]$LogPath) {
    # Keep the full log on disk; CI gets at most 80 relevant lines without uploads.
    if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        Write-Host "MSI diagnostics unavailable: log was not created at $LogPath"
        return
    }
    try {
        $lines = @(Get-Content -LiteralPath $LogPath)
        $indices = [Collections.Generic.SortedSet[int]]::new()
        for ($i = 0; $i -lt $lines.Count; $i++) {
            if ($lines[$i] -match 'Return value 3|Error [0-9]+|MainEngineThread is returning|exception|failed|cannot') {
                $first = [Math]::Max(0, $i - 6)
                $last = [Math]::Min($lines.Count - 1, $i + 3)
                for ($j = $first; $j -le $last; $j++) { $null = $indices.Add($j) }
            }
        }
        # Prefer the last failure context when rollback also emitted errors.
        $selected = @($indices | Select-Object -Last 60)
        $tailStart = [Math]::Max(0, $lines.Count - 20)
        if ($lines.Count -gt 0) { $selected += @($tailStart..($lines.Count - 1)) }
        $selected = @($selected | Where-Object { $_ -ge 0 -and $_ -lt $lines.Count } | Sort-Object -Unique | Select-Object -Last 80)
        Write-Host "--- MSI failure excerpt: $LogPath (full log retained; $($selected.Count) of $($lines.Count) lines) ---"
        foreach ($index in $selected) { Write-Host ('{0}: {1}' -f ($index + 1), $lines[$index]) }
        Write-Host '--- End MSI failure excerpt ---'
    } catch {
        Write-Warning "Cannot read MSI diagnostic log: $($_.Exception.Message)"
    }
}
function Invoke-Msi([string]$Name, [string[]]$Arguments, [int[]]$Expected = @(0)) {
    $log = Join-Path $logs "$Name.log"
    $all = $Arguments + @('/qn', '/norestart', 'REBOOT=ReallySuppress', '/l*v', ('"' + $log + '"'))
    $process = Start-Process -FilePath "$env:SystemRoot\System32\msiexec.exe" -ArgumentList $all -PassThru -Wait
    if ($process.ExitCode -notin $Expected) {
        Write-MsiFailureDiagnostics $log
        throw "MSI $Name returned $($process.ExitCode); expected $($Expected -join ','). Log: $log"
    }
    return $process.ExitCode
}
function Open-MsiDatabase([string]$Path) {
    $installer = New-Object -ComObject WindowsInstaller.Installer
    return $installer.OpenDatabase($Path, 0)
}
function Get-MsiValue($Database, [string]$Sql) {
    $view = $Database.OpenView($Sql)
    try {
        $null = $view.Execute()
        $row = $view.Fetch()
        if ($null -eq $row) { return $null }
        return $row.StringData(1)
    } finally { $null = $view.Close() }
}
function Assert-PackagePolicy($Build) {
    $db = Open-MsiDatabase $Build.msiPath
    Assert-True ((Get-MsiValue $db 'SELECT `Value` FROM `Property` WHERE `Property` = ''ALLUSERS''') -in @($null, '')) 'MSI unexpectedly sets ALLUSERS.'
    Assert-True ((Get-MsiValue $db 'SELECT `Value` FROM `Property` WHERE `Property` = ''MSIRESTARTMANAGERCONTROL''') -eq 'DisableShutdown') 'Automatic application shutdown is enabled.'
    Assert-True ((Get-MsiValue $db 'SELECT `Level` FROM `Feature` WHERE `Feature` = ''DesktopShortcutFeature''') -eq '2') 'Desktop shortcut is enabled by default.'
    Assert-True ((Get-MsiValue $db 'SELECT `Level` FROM `Feature` WHERE `Feature` = ''StartupShortcutFeature''') -eq '2') 'Startup is enabled by default.'
    $remove = [int](Get-MsiValue $db 'SELECT `Sequence` FROM `InstallExecuteSequence` WHERE `Action` = ''RemoveExistingProducts''')
    $initialize = [int](Get-MsiValue $db 'SELECT `Sequence` FROM `InstallExecuteSequence` WHERE `Action` = ''InstallInitialize''')
    $finalize = [int](Get-MsiValue $db 'SELECT `Sequence` FROM `InstallExecuteSequence` WHERE `Action` = ''InstallFinalize''')
    $guard = [int](Get-MsiValue $db 'SELECT `Sequence` FROM `InstallExecuteSequence` WHERE `Action` = ''RequireClosedApp''')
    Assert-True ($remove -gt $initialize -and $remove -lt $finalize) 'Old-product removal is outside the MSI transaction.'
    Assert-True ($guard -gt 0 -and $guard -lt $initialize) 'Running-app guard must execute before any transactional changes.'
    $summary = $db.SummaryInformation(0)
    Assert-True (([int]($summary.Property(15)) -band 8) -eq 8) 'Package requests elevated privileges.'
    Assert-True ([string]($summary.Property(7)) -like 'x64;*') 'Package is not x64.'
    Record-Pass "MSI table policy $($Build.packageVersion)"
}
function Assert-ProductRegistration($Build, [bool]$Installed, [string]$Scenario) {
    $registrations = @(Get-MsiProductRegistrations $Build.productCode $currentUserSid)
    $registrationChecks.Add([pscustomobject]@{
        scenario = $Scenario; productCode = $Build.productCode; expectedInstalled = $Installed
        registrations = $registrations
    })
    Assert-True (@($registrations | Where-Object { $_.context -ne 2 }).Count -eq 0) "$Scenario`: unexpected machine or managed-user registration."
    if ($Installed) {
        Assert-True ($registrations.Count -eq 1) "$Scenario`: expected exactly one current-user unmanaged registration."
        $registration = $registrations[0]
        Assert-True ($registration.productCode -eq $Build.productCode -and $registration.userSid -eq $currentUserSid) "$Scenario`: MSI product identity or user SID differs."
        Assert-True ($registration.state -eq '5') "$Scenario`: MSI product is not installed."
        Assert-True ($registration.version -eq $Build.packageVersion) "$Scenario`: MSI registered version differs."
    } else {
        Assert-True ($registrations.Count -eq 0) "$Scenario`: MSI product registration remains."
    }
}
function Assert-Installed($Build) {
    $marker = Get-Content (Join-Path $installPath 'installed-by-msi.json') -Raw | ConvertFrom-Json
    Assert-True ($marker.packageVersion -eq $Build.packageVersion) 'Installed marker version differs.'
    Assert-True ($marker.updateMode -eq 'msi' -and $marker.fixture) 'Installed mode/fixture marker is missing.'
    Assert-True (Test-Path (Join-Path $installPath "$($config.app).exe")) 'Installed application is missing.'
    Assert-True (Test-Path (Join-Path $installPath 'coreclr.dll')) 'Self-contained runtime is missing.'
    Assert-True (Test-Path $startLink) 'Start menu shortcut is missing.'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($startLink)
    Assert-True ($link.TargetPath -eq (Join-Path $installPath "$($config.app).exe")) 'Start menu target is wrong.'
    Assert-ProductRegistration $Build $true 'Installed product'
}
function Assert-Sentinel {
    Assert-True ((Get-Content $sentinel -Raw) -eq $sentinelContent) 'Unowned user data was changed or deleted.'
}

try {
    # Fail fast on COM/API binding problems before three expensive MSI builds.
    $preflight = [pscustomobject]@{ productCode = [guid]::NewGuid().ToString('B').ToUpperInvariant() }
    Assert-MsiProductIdentityApi $preflight.productCode $currentUserSid
    Assert-ProductRegistration $preflight $false 'MSI registration API preflight'
    Record-Pass 'MSI registration API preflight for current-user and machine contexts'

    $buildArgs = @{ FixtureId = $fixtureId; OutputDirectory = $OutputDirectory }
    if ($TargetFramework) { $buildArgs.TargetFramework = $TargetFramework }
    if ($RuntimeFrameworkVersion) { $buildArgs.RuntimeFrameworkVersion = $RuntimeFrameworkVersion }
    if ($WixBin) { $buildArgs.WixBin = $WixBin }
    if ($WixSourceArchive) { $buildArgs.WixSourceArchive = $WixSourceArchive }
    $v1 = & (Join-Path $PSScriptRoot 'Build-Installer.ps1') @buildArgs -PackageVersion '1.1.0'
    $packages.Add($v1)
    $buildArgs.WixSourceArchive = $v1.wixSourceArchive
    Assert-PackagePolicy $v1
    $installPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "Programs/$($v1.installFolder)"
    $startFolder = Join-Path ([Environment]::GetFolderPath('Programs')) $v1.productName
    $startLink = Join-Path $startFolder "$($v1.productName).lnk"
    $desktopLink = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) "$($v1.productName).lnk"
    $startupLink = Join-Path ([Environment]::GetFolderPath('Startup')) "$($v1.productName).lnk"
    $dataPath = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "$($config.app)-MsiDataFixture-$fixtureId"
    Assert-True (-not (Test-Path $installPath)) 'Fixture path already exists; refusing to touch it.'
    Assert-True (-not (Test-Path $dataPath)) 'Fixture data already exists; refusing to touch it.'
    [IO.Directory]::CreateDirectory($dataPath) | Out-Null
    $sentinel = Join-Path $dataPath 'user-data-sentinel.txt'
    $sentinelContent = "Must survive repair, upgrade, rollback and uninstall: $fixtureId"
    [IO.File]::WriteAllText($sentinel, $sentinelContent)

    $null = Invoke-Msi 'reject-allusers' @('/i', ('"' + $v1.msiPath + '"'), 'ALLUSERS=1') @(1603)
    Assert-True (-not (Test-Path $installPath)) 'ALLUSERS=1 rejection created an installation directory.'
    Assert-ProductRegistration $v1 $false 'ALLUSERS=1 rejection'
    Assert-Sentinel
    Record-Pass 'ALLUSERS=1 is rejected before installation'

    # Windows Installer ignores MSIINSTALLPERUSER unless ALLUSERS=2. Verify
    # the effective scope and fixed destination, not an invalid rejection premise.
    # https://learn.microsoft.com/en-us/windows/win32/msi/msiinstallperuser
    $null = Invoke-Msi 'msiinstallperuser-fixed-scope' @('/i', ('"' + $v1.msiPath + '"'), 'MSIINSTALLPERUSER=1')
    Assert-Installed $v1
    Assert-True (-not (Test-Path $desktopLink) -and -not (Test-Path $startupLink)) 'Scope-property probe enabled optional shortcuts.'
    Assert-Sentinel
    Record-Pass 'MSIINSTALLPERUSER=1 leaves fixed current-user scope, path and defaults intact'
    $null = Invoke-Msi 'cleanup-scope-probe' @('/x', $v1.productCode)
    Assert-ProductRegistration $v1 $false 'Scope probe uninstall'
    Assert-True (-not (Test-Path $installPath)) 'Scope-probe uninstall left its managed installation directory.'
    Assert-True (-not (Test-Path $startLink) -and -not (Test-Path $desktopLink) -and -not (Test-Path $startupLink)) 'Scope-probe uninstall left a shortcut.'
    Assert-Sentinel

    # The guard fixture is a tiny managed executable that exits by itself. It does not
    # launch the real app, open a profile, or need a forced stop even on test failure.
    $helper = Join-Path $OutputDirectory "process-fixture-$fixtureId"
    [IO.Directory]::CreateDirectory($helper) | Out-Null
    $helperProject = Join-Path $helper 'ProcessFixture.csproj'
    "<Project Sdk=`"Microsoft.NET.Sdk`"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>$($v1.targetFramework)</TargetFramework><AssemblyName>$($config.app)</AssemblyName><UseAppHost>true</UseAppHost></PropertyGroup></Project>" | Set-Content $helperProject
    'using System.Threading; internal static class Program { static void Main() { Thread.Sleep(45000); } }' | Set-Content (Join-Path $helper 'Program.cs')
    $helperOut = Join-Path $helper 'publish'
    $sdkContext = Join-Path (Split-Path $v1.msiPath -Parent) 'sdk-context'
    Push-Location $sdkContext
    try {
        & dotnet publish $helperProject -c Release -r win-x64 --self-contained true -o $helperOut "-p:RuntimeFrameworkVersion=$($v1.runtimeFrameworkVersion)" '-p:RestoreSources=https://api.nuget.org/v3/index.json' | Out-Host
        if ($LASTEXITCODE -ne 0) { throw 'Cannot build the isolated process-detection fixture.' }
    } finally { Pop-Location }
    $runningFixture = Start-Process (Join-Path $helperOut "$($config.app).exe") -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 500
    Assert-True (-not $runningFixture.HasExited) 'Process-detection fixture exited early.'
    $null = Invoke-Msi 'running-app-blocked' @('/i', ('"' + $v1.msiPath + '"')) @(1603)
    Assert-True (-not (Test-Path $installPath)) 'Running-app rejection partially installed the app.'
    Assert-True (-not $runningFixture.HasExited) 'Installer closed the running process.'
    Assert-True ((Get-Content (Join-Path $logs 'running-app-blocked.log') -Raw) -match 'Please quit') 'Expected running-app error was not recorded.'
    $runningFixture.WaitForExit()
    $runningFixture = $null
    Record-Pass 'Running app blocks setup without closing it'

    $null = Invoke-Msi 'install-v1' @('/i', ('"' + $v1.msiPath + '"'))
    Assert-Installed $v1
    Assert-True (-not (Test-Path $desktopLink) -and -not (Test-Path $startupLink)) 'Optional shortcuts were enabled without consent.'
    Assert-Sentinel
    Record-Pass 'Clean per-user install; bundled runtime; Start menu; no desktop/startup default'

    # Unowned content INSIDE the installation directory must also survive uninstall.
    $unownedFile = Join-Path $installPath 'user-created-not-managed.txt'
    [IO.File]::WriteAllText($unownedFile, $sentinelContent)
    $payloadFile = Join-Path $installPath "$($config.app).dll"
    $expectedHash = (Get-FileHash $payloadFile -Algorithm SHA256).Hash
    Remove-Item -LiteralPath $payloadFile
    $null = Invoke-Msi 'repair-v1' @('/fa', ('"' + $v1.msiPath + '"'))
    Assert-True ((Get-FileHash $payloadFile -Algorithm SHA256).Hash -eq $expectedHash) 'Repair did not restore the deleted payload file.'
    Assert-Installed $v1
    Assert-Sentinel
    Record-Pass 'Same-version repair restores deleted payload'

    $null = Invoke-Msi 'opt-in-shortcuts' @('/i', ('"' + $v1.msiPath + '"'), 'ADDLOCAL=Core,DesktopShortcutFeature,StartupShortcutFeature')
    Assert-True ((Test-Path $desktopLink) -and (Test-Path $startupLink)) 'Explicit shortcut opt-in failed.'
    Record-Pass 'Explicit desktop/startup opt-in'

    # Build newer fixtures only after version 1 installation/repair is proven.
    $v2 = & (Join-Path $PSScriptRoot 'Build-Installer.ps1') @buildArgs -PackageVersion '1.1.1' -FixturePayloadDirectory $v1.payloadDirectory
    $packages.Add($v2)
    $v3 = & (Join-Path $PSScriptRoot 'Build-Installer.ps1') @buildArgs -PackageVersion '1.1.2' -FixturePayloadDirectory $v1.payloadDirectory
    $packages.Add($v3)
    Assert-PackagePolicy $v2
    Assert-PackagePolicy $v3

    $runningFixture = Start-Process (Join-Path $helperOut "$($config.app).exe") -PassThru -WindowStyle Hidden
    Start-Sleep -Milliseconds 500
    Assert-True (-not $runningFixture.HasExited) 'Process fixture exited before maintenance checks.'
    $null = Invoke-Msi 'running-repair-blocked' @('/fa', ('"' + $v1.msiPath + '"')) @(1603)
    $null = Invoke-Msi 'running-upgrade-blocked' @('/i', ('"' + $v2.msiPath + '"')) @(1603)
    $null = Invoke-Msi 'running-uninstall-blocked' @('/x', $v1.productCode) @(1603)
    Assert-Installed $v1
    Assert-True (-not $runningFixture.HasExited) 'Maintenance terminated the running process.'
    Assert-Sentinel
    $runningFixture.WaitForExit()
    $runningFixture = $null
    Record-Pass 'Repair, upgrade and uninstall refuse a running app before mutation'

    $null = Invoke-Msi 'upgrade-v2' @('/i', ('"' + $v2.msiPath + '"'))
    Assert-Installed $v2
    Assert-ProductRegistration $v1 $false 'Old product after major upgrade'
    Assert-True ((Test-Path $desktopLink) -and (Test-Path $startupLink)) 'Upgrade did not preserve the opted-in features.'
    Assert-Sentinel
    Record-Pass 'Major upgrade and opted-in feature migration'

    $null = Invoke-Msi 'reject-downgrade' @('/i', ('"' + $v1.msiPath + '"')) @(1603)
    Assert-Installed $v2
    Assert-Sentinel
    Record-Pass 'Older MSI rejected without changing installed version'

    $null = Invoke-Msi 'rollback-failed-upgrade' @('/i', ('"' + $v3.msiPath + '"'), 'WIXFAILWHENDEFERRED=1') @(1603)
    Assert-Installed $v2
    Assert-True ((Get-FileHash $payloadFile -Algorithm SHA256).Hash -eq $expectedHash) 'Rollback did not restore the original payload.'
    Assert-ProductRegistration $v3 $false 'Failed-upgrade product'
    Assert-True ((Test-Path $desktopLink) -and (Test-Path $startupLink)) 'Rollback lost the previous shortcuts.'
    Assert-Sentinel
    Assert-True ((Get-Content $unownedFile -Raw) -eq $sentinelContent) 'Rollback changed unowned data in the install folder.'
    Record-Pass 'Deferred-failure upgrade rolls back old version, registration, shortcuts and data'

    $null = Invoke-Msi 'uninstall-v2' @('/x', $v2.productCode)
    Assert-True (-not (Test-Path (Join-Path $installPath "$($config.app).exe"))) 'Uninstall left the application behind.'
    Assert-True (-not (Test-Path $startLink) -and -not (Test-Path $desktopLink) -and -not (Test-Path $startupLink)) 'Uninstall left managed shortcuts behind.'
    Assert-ProductRegistration $v2 $false 'Uninstalled product'
    Assert-Sentinel
    Assert-True ((Get-Content $unownedFile -Raw) -eq $sentinelContent) 'Uninstall removed unowned content.'
    Record-Pass 'Uninstall preserves external and unowned in-folder data'
    $success = $true
} catch {
    $results.Add([pscustomobject]@{ name = 'Failure'; result = 'failed'; error = $_.Exception.Message })
    throw
} finally {
    # Never kill either the real app or the timed fixture process.
    if ($null -ne $runningFixture) { $runningFixture.WaitForExit() }
    foreach ($p in $packages) {
        try { $null = Invoke-Msi "cleanup-$($p.packageVersion)" @('/x', $p.productCode) @(0,1605) }
        catch { Write-Warning $_.Exception.Message }
    }
    [ordered]@{ success = $success; fixtureId = $fixtureId; logs = $logs; checks = $results; testedAtUtc = [DateTime]::UtcNow.ToString('o')
        registrationChecks = $registrationChecks
        testUserIsAdministrator = $isAdministrator
        standardUserInstallEstablished = ($success -and -not $isAdministrator)
        privilegeLimitation = $(if ($isAdministrator) { 'Executed with an administrator token. Limited MSI metadata was checked, but actual standard-user installation is not established.' } else { 'Executed without an administrator token; interactive UI acceptance remains separate.' }) } |
        ConvertTo-Json -Depth 7 | Set-Content (Join-Path $OutputDirectory "test-results-$fixtureId.json")
    # Preserve all logs and data sentinels for inspection. The disposable VM/runner
    # owns their eventual removal; never recursively delete AppData in a test.
}
