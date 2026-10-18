#Requires -Version 7.2
<#
Run only after the real v1.1.2 Release assets are public, before changing main/version.json.
The public v1.0.1 EXE is DATA ONLY: this test never starts it. It exercises the exact
legacy argument shape except that --restart is intentionally omitted, preventing
failure recovery from starting the legacy WPF app (which ignores DINDEX_DATA_DIR).
No DNS/hosts overrides, tools installation, MSI installation, or feed changes occur.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExpectedManifestPath,
    [string]$OutputDirectory,
    [switch]$SmokeTestNewApp
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This post-publication test requires Windows and preinstalled PowerShell 7.2 or later.' }

$releaseBase = 'https://github.com/184649/DIndex/releases/download/v1.1.2'
$appUrl = "$releaseBase/DIndex.zip"
$updaterUrl = "$releaseBase/DIndexUpdater.zip"
$manifestFile = Get-Item -LiteralPath $ExpectedManifestPath
if ($manifestFile.PSIsContainer -or $manifestFile.Length -le 0 -or $manifestFile.Length -gt 131072) { throw 'Expected release manifest is missing, empty, or too large.' }
$manifest = [IO.File]::ReadAllText($manifestFile.FullName) | ConvertFrom-Json
if ($manifest.version -cne '1.1.2' -or $manifest.downloadUrl -cne $appUrl -or $manifest.updaterUrl -cne $updaterUrl) {
    throw 'Expected manifest must pin stable 1.1.2 and the exact 184649/DIndex v1.1.2 release ZIP URLs.'
}
$AppZipSha256 = [string]$manifest.downloadSha256
$UpdaterZipSha256 = [string]$manifest.updaterSha256
if ($AppZipSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or $UpdaterZipSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'Expected manifest must contain both SHA256 values.' }
$legacyUrl = 'https://raw.githubusercontent.com/184649/DIndex/3a90f4cb5a0dd3361fc55865feedb804f70e14f3/DIndex.zip'
$legacyZipHash = 'f25238779d8b0f102a671551230a2c0efa758880ef3ac71c8e81fff01da5d14c'
$legacyExeHash = '926a0c7ca53088c65f6269d39b19154ed6a91cb98fe21c988d0310fcf932fdc8'
$expectedVersion = '1.1.2.0'
$maximumPackageBytes = 512L * 1024 * 1024
$maximumExecutableBytes = 1024L * 1024 * 1024
if (-not $OutputDirectory) { $OutputDirectory = [IO.Path]::GetTempPath() }
$parent = [IO.Path]::GetFullPath($OutputDirectory)
if (-not [IO.Directory]::Exists($parent)) { throw 'OutputDirectory must already exist.' }
$work = Join-Path $parent ('DIndex-PublishedLegacy-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $work) { throw 'The fresh output directory unexpectedly exists.' }
[IO.Directory]::CreateDirectory($work) | Out-Null
$profile = Join-Path $work 'synthetic-profile'
$install = Join-Path $work 'synthetic-install'
$expected = Join-Path $work 'expected-release'
$updaterDirectory = Join-Path $work 'updater'
foreach ($path in @($profile, $install, $expected, $updaterDirectory)) { [IO.Directory]::CreateDirectory($path) | Out-Null }
$settings = Join-Path $profile 'settings.json'
$sentinel = Join-Path $profile 'user-data-sentinel.txt'
$cacheSentinel = Join-Path $profile 'index-cache.tsv'
[IO.File]::WriteAllText($settings, '{"SearchRoots":[],"SearchPath":true,"IncludeDirectories":true,"ResultLimit":500,"ExcludeFolderNames":[]}', [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($sentinel, 'Retain this synthetic user data: ' + [guid]::NewGuid().ToString('N'))
[IO.File]::WriteAllText($cacheSentinel, 'Synthetic legacy cache sentinel; never scan a real profile.')
$settingsHash = (Get-FileHash -LiteralPath $settings -Algorithm SHA256).Hash
$sentinelHash = (Get-FileHash -LiteralPath $sentinel -Algorithm SHA256).Hash
$cacheHash = (Get-FileHash -LiteralPath $cacheSentinel -Algorithm SHA256).Hash
$result = [ordered]@{
    schemaVersion = 1; release = 'v1.1.2'; success = $false; outputDirectory = $work
    legacyCommit = '3a90f4cb5a0dd3361fc55865feedb804f70e14f3'
    legacyAppExecuted = $false; legacyArgumentDifference = '--restart omitted deliberately'
    profile = $profile; appUrl = $appUrl; updaterUrl = $updaterUrl
    expectedManifestSha256 = (Get-FileHash -LiteralPath $manifestFile.FullName -Algorithm SHA256).Hash
    appZipSha256 = $AppZipSha256; updaterZipSha256 = $UpdaterZipSha256
    restartSmokeRequested = [bool]$SmokeTestNewApp; restartSmokePassed = $false
    limitations = @('Does not execute the legacy WPF app or its update UI.', 'Does not test legacy caller exit races or automatic updater restart.', 'Uses only a synthetic profile and an already-exited owned process ID.')
    checks = [Collections.Generic.List[string]]::new()
    cleanupErrors = [Collections.Generic.List[string]]::new()
}
$processes = [Collections.Generic.List[object]]::new()
$stdout = $null
$stderr = $null
$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$http = [Net.Http.HttpClient]::new($handler)
$http.Timeout = [TimeSpan]::FromMinutes(5)

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}
function Assert-Hash([string]$Path, [string]$ExpectedHash) {
    Assert-True (Test-Path -LiteralPath $Path -PathType Leaf) "Missing expected file: $Path"
    $actual = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
    Assert-True ($actual -eq $ExpectedHash) "SHA256 mismatch: $Path"
}
function Download-Verified([string]$Url, [string]$Destination, [string]$Hash) {
    $uri = [uri]$Url
    $response = $null
    try {
        for ($redirects = 0; $redirects -le 5; $redirects++) {
            $response = $http.GetAsync($uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
            $status = [int]$response.StatusCode
            if ($status -notin @(301,302,303,307,308)) { break }
            $location = $response.Headers.Location
            if (-not $location) { throw 'Download redirect has no Location.' }
            $next = if ($location.IsAbsoluteUri) { $location } else { [uri]::new($uri, $location) }
            if ($next.Scheme -ne 'https' -or -not $next.IsDefaultPort -or $next.UserInfo -or $next.Fragment -or
                $next.Host -notin @('release-assets.githubusercontent.com','objects.githubusercontent.com')) {
                throw "Unapproved download redirect: $($next.Host)"
            }
            $response.Dispose(); $response = $null; $uri = $next
        }
        if (-not $response -or [int]$response.StatusCode -in @(301,302,303,307,308)) { throw 'Too many download redirects.' }
        $response.EnsureSuccessStatusCode() | Out-Null
        $length = $response.Content.Headers.ContentLength
        if ($null -ne $length -and ($length -le 0 -or $length -gt $maximumPackageBytes)) { throw 'Invalid download size.' }
        $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
        $output = [IO.FileStream]::new($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(5))
        try {
            $buffer = [byte[]]::new(131072)
            [long]$total = 0
            while (($read = $input.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                $total += $read
                if ($total -gt $maximumPackageBytes) { throw 'Download exceeds the size limit.' }
                $output.Write($buffer, 0, $read)
            }
            if ($total -eq 0) { throw 'Download is empty.' }
            $output.Flush($true)
        } finally { $deadline.Dispose(); $output.Dispose(); $input.Dispose() }
    } finally { if ($response) { $response.Dispose() } }
    Assert-Hash $Destination $Hash
}
function Extract-SingleExecutable([string]$Zip, [string]$Directory, [string]$Name) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        Assert-True ($archive.Entries.Count -eq 1) 'Expected exactly one root executable in the ZIP.'
        $entry = $archive.Entries[0]
        Assert-True ($entry.FullName -ceq $Name) "Unexpected ZIP entry: $($entry.FullName)"
        Assert-True ($entry.Length -gt 0 -and $entry.Length -le $maximumExecutableBytes) 'Invalid executable size.'
        $destination = Join-Path $Directory $Name
        $input = $entry.Open()
        $output = [IO.FileStream]::new($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $input.CopyTo($output); $output.Flush($true) } finally { $output.Dispose(); $input.Dispose() }
        return $destination
    } finally { $archive.Dispose() }
}
function Get-NumericFileVersion([string]$Path) {
    $v = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
    return "$($v.FileMajorPart).$($v.FileMinorPart).$($v.FileBuildPart).$($v.FilePrivatePart)"
}
function Start-OwnedProcess([Diagnostics.ProcessStartInfo]$Start) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $Start
    if (-not $process.Start()) { $process.Dispose(); throw 'Could not start an isolated test process.' }
    # Register ownership before any optional metadata query. A cmd /c exit child
    # can exit between Start(), HasExited, and StartTime; no timestamp is needed
    # for a process that is already exited and will be skipped during cleanup.
    $owned = [pscustomobject]@{ process = $process; path = [IO.Path]::GetFullPath($Start.FileName); id = $process.Id; startTime = $null }
    $processes.Add($owned)
    if (-not $process.HasExited) {
        try { $owned.startTime = $process.StartTime }
        catch { if (-not $process.HasExited) { throw } }
    }
    return $process
}
function Assert-SyntheticDataUnchanged {
    Assert-Hash $settings $settingsHash
    Assert-Hash $sentinel $sentinelHash
    Assert-Hash $cacheSentinel $cacheHash
}

try {
    $oldZip = Join-Path $work 'legacy-DIndex.zip'
    $appZip = Join-Path $work 'release-DIndex.zip'
    $updaterZip = Join-Path $work 'release-DIndexUpdater.zip'
    Download-Verified $legacyUrl $oldZip $legacyZipHash
    Download-Verified $appUrl $appZip $AppZipSha256
    Download-Verified $updaterUrl $updaterZip $UpdaterZipSha256
    $result.checks.Add('Actual public immutable URLs downloaded and all three ZIP hashes matched.')
    $target = Extract-SingleExecutable $oldZip $install 'DIndex.exe'
    $expectedExe = Extract-SingleExecutable $appZip $expected 'DIndex.exe'
    $updater = Extract-SingleExecutable $updaterZip $updaterDirectory 'DIndexUpdater.exe'
    Assert-Hash $target $legacyExeHash
    Assert-True ((Get-NumericFileVersion $target) -eq '1.0.1.0') 'The replacement target is not the public v1.0.1 executable.'
    Assert-True ((Get-NumericFileVersion $expectedExe) -eq $expectedVersion) 'Published application file version is not 1.1.2.0.'
    $productVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($expectedExe).ProductVersion
    Assert-True ($productVersion -match '^1\.1\.2(?:\+.*)?$') "Published application is not a stable 1.1.2 build: $productVersion"
    $expectedExeHash = (Get-FileHash -LiteralPath $expectedExe -Algorithm SHA256).Hash
    $result.expectedExeSha256 = $expectedExeHash
    $result.checks.Add('Old and new application identity checked statically; old EXE was never executed.')

    # This owned child is harmless and fully exited before the updater gets its PID.
    # If Windows reuses that PID, updater identity checks must fail safely.
    $exitStart = [Diagnostics.ProcessStartInfo]::new()
    $exitStart.FileName = [IO.Path]::Combine([Environment]::GetFolderPath('System'), 'cmd.exe')
    $exitStart.UseShellExecute = $false; $exitStart.CreateNoWindow = $true
    foreach ($argument in @('/d','/c','exit','0')) { $exitStart.ArgumentList.Add($argument) }
    $exited = Start-OwnedProcess $exitStart
    Assert-True ($exited.WaitForExit(10000)) 'Owned harmless child did not exit.'
    Assert-True ($exited.ExitCode -eq 0) 'Owned harmless child failed.'
    $legacyPid = $exited.Id

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $updater; $start.WorkingDirectory = $updaterDirectory
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Environment['DINDEX_DATA_DIR'] = $profile
    # Never add --restart: failed recovery must not execute the old WPF binary.
    foreach ($argument in @('--processId', "$legacyPid", '--appPath', $target, '--appZip', $appUrl)) { $start.ArgumentList.Add($argument) }
    $running = Start-OwnedProcess $start
    $stdout = $running.StandardOutput.ReadToEndAsync()
    $stderr = $running.StandardError.ReadToEndAsync()
    Assert-True ($running.WaitForExit(360000)) 'Published updater exceeded the six-minute test timeout.'
    [IO.File]::WriteAllText((Join-Path $work 'updater.stdout.log'), $stdout.GetAwaiter().GetResult())
    [IO.File]::WriteAllText((Join-Path $work 'updater.stderr.log'), $stderr.GetAwaiter().GetResult())
    $result.updaterExitCode = $running.ExitCode
    Assert-True ($running.ExitCode -eq 0) "Published updater failed with exit code $($running.ExitCode). See updater.stderr.log."
    Assert-Hash $target $expectedExeHash
    Assert-True ((Get-NumericFileVersion $target) -eq $expectedVersion) 'Installed file version differs from 1.1.2.0.'
    Assert-Hash ($target + '.bak') $legacyExeHash
    Assert-SyntheticDataUnchanged
    $result.checks.Add('Legacy no-hash CLI downloaded the public package, replaced v1.0.1 with exact v1.1.2 bytes, retained original .bak, and left synthetic settings/cache/data unchanged.')

    if ($SmokeTestNewApp) {
        # A separate explicit launch, only after verifying the target is the new binary.
        # Use another profile: the upgrade-preservation sentinel remains untouched.
        $smokeProfile = Join-Path $work 'new-app-smoke-profile'
        [IO.Directory]::CreateDirectory($smokeProfile) | Out-Null
        $smokeSettings = Join-Path $smokeProfile 'settings.json'
        [IO.File]::Copy($settings, $smokeSettings, $false)
        $smokeStart = [Diagnostics.ProcessStartInfo]::new()
        $smokeStart.FileName = $target; $smokeStart.WorkingDirectory = $install
        $smokeStart.UseShellExecute = $false
        $smokeStart.Environment['DINDEX_DATA_DIR'] = $smokeProfile
        $smoke = Start-OwnedProcess $smokeStart
        Assert-True (-not $smoke.WaitForExit(5000)) 'The new application exited during the five-second startup observation.'
        Assert-True ([IO.Path]::GetFullPath($smoke.MainModule.FileName) -eq [IO.Path]::GetFullPath($target)) 'Smoke process path differs from the verified new app.'
        Assert-Hash $smokeSettings $settingsHash
        Assert-True (Test-Path -LiteralPath (Join-Path $smokeProfile '.instance.lock')) 'New application did not establish the isolated profile lock.'
        $result.restartSmokePassed = $true
        $result.checks.Add('Separately launched verified new app stayed alive for five seconds with an isolated empty-root profile; this was not updater-controlled restart.')
        $result.limitations += 'A live process and profile lock do not prove interactive UI behavior or full indexing correctness.'
    }
    Assert-SyntheticDataUnchanged
    $result.success = $true
} catch {
    $result.error = $_.Exception.ToString()
    throw
} finally {
    # Only stop exact process objects started by this test, after path/start-time checks.
    # No process-name sweeps and no recursive deletion of output or profile directories.
    foreach ($owned in $processes) {
        try {
            $process = $owned.process
            if (-not $process.HasExited) {
                $actualPath = [IO.Path]::GetFullPath($process.MainModule.FileName)
                if ($process.Id -ne $owned.id -or $process.StartTime -ne $owned.startTime -or $actualPath -ne $owned.path) {
                    throw 'Refusing cleanup because the tracked process identity changed.'
                }
                $process.Kill()
                if (-not $process.WaitForExit(10000)) { throw 'Owned test process did not exit during cleanup.' }
            }
        } catch {
            # A normal exit racing the identity checks needs no cleanup.
            if (-not $owned.process.HasExited) { $result.cleanupErrors.Add($_.Exception.Message); $result.success = $false }
        }
        finally { if ($owned.process) { $owned.process.Dispose() } }
    }
    $http.Dispose(); $handler.Dispose()
    # Capture output after terminating an owned timed-out updater too. Never
    # wait indefinitely for a pipe when process cleanup itself failed.
    foreach ($capture in @(
        [pscustomobject]@{ task = $stdout; name = 'updater.stdout.log' },
        [pscustomobject]@{ task = $stderr; name = 'updater.stderr.log' }
    )) {
        if ($null -ne $capture.task) {
            try {
                if ($capture.task.IsCompleted -or $capture.task.Wait(1000)) {
                    [IO.File]::WriteAllText((Join-Path $work $capture.name), $capture.task.GetAwaiter().GetResult())
                }
            } catch { $result.cleanupErrors.Add("Cannot capture $($capture.name): $($_.Exception.Message)"); $result.success = $false }
        }
    }
    $resultJson = $result | ConvertTo-Json -Depth 6
    [IO.File]::WriteAllText((Join-Path $work 'test-results.json'), $resultJson, [Text.UTF8Encoding]::new($false))
    Write-Host "Evidence retained: $work"
    if (-not $result.success) {
        Write-Host '--- Published legacy test failure result ---'
        Write-Host $resultJson
        foreach ($logName in @('updater.stdout.log','updater.stderr.log')) {
            $log = Join-Path $work $logName
            if (Test-Path -LiteralPath $log -PathType Leaf) {
                Write-Host "--- $logName (last 120 lines) ---"
                Get-Content -LiteralPath $log -Tail 120 | ForEach-Object { Write-Host $_ }
            }
        }
        $updaterLog = Join-Path $profile 'updater.log'
        if (Test-Path -LiteralPath $updaterLog -PathType Leaf) {
            Write-Host '--- synthetic-profile/updater.log (last 120 lines) ---'
            Get-Content -LiteralPath $updaterLog -Tail 120 | ForEach-Object { Write-Host $_ }
        }
    }
}
if (-not $result.success) { throw 'Post-publication legacy test did not complete cleanly. See test-results.json.' }
Write-Host 'PASS: public v1.0.1 replacement target upgraded through the legacy CLI to verified v1.1.2; old WPF was never started.'
