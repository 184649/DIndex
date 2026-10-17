#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$PackageVersion = '1.1.1',
    [ValidatePattern('^net(8|10)\.0-windows(?:\.[0-9.]+)?$')][string]$TargetFramework,
    [ValidatePattern('^(8|10)\.0\.\d+$')][string]$RuntimeFrameworkVersion,
    [string]$OutputDirectory,
    [string]$WixBin,
    [string]$WixSourceArchive,
    # Fixture IDs produce a different UpgradeCode, registry key and installation path.
    [ValidatePattern('^[a-f0-9]{32}$')][string]$FixtureId,
    # Reuse is allowed only for synthetic MSI-version tests, never for distributable builds.
    [string]$FixturePayloadDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Build this installer on Windows with a preinstalled .NET SDK and WiX 3.14.' }
$repo = Split-Path $PSScriptRoot -Parent
$config = Get-Content (Join-Path $PSScriptRoot 'Product.json') -Raw | ConvertFrom-Json
$project = Join-Path $repo $config.project
if (-not $TargetFramework) {
    [xml]$projectXml = Get-Content $project -Raw
    $TargetFramework = [string]($projectXml.Project.PropertyGroup.TargetFramework | Where-Object { $_ } | Select-Object -First 1)
}
if ($TargetFramework -notmatch '^net(8|10)\.0-windows(?:\.[0-9.]+)?$') { throw "Unsupported target framework: $TargetFramework" }
$sdkMajor = [int]$Matches[1]
if (-not $RuntimeFrameworkVersion) { $RuntimeFrameworkVersion = if ($sdkMajor -eq 10) { '10.0.13' } else { '8.0.32' } }
if ($RuntimeFrameworkVersion -and [int]($RuntimeFrameworkVersion.Split('.')[0]) -ne $sdkMajor) { throw 'RuntimeFrameworkVersion must match the target framework major version.' }
$versionParts = $PackageVersion.Split('.') | ForEach-Object { [int]$_ }
if ($versionParts[0] -gt 255 -or $versionParts[1] -gt 255 -or $versionParts[2] -gt 65535) { throw 'MSI version must be major <=255, minor <=255, build <=65535.' }
if ($FixturePayloadDirectory -and -not $FixtureId) { throw 'Payload reuse requires an isolated FixtureId.' }

function Get-HexHash([string]$Value) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Value))).ToLowerInvariant()
}
function Get-StableGuid([string]$Value) {
    $h = Get-HexHash $Value
    ([guid]($h.Substring(0,8)+'-'+$h.Substring(8,4)+'-4'+$h.Substring(13,3)+'-a'+$h.Substring(17,3)+'-'+$h.Substring(20,12))).ToString().ToUpperInvariant()
}
function Escape-Xml([string]$Value) { [Security.SecurityElement]::Escape($Value) }
function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "$Executable failed with exit code $LASTEXITCODE" }
}
. (Join-Path $PSScriptRoot 'Find-WixToolset.ps1')
$WixBin = Find-PreinstalledWix -WixBin $WixBin
if (-not $WixBin) {
    throw 'Preinstalled official WiX 3.14.1.8722 candle.exe, light.exe and UI/Util extensions are required. See the component metadata diagnostics above. This script never installs tools.'
}
$wixComponents = @(
    foreach ($name in @('candle.exe', 'light.exe', 'WixUIExtension.dll', 'WixUtilExtension.dll')) {
        $path = Join-Path $WixBin $name
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($path)
        [pscustomobject]@{
            name = $name; path = $path; fileVersion = $version.FileVersion; productVersion = $version.ProductVersion
            numericFileVersion = "$($version.FileMajorPart).$($version.FileMinorPart).$($version.FileBuildPart).$($version.FilePrivatePart)"
        }
    }
)
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$sdkLines = & $dotnet --list-sdks
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the preinstalled .NET SDKs.' }
$sdkVersions = @($sdkLines | ForEach-Object { if ($_ -match '^(\d+\.\d+\.\d+)\s') { [version]$Matches[1] } } | Where-Object { $_.Major -eq $sdkMajor } | Sort-Object -Descending)
if ($sdkVersions.Count -eq 0) { throw "A preinstalled .NET $sdkMajor SDK is required; no SDK will be downloaded." }
$sdkVersion = $sdkVersions[0].ToString()
$identity = $config.installFolder
$upgradeCode = $config.upgradeCode
$productName = $config.productName
$registryKey = $config.registryKey
if ($FixtureId) {
    $identity = "$($config.app)-MsiFixture-$FixtureId"
    $upgradeCode = Get-StableGuid "$($config.upgradeCode)/fixture/$FixtureId"
    $productName = "$($config.app) MSI Fixture $FixtureId"
    $registryKey = "Software\$($config.app)\MsiFixture\$FixtureId"
}
$productCode = Get-StableGuid "$upgradeCode/product/$PackageVersion"
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $PSScriptRoot "out/$identity/$PackageVersion" }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
# Do not recursively delete a caller-selected directory. Every build gets a fresh child.
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$work = Join-Path $OutputDirectory ('build-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $work 'payload'
[IO.Directory]::CreateDirectory($payload) | Out-Null
$sdkContext = Join-Path $work 'sdk-context'
[IO.Directory]::CreateDirectory($sdkContext) | Out-Null
# SDK resolution uses the current working directory, avoiding an older repository global.json.
@{ sdk = @{ version = $sdkVersion; rollForward = 'disable'; allowPrerelease = $false } } | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $sdkContext 'global.json') -Encoding utf8

