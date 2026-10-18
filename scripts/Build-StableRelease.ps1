#requires -Version 7.2
<# Builds local, unsigned release assets. Does not create a tag/release, upload,
   change main/version.json, install tools or touch either user profile. #>
[CmdletBinding()]
param(
    [string]$Version = '1.1.2',
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string]$SourceCommit,
    [string]$ReleaseBaseUrl,
    [string]$RuntimeFrameworkVersion = '10.0.13'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($SourceCommit -cnotmatch '\A[0-9a-f]{40}\z') { throw 'SourceCommit must be the exact lowercase 40-hex source commit.' }
if ($Version -cnotmatch '\A(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\.(0|[1-9][0-9]{0,4})\z' -or
    @($Version.Split('.') | Where-Object { [long]$_ -gt 65534 }).Count) {
    throw 'Version must be a canonical stable major.minor.patch, each component <= 65534.'
}
$expectedBase = "https://github.com/184649/DIndex/releases/download/v$Version"
if (-not $ReleaseBaseUrl) { $ReleaseBaseUrl = $expectedBase }
if ($ReleaseBaseUrl -cne $expectedBase) { throw "ReleaseBaseUrl must be exactly $expectedBase (no latest/main, query, fragment or redirect URL)." }
if ($RuntimeFrameworkVersion -cnotmatch '\A10\.0\.(0|[1-9][0-9]{0,4})\z') { throw 'RuntimeFrameworkVersion must be a stable .NET 10.0 patch.' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $OutputDirectory) { throw 'OutputDirectory already exists. Use a NEW path; stale artifacts are never reused or deleted.' }
if (-not $IsWindows) { throw 'Stable release packaging requires Windows with preinstalled official .NET 10 SDK and PowerShell 7.2+.' }
$repo = Split-Path $PSScriptRoot -Parent
foreach ($entry in @(@('src/DIndex/DIndex.csproj', 'net10.0-windows'), @('src/DIndexUpdater/DIndexUpdater.csproj', 'net10.0'))) {
    [xml]$project = Get-Content (Join-Path $repo $entry[0]) -Raw
    if ([string]$project.SelectSingleNode('/Project/PropertyGroup/TargetFramework').InnerText -cne $entry[1]) { throw "Unexpected source target framework: $($entry[0])" }
}
$git = (Get-Command git.exe -ErrorAction Stop).Source
$head = (& $git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $head -cne $SourceCommit) { throw 'SourceCommit does not match the checked-out source HEAD.' }
& $git -C $repo diff --quiet HEAD -- src scripts/Build-StableRelease.ps1 scripts/Export-ReleaseSource.ps1 scripts/StableReleaseVerifier
if ($LASTEXITCODE -ne 0) { throw 'Release source or packaging has uncommitted tracked changes.' }
$untracked = @(& $git -C $repo ls-files --others --exclude-standard -- src scripts/StableReleaseVerifier)
if ($LASTEXITCODE -ne 0 -or $untracked.Count) { throw 'Untracked source/verification files would make source provenance incomplete.' }
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
$sdkLines = @(& $dotnet --list-sdks)
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate the preinstalled SDKs.' }
$sdk = $sdkLines | ForEach-Object { if ($_ -match '^(10\.0\.\d+) ') { $Matches[1] } } | Sort-Object { [version]$_ } -Descending | Select-Object -First 1
if (-not $sdk) { throw 'A preinstalled official stable .NET 10 SDK is required; this script does not install one.' }
[void][IO.Directory]::CreateDirectory($OutputDirectory)
$work = Join-Path $OutputDirectory 'work'
$assets = Join-Path $OutputDirectory 'release'
$sdkContext = Join-Path $work 'sdk-context'
foreach ($path in @($work, $assets, $sdkContext)) { [void][IO.Directory]::CreateDirectory($path) }
# Compile an exact, filtered tracked-source snapshot. This repository historically
# tracked generated obj/bin and root ZIPs; the shared export excludes only those.
$buildRepo = Join-Path $work 'source'
$sourceArchive = Join-Path $work 'source.zip'
$sourceSnapshot = & (Join-Path $PSScriptRoot 'Export-ReleaseSource.ps1') -SourceCommit $SourceCommit -RepositoryDirectory $repo -OutputArchive $sourceArchive -ManifestPath (Join-Path $work 'source-manifest.json')
[IO.Compression.ZipFile]::ExtractToDirectory($sourceArchive, $buildRepo)
@{ sdk = @{ version = $sdk; rollForward = 'disable'; allowPrerelease = $false } } | ConvertTo-Json | Set-Content (Join-Path $sdkContext 'global.json') -Encoding utf8NoBOM
function Invoke-Checked([string[]]$Arguments) {
    & $dotnet @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed ($LASTEXITCODE): $($Arguments -join ' ')" }
}
function Publish-Application([string]$Name, [string]$Framework, [string[]]$Extra = @()) {
    $payload = Join-Path $work "$Name-publish"
    $arguments = @('publish', (Join-Path $buildRepo "src/$Name/$Name.csproj"), '--configuration', 'Release',
        '--framework', $Framework, '--runtime', 'win-x64', '--self-contained', 'true',
        '--output', $payload, '--artifacts-path', (Join-Path $work "$Name-artifacts"),
        "-p:Version=$Version", "-p:AssemblyVersion=$Version.0", "-p:FileVersion=$Version.0",
        "-p:InformationalVersion=$Version", '-p:IncludeSourceRevisionInInformationalVersion=false',
        "-p:RuntimeFrameworkVersion=$RuntimeFrameworkVersion", '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=false',
        '-p:PublishTrimmed=false', '-p:PublishReadyToRun=false', '-p:UseAppHost=true',
        '-p:DebugType=None', '-p:DebugSymbols=false', '-p:RestoreSources=https://api.nuget.org/v3/index.json') + $Extra
    Invoke-Checked $arguments
    $published = @(Get-ChildItem -LiteralPath $payload -Force)
    if ($published.Count -ne 1 -or $published[0].Name -cne "$Name.exe" -or $published[0].PSIsContainer) {
        throw "Expected only $Name.exe in clean single-file publish output; refuse to omit unexpected files: $payload"
    }
    $zipPath = Join-Path $assets "$Name.zip"
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry("$Name.exe", [IO.Compression.CompressionLevel]::Optimal)
        # ZIP's minimum timestamp avoids rebuild-time hash churn before pinning.
        # In Create mode the timestamp must be set BEFORE opening the entry.
        $entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
        $inputStream = [IO.File]::OpenRead($published[0].FullName)
        try {
            $outputStream = $entry.Open()
            try { $inputStream.CopyTo($outputStream) }
            finally { $outputStream.Dispose() }
        } finally { $inputStream.Dispose() }
    }
    finally { $zip.Dispose() }
}
Push-Location $sdkContext
try {
    if ((& $dotnet --version).Trim() -cne $sdk -or $LASTEXITCODE -ne 0) { throw 'Unexpected selected SDK.' }
    # Order is security-critical: finish app ZIP -> pin its exact hash -> compile updater.
    Publish-Application 'DIndex' 'net10.0-windows'
    $appHash = (Get-FileHash -LiteralPath (Join-Path $assets 'DIndex.zip') -Algorithm SHA256).Hash
    $pin = [ordered]@{ version = $Version; downloadUrl = "$ReleaseBaseUrl/DIndex.zip"; downloadSha256 = $appHash }
    $pinPath = Join-Path $work 'legacy-update.json'
    $pin | ConvertTo-Json | Set-Content -LiteralPath $pinPath -Encoding utf8NoBOM
    Publish-Application 'DIndexUpdater' 'net10.0' @("-p:LegacyUpdateMetadataPath=$pinPath")
    $updaterHash = (Get-FileHash -LiteralPath (Join-Path $assets 'DIndexUpdater.zip') -Algorithm SHA256).Hash
    [ordered]@{
        version = $Version; downloadUrl = "$ReleaseBaseUrl/DIndex.zip"; updaterUrl = "$ReleaseBaseUrl/DIndexUpdater.zip"
        downloadSha256 = $appHash; updaterSha256 = $updaterHash; releaseNotes = "$ReleaseBaseUrl/release-notes.json"
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $assets 'version.json') -Encoding utf8NoBOM
    [ordered]@{ version = $Version; notes = @(
        '閉じてタスクトレイに常駐するときの案内通知を表示しないようにしました。',
        '索引の初期作成中にも検索結果を順次表示するようにしました。',
        '検索、キャッシュ保存、終了時処理の信頼性を改善しました。',
        '更新ファイルのSHA-256検証と旧バージョンからの安全な更新に対応しました。',
        '通常版の設定・キャッシュ保存先は従来どおりLocalAppData\\DIndexです。'
    ) } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $assets 'release-notes.json') -Encoding utf8NoBOM
    # Runtime licenses remain separate release assets; legacy ZIPs contain one EXE only.
    $cacheLines = @(& $dotnet nuget locals global-packages --list --force-english-output)
    if ($LASTEXITCODE -ne 0 -or ($cacheLines -join '') -notmatch 'global-packages:\s*(.+)') { throw 'Cannot locate official runtime license notices.' }
    $cache = $Matches[1].Trim()
    $notices = [Collections.Generic.List[string]]::new()
    $coreClrReference = $null
    foreach ($package in @('Microsoft.NETCore.App.Runtime.win-x64', 'Microsoft.WindowsDesktop.App.Runtime.win-x64')) {
        $roots = @((Join-Path $cache "$($package.ToLowerInvariant())/$RuntimeFrameworkVersion"), (Join-Path (Split-Path $dotnet -Parent) "packs/$package/$RuntimeFrameworkVersion"))
        $pack = $roots | Where-Object { Test-Path -LiteralPath $_ -PathType Container } | Select-Object -First 1
        if (-not $pack) { throw "Cannot find official runtime pack $package/$RuntimeFrameworkVersion." }
        if ($package -eq 'Microsoft.NETCore.App.Runtime.win-x64') {
            $coreClrReference = Join-Path $pack 'runtimes/win-x64/native/coreclr.dll'
            if (-not (Test-Path -LiteralPath $coreClrReference -PathType Leaf)) { throw 'Exact restored CoreCLR reference is missing.' }
        }
        $files = @(Get-ChildItem -LiteralPath $pack -File | Where-Object { $_.Name -match '^(LICENSE|THIRD[-_ ]?PARTY[-_ ]?NOTICES|NOTICE)(\..*)?$' } | Sort-Object Name)
        if (-not ($files | Where-Object Name -Match '^LICENSE')) { throw "Missing runtime license in $pack" }
        if ($package -like 'Microsoft.NETCore*' -and -not ($files | Where-Object Name -Match '^(THIRD|NOTICE)')) { throw 'Missing .NET runtime third-party notices.' }
        foreach ($file in $files) { $notices.Add("=== $package/$RuntimeFrameworkVersion : $($file.Name) ===`n" + (Get-Content -LiteralPath $file.FullName -Raw)) }
    }
    $notices -join "`n`n" | Set-Content -LiteralPath (Join-Path $assets 'THIRD-PARTY-NOTICES.txt') -Encoding utf8NoBOM
    $assetNames = @('DIndex.zip', 'DIndexUpdater.zip', 'release-notes.json', 'THIRD-PARTY-NOTICES.txt', 'version.json')
    $assetNames | ForEach-Object { (Get-FileHash -LiteralPath (Join-Path $assets $_) -Algorithm SHA256).Hash + '  ' + $_ } |
        Set-Content -LiteralPath (Join-Path $assets 'SHA256SUMS') -Encoding ascii
    $verifier = Join-Path $buildRepo 'scripts/StableReleaseVerifier/StableReleaseVerifier.csproj'
    $verifierOutput = Join-Path $work 'verifier'
    Invoke-Checked @('build', $verifier, '-c', 'Release', '-o', $verifierOutput, '--artifacts-path', (Join-Path $work 'verifier-artifacts'), '-p:RestoreSources=https://api.nuget.org/v3/index.json')
    $verifierDll = Join-Path $verifierOutput 'StableReleaseVerifier.dll'
    Invoke-Checked @($verifierDll, 'self-test')
    $runtimeProofPath = Join-Path $work 'native-runtime-proof.json'
    Invoke-Checked @($verifierDll, 'verify', $assets, $Version, $RuntimeFrameworkVersion, $coreClrReference, $runtimeProofPath)
    $runtimeSmokeDirectory = Join-Path $work 'runtime-smoke'
    Invoke-Checked @($verifierDll, 'smoke', (Join-Path $work 'DIndex-publish/DIndex.exe'), (Join-Path $work 'DIndexUpdater-publish/DIndexUpdater.exe'), $Version, $runtimeSmokeDirectory)
    # This marker is written only after all checks; failed directories are diagnostic only.
    [ordered]@{ version = $Version; repository = '184649/DIndex'; sourceCommit = $SourceCommit; sdk = $sdk; runtime = $RuntimeFrameworkVersion; assetDirectory = $assets;
        sourceSnapshot = @{ sourceTree = $sourceSnapshot.sourceTree; filterId = $sourceSnapshot.filterId; includedPathsSha256 = $sourceSnapshot.includedPathsSha256; includedCount = $sourceSnapshot.includedCount; excludedCount = $sourceSnapshot.excludedCount };
        runtimeProof = (Get-Content -LiteralPath $runtimeProofPath -Raw | ConvertFrom-Json -Depth 10);
        runtimeSmoke = (Get-Content -LiteralPath (Join-Path $runtimeSmokeDirectory 'smoke-proof.json') -Raw | ConvertFrom-Json -Depth 5);
        unsigned = $true; profile = '%LocalAppData%\DIndex'; verified = $true; published = $false;
        downloadSha256 = $appHash; updaterSha256 = $updaterHash;
        appExeSha256 = (Get-FileHash -LiteralPath (Join-Path $work 'DIndex-publish/DIndex.exe') -Algorithm SHA256).Hash;
        updaterExeSha256 = (Get-FileHash -LiteralPath (Join-Path $work 'DIndexUpdater-publish/DIndexUpdater.exe') -Algorithm SHA256).Hash;
        assets = @($assetNames | ForEach-Object { @{ name = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $assets $_) -Algorithm SHA256).Hash } }) } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'verified-release.json') -Encoding utf8NoBOM
    Write-Host "Verified local stable release assets: $assets. Nothing has been published."
} finally { Pop-Location }