# Supply corresponding, unmodified source for the WiX binaries/dialogs embedded
# in this MSI. The archive is documentation only: never installed or executed.
$sourceCommit = 'b40e9a32c24033e11b77baf2c91a704382f898ed'
$sourceUrl = "https://github.com/wixtoolset/wix3/archive/$sourceCommit.zip"
$thirdParty = Join-Path $work 'third-party'
[IO.Directory]::CreateDirectory($thirdParty) | Out-Null
$sourceArchive = Join-Path $thirdParty "wix3-$sourceCommit.zip"
if ($WixSourceArchive) {
    Copy-Item -LiteralPath (Resolve-Path $WixSourceArchive).Path -Destination $sourceArchive
} else {
    Invoke-WebRequest -Uri $sourceUrl -OutFile $sourceArchive
}
$archive = [IO.Compression.ZipFile]::OpenRead($sourceArchive)
try {
    $sourceRoot = "wix3-$sourceCommit/"
    $licenseEntry = $archive.GetEntry($sourceRoot + 'LICENSE.TXT')
    if ($null -eq $licenseEntry) { throw 'WiX source archive does not match the pinned 3.14.1 source commit.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($licenseEntry, (Join-Path $thirdParty 'WiX-LICENSE.TXT'), $false)
} finally { $archive.Dispose() }
$wixNotice = "This MSI embeds unmodified WiX Toolset 3.14.1 code and dialogs. Copyright (c) .NET Foundation and contributors. Licensed under the Microsoft Reciprocal License (MS-RL). Corresponding source commit: $sourceCommit. Source: $sourceUrl. Distribute this third-party directory together with the MSI. No extra end-user license acceptance is requested by setup."
$wixNotice | Set-Content (Join-Path $thirdParty 'NOTICE.txt') -Encoding utf8

if ($FixturePayloadDirectory) {
    $reuse = (Resolve-Path $FixturePayloadDirectory).Path
    Copy-Item (Join-Path $reuse '*') $payload -Recurse
} else {
    Push-Location $sdkContext
    try {
        $activeSdk = (& $dotnet --version).Trim()
        if ($LASTEXITCODE -ne 0 -or $activeSdk -ne $sdkVersion) { throw "Expected preinstalled SDK $sdkVersion, selected $activeSdk" }
        # A normal NuGet restore obtains official Microsoft runtime dependencies only.
        # There is no SDK bootstrap, tool install, single-file bundling or trimming.
        $publishArgs = @('publish', $project, '--configuration', 'Release', '--runtime', 'win-x64',
            '--self-contained', 'true', '--framework', $TargetFramework, '--output', $payload,
            "-p:TargetFramework=$TargetFramework", "-p:Version=$PackageVersion-preview.1",
            "-p:AssemblyVersion=$PackageVersion.0", "-p:FileVersion=$PackageVersion.0",
            '-p:PublishSingleFile=false', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false',
            '-p:IncludeNativeLibrariesForSelfExtract=false', '-p:UseAppHost=true', '-p:DebugType=None', '-p:DebugSymbols=false',
            '-p:RestoreSources=https://api.nuget.org/v3/index.json')
        if ($RuntimeFrameworkVersion) { $publishArgs += "-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion" }
        Invoke-Checked $dotnet $publishArgs
    } finally { Pop-Location }
}
$appExe = "$($config.app).exe"
foreach ($required in @($appExe, "$($config.app).dll", "$($config.app).deps.json", "$($config.app).runtimeconfig.json", 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'PresentationFramework.dll', 'System.Private.CoreLib.dll')) {
    if (-not (Test-Path (Join-Path $payload $required) -PathType Leaf)) { throw "Self-contained publish is incomplete: $required is missing." }
}
$runtime = Get-Content (Join-Path $payload "$($config.app).runtimeconfig.json") -Raw | ConvertFrom-Json
if ($runtime.runtimeOptions.PSObject.Properties.Name -contains 'framework' -or $runtime.runtimeOptions.PSObject.Properties.Name -contains 'frameworks') {
    throw 'The application still depends on a machine-wide .NET runtime.'
}
$licenseRoot = Join-Path $payload 'licenses'
[IO.Directory]::CreateDirectory($licenseRoot) | Out-Null
Copy-Item (Join-Path $thirdParty 'WiX-LICENSE.TXT') (Join-Path $licenseRoot 'WiX-LICENSE.TXT')
Copy-Item (Join-Path $thirdParty 'NOTICE.txt') (Join-Path $licenseRoot 'WiX-NOTICE.txt')
$cacheOutput = & $dotnet nuget locals global-packages --list --force-english-output
if ($LASTEXITCODE -ne 0 -or ($cacheOutput -join '') -notmatch 'global-packages:\s*(.+)') { throw 'Cannot find the restored official runtime packages for their licenses.' }
$packageCache = $Matches[1].Trim()
if (-not ($runtime.runtimeOptions.PSObject.Properties.Name -contains 'includedFrameworks')) { throw 'Self-contained includedFrameworks metadata is missing.' }
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    if ($framework.name -notin @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App')) { throw "Unexpected framework: $($framework.name)" }
    if ($framework.version -ne $RuntimeFrameworkVersion) { throw "Expected runtime $RuntimeFrameworkVersion, found $($framework.name)/$($framework.version)." }
    $packageName = "$($framework.name).Runtime.win-x64"
    $packRoots = @((Join-Path $packageCache "$($packageName.ToLowerInvariant())/$($framework.version)"), (Join-Path (Split-Path $dotnet -Parent) "packs/$packageName/$($framework.version)"))
    $pack = $packRoots | Where-Object { Test-Path $_ -PathType Container } | Select-Object -First 1
    if (-not $pack) { throw "Restored runtime pack $packageName/$($framework.version) is missing; cannot include its license notices." }
    $notices = @(Get-ChildItem $pack -File | Where-Object { $_.Name -match '^(LICENSE|THIRD[-_ ]PARTY[-_ ]NOTICES|NOTICE)(\..*)?$' })
    if (-not ($notices | Where-Object Name -Match '^LICENSE')) { throw "License missing in $pack" }
    if (-not ($notices | Where-Object Name -Match '^(THIRD|NOTICE)')) {
        if ($framework.name -eq 'Microsoft.NETCore.App') { throw "Required .NET runtime third-party notices missing in $pack" }
        # Some official WindowsDesktop runtime packs ship only LICENSE. Preserve all
        # notices present in that package and explicitly record the absence.
        Write-Warning "$packageName/$($framework.version) contains no separate third-party notices file; its LICENSE and the Core runtime notices are included."
    }
    $noticeDestination = Join-Path $licenseRoot "$packageName-$($framework.version)"
    [IO.Directory]::CreateDirectory($noticeDestination) | Out-Null
    foreach ($notice in $notices) { Copy-Item $notice.FullName (Join-Path $noticeDestination $notice.Name) -Force }
}
$marker = [ordered]@{ schemaVersion = 1; app = $config.app; channel = 'preview'; updateMode = 'msi'; packageVersion = $PackageVersion; upgradeCode = $upgradeCode; productCode = $productCode; fixture = [bool]$FixtureId }
$marker | ConvertTo-Json | Set-Content (Join-Path $payload 'installed-by-msi.json') -Encoding utf8

# Deterministic, per-file components preserve component identity across major upgrades.
# HKCU key paths satisfy per-user MSI ICE38; full repair uses /fa to restore every payload file.
$files = @(Get-ChildItem $payload -File -Recurse | Sort-Object FullName)
if ($files.Count -eq 0) { throw 'Refusing to create an empty installer.' }
$ns = 'http://schemas.microsoft.com/wix/2006/wi'
$lines = [Collections.Generic.List[string]]::new()
$refs = [Collections.Generic.List[string]]::new()
$lines.Add('<?xml version="1.0" encoding="utf-8"?>')
$lines.Add("<Wix xmlns=`"$ns`"><Fragment>")
$directoryIds = @{ '' = 'AppInstallFolder' }
$directories = @(Get-ChildItem $payload -Directory -Recurse | Sort-Object { $_.FullName.Length }, FullName)
foreach ($dir in $directories) {
    $rel = [IO.Path]::GetRelativePath($payload, $dir.FullName).Replace('\','/')
    $parentRel = [IO.Path]::GetDirectoryName($rel).Replace('\','/')
    $id = 'd' + (Get-HexHash $rel).Substring(0,30)
    $directoryIds[$rel] = $id
    $lines.Add("<DirectoryRef Id=`"$($directoryIds[$parentRel])`"><Directory Id=`"$id`" Name=`"$(Escape-Xml $dir.Name)`" /></DirectoryRef>")
}
foreach ($entry in $directoryIds.GetEnumerator()) {
    $id = 'cdir' + (Get-HexHash $entry.Key).Substring(0,30)
    $guid = Get-StableGuid "$upgradeCode/directory/$($entry.Key)"
    $parentCleanup = if ($entry.Key -eq '') { '<RemoveFolder Id="RemoveUserProgramsFolder" Directory="UserProgramsFolder" On="uninstall" />' } else { '' }
    $lines.Add("<DirectoryRef Id=`"$($entry.Value)`"><Component Id=`"$id`" Guid=`"$guid`" Win64=`"yes`"><RemoveFolder Id=`"r$id`" On=`"uninstall`" />$parentCleanup<RegistryValue Root=`"HKCU`" Key=`"$(Escape-Xml $registryKey)\Directories`" Name=`"$id`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" /></Component></DirectoryRef>")
    $refs.Add("<ComponentRef Id=`"$id`" />")
}
foreach ($file in $files) {
    $rel = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\','/')
    $parentRel = [IO.Path]::GetDirectoryName($rel).Replace('\','/')
    $hash = (Get-HexHash $rel).Substring(0,30)
    $guid = Get-StableGuid "$upgradeCode/file/$rel"
    $lines.Add("<DirectoryRef Id=`"$($directoryIds[$parentRel])`"><Component Id=`"c$hash`" Guid=`"$guid`" Win64=`"yes`"><File Id=`"f$hash`" Name=`"$(Escape-Xml $file.Name)`" Source=`"$(Escape-Xml $file.FullName)`" /><RegistryValue Root=`"HKCU`" Key=`"$(Escape-Xml $registryKey)\Files`" Name=`"f$hash`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" /></Component></DirectoryRef>")
    $refs.Add("<ComponentRef Id=`"c$hash`" />")
}
$lines.Add('<ComponentGroup Id="PublishedPayload">')
$lines.AddRange($refs)
$lines.Add('</ComponentGroup></Fragment></Wix>')
$payloadWxs = Join-Path $work 'Payload.wxs'
$lines | Set-Content $payloadWxs -Encoding utf8

$defines = [ordered]@{
    ProductCode = $productCode; ProductName = $productName; PackageVersion = $PackageVersion
    Manufacturer = $config.manufacturer; UpgradeCode = $upgradeCode; AppName = $config.app
    AppExe = $appExe; InstallFolder = $identity; RegistryKey = $registryKey
    IconPath = (Join-Path $repo $config.icon); FixtureBuild = [int][bool]$FixtureId
    StartMenuGuid = (Get-StableGuid "$upgradeCode/shortcut/startmenu")
    DesktopGuid = (Get-StableGuid "$upgradeCode/shortcut/desktop")
    StartupGuid = (Get-StableGuid "$upgradeCode/shortcut/startup")
}
$candleArgs = @('-nologo', '-arch', 'x64', '-wx', '-ext', (Join-Path $WixBin 'WixUtilExtension.dll'))
foreach ($entry in $defines.GetEnumerator()) { $candleArgs += "-d$($entry.Key)=$($entry.Value)" }
$candleArgs += @('-out', ($work + [IO.Path]::DirectorySeparatorChar), (Join-Path $PSScriptRoot 'Product.wxs'), (Join-Path $PSScriptRoot 'PreviewUI.wxs'), $payloadWxs)
Invoke-Checked (Join-Path $WixBin 'candle.exe') $candleArgs
$msi = Join-Path $work "$identity-$PackageVersion-win-x64.msi"
# Run every ICE. Warnings are checked below, not suppressed or skipped.
# The documented ICE91 per-user-only file warning is the sole conditional exception.
$lightArgs = @('-nologo', '-cultures:en-us', '-ext', (Join-Path $WixBin 'WixUIExtension.dll'), '-ext', (Join-Path $WixBin 'WixUtilExtension.dll'),
    '-out', $msi, (Join-Path $work 'Product.wixobj'), (Join-Path $work 'PreviewUI.wixobj'), (Join-Path $work 'Payload.wixobj'))
$lightOutput = @(& (Join-Path $WixBin 'light.exe') @lightArgs 2>&1 | ForEach-Object { [string]$_ })
$lightExitCode = $LASTEXITCODE
$lightLog = Join-Path $work 'light-validation.log'
$lightOutput | Set-Content -LiteralPath $lightLog -Encoding utf8
if ($lightExitCode -ne 0) {
    $lightOutput | Select-Object -Last 80 | Out-Host
    throw "WiX light failed with exit code $lightExitCode. Full validation log: $lightLog"
}
. (Join-Path $PSScriptRoot 'Assert-MsiPolicy.ps1')
$msiPolicy = Assert-PerUserMsiPolicy -MsiPath $msi -ProductSourcePath (Join-Path $PSScriptRoot 'Product.wxs') -ExpectedInstallFolder $identity
$validationWarnings = Get-AllowedWixWarnings -Lines $lightOutput
Write-Host "WiX validation complete: $($validationWarnings.count) documented ICE91 file warnings accepted after per-user MSI invariants passed. Full log: $lightLog"
$result = [ordered]@{
    app = $config.app; packageVersion = $PackageVersion; productCode = "{$productCode}"; upgradeCode = "{$upgradeCode}"
    productName = $productName; installFolder = $identity; registryKey = $registryKey; fixtureId = $FixtureId
    msiPath = $msi; sha256 = (Get-FileHash $msi -Algorithm SHA256).Hash; payloadDirectory = $payload
    wixSourceArchive = $sourceArchive; wixSourceUrl = $sourceUrl; wixSourceSha256 = (Get-FileHash $sourceArchive -Algorithm SHA256).Hash
    sdk = $sdkVersion; targetFramework = $TargetFramework; wix = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $WixBin 'candle.exe')).FileVersion
    wixComponents = $wixComponents
    msiPolicy = $msiPolicy; validationWarnings = $validationWarnings; validationLog = $lightLog
    includedFrameworks = $runtime.runtimeOptions.includedFrameworks
    runtimeFrameworkVersion = $RuntimeFrameworkVersion
    coreClrFileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $payload 'coreclr.dll')).FileVersion
    coreClrSha256 = (Get-FileHash (Join-Path $payload 'coreclr.dll') -Algorithm SHA256).Hash
    fileCount = $files.Count; unsigned = $true
}
$result | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $work 'build-manifest.json') -Encoding utf8
[pscustomobject]$result
