#requires -Version 7.2
# One-shot DIndex v1.1.2 publication. Only an explicitly approved release job may invoke this script.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceCommit,
    [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$DistributionCommit
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = '184649/DIndex'
$tag = 'v1.1.2'
$version = '1.1.2'
$releaseBase = "https://github.com/$repo/releases/download/$tag"
if (-not $IsWindows -or $env:GITHUB_ACTIONS -cne 'true' -or $env:GITHUB_REPOSITORY -cne $repo -or
    $env:GITHUB_REF -cne 'refs/heads/improve/performance-reliability-20261017' -or
    $env:GITHUB_SERVER_URL -cne 'https://github.com' -or $env:GITHUB_API_URL -cne 'https://api.github.com' -or
    $env:GITHUB_RUN_ID -cnotmatch '\A[1-9][0-9]*\z') {
    throw 'Publication is restricted to Windows Actions in the approved GitHub repository and dedicated source branch.'
}
if (-not $env:GH_TOKEN -or ($env:GH_HOST -and $env:GH_HOST -cne 'github.com')) {
    throw 'The job-scoped GitHub token or canonical GitHub host is unavailable.'
}
if (-not $env:RUNNER_TEMP -or -not (Test-Path -LiteralPath $env:RUNNER_TEMP -PathType Container)) { throw 'RUNNER_TEMP must exist.' }
$checkout = (Resolve-Path -LiteralPath (Split-Path $PSScriptRoot -Parent)).Path
$root = (Resolve-Path -LiteralPath $PackageDirectory).Path
function Invoke-Git([string[]]$Arguments) {
    $output = & git -C $checkout @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Local git verification failed (exit $LASTEXITCODE)." }
    return ($output -join "`n").Trim()
}
function Invoke-GhJson([string[]]$Arguments) {
    $output = & gh api --hostname github.com @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub operation failed (exit $LASTEXITCODE). Existing release state was preserved; do not blindly retry." }
    return (($output -join "`n") | ConvertFrom-Json -Depth 30)
}
function Assert-Hash([string]$Path, [string]$Expected) {
    if ($Expected -cnotmatch '\A[a-fA-F0-9]{64}\z' -or -not (Test-Path -LiteralPath $Path -PathType Leaf) -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $Expected) { throw "Asset hash mismatch: $Path" }
}
function Read-BlobText([string]$Sha) {
    $blob = Invoke-GhJson @("repos/$repo/git/blobs/$Sha")
    if ($blob.sha -cne $Sha -or $blob.encoding -cne 'base64' -or $blob.size -gt 1048576) { throw 'Unexpected distribution metadata blob.' }
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($blob.content))
}
# Resolve git relative to this script, never the caller's current directory.
if ([IO.Path]::GetFullPath((Invoke-Git @('rev-parse','--show-toplevel'))) -ine [IO.Path]::GetFullPath($checkout) -or
    (Invoke-Git @('rev-parse','HEAD')) -cne $SourceCommit -or
    (Invoke-Git @('remote','get-url','origin')) -cnotmatch '\Ahttps://github\.com/184649/DIndex(?:\.git)?\z') {
    throw 'The script checkout, canonical origin and exact source HEAD must match the approved build.'
}
$changed = Invoke-Git @('diff','--name-only','HEAD','--','.',':(exclude)global.json')
$untracked = Invoke-Git @('ls-files','--others','--exclude-standard')
if ($changed -or $untracked) { throw 'Tracked source changes or untracked inputs exist in the source checkout.' }
$sourceTree = Invoke-Git @('rev-parse',"${SourceCommit}^{tree}")
$baseAssets = @('DIndex.zip','DIndexUpdater.zip','version.json','release-notes.json','THIRD-PARTY-NOTICES.txt')
$actualFiles = @(Get-ChildItem -LiteralPath $root -Force)
$expectedFiles = @($baseAssets + 'SHA256SUMS' | Sort-Object)
if ($actualFiles.Count -ne $expectedFiles.Count -or @($actualFiles | Where-Object { $_.PSIsContainer -or ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) }).Count -or
    (Compare-Object ($actualFiles.Name | Sort-Object) $expectedFiles -CaseSensitive)) { throw 'PackageDirectory must contain only the six clean, verified build outputs.' }
$markerPath = Join-Path (Split-Path $root -Parent) 'verified-release.json'
$verified = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json
if ($verified.repository -cne $repo -or $verified.version -cne $version -or $verified.sourceCommit -cne $SourceCommit -or
    $verified.verified -isnot [bool] -or $verified.verified -cne $true -or
    $verified.published -isnot [bool] -or $verified.published -cne $false -or $verified.unsigned -isnot [bool] -or $verified.unsigned -cne $true -or
    [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($verified.assetDirectory)) -ine [IO.Path]::TrimEndingDirectorySeparator($root) -or
    $verified.sdk -cnotmatch '\A10\.0\.[0-9]+\z' -or $verified.runtime -cnotmatch '\A10\.0\.[0-9]+\z') {
    throw 'The successful build marker must match this exact source, repository, asset directory and stable .NET 10 build.'
}
# Validation jobs deliberately select a preinstalled SDK in the tracked global.json.
# This is the only permitted working-tree difference, and its contents are checked.
if (Invoke-Git @('diff','--name-only','HEAD','--','global.json')) {
    $sdkSelection = Get-Content -LiteralPath (Join-Path $checkout 'global.json') -Raw | ConvertFrom-Json
    if ($sdkSelection.sdk.version -cne $verified.sdk -or $sdkSelection.sdk.rollForward -cne 'disable') { throw 'Unexpected SDK-selection override.' }
}
if (@($verified.assets).Count -ne $baseAssets.Count -or
    (Compare-Object (@($verified.assets | ForEach-Object { $_.name }) | Sort-Object) ($baseAssets | Sort-Object) -CaseSensitive)) {
    throw 'Build verification must cover every base asset exactly once.'
}
foreach ($asset in $verified.assets) { Assert-Hash (Join-Path $root $asset.name) $asset.sha256 }
$checksumLines = @(Get-Content -LiteralPath (Join-Path $root 'SHA256SUMS'))
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
if ($checksumLines.Count -ne $baseAssets.Count) { throw 'Unexpected build checksum list.' }
foreach ($line in $checksumLines) {
    if ($line -cnotmatch '\A([a-fA-F0-9]{64})  ([A-Za-z0-9._-]+)\z' -or $Matches[2] -cnotin $baseAssets -or -not $seen.Add($Matches[2])) { throw 'Invalid or duplicate build checksum entry.' }
    Assert-Hash (Join-Path $root $Matches[2]) $Matches[1]
}
$manifest = Get-Content -LiteralPath (Join-Path $root 'version.json') -Raw | ConvertFrom-Json
$notes = Get-Content -LiteralPath (Join-Path $root 'release-notes.json') -Raw | ConvertFrom-Json
if ($manifest.version -cne $version -or $notes.version -cne $version -or
    $manifest.downloadUrl -cne "$releaseBase/DIndex.zip" -or $manifest.updaterUrl -cne "$releaseBase/DIndexUpdater.zip" -or
    $manifest.releaseNotes -cne "$releaseBase/release-notes.json" -or
    $manifest.downloadSha256 -ine $verified.downloadSha256 -or $manifest.updaterSha256 -ine $verified.updaterSha256) { throw 'Unexpected release manifest, notes or build hashes.' }
Assert-Hash (Join-Path $root 'DIndex.zip') $manifest.downloadSha256
Assert-Hash (Join-Path $root 'DIndexUpdater.zip') $manifest.updaterSha256
# Recheck the exact executable bytes without loading or executing either application.
foreach ($pair in @(@('DIndex','appExeSha256'), @('DIndexUpdater','updaterExeSha256'))) {
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $root ($pair[0] + '.zip')))
    try {
        if ($zip.Entries.Count -ne 1 -or $zip.Entries[0].FullName -cne ($pair[0] + '.exe') -or
            $zip.Entries[0].Length -le 0 -or $zip.Entries[0].Length -gt 536870912) { throw 'Unexpected release executable ZIP layout.' }
        $inputStream = $zip.Entries[0].Open()
        try { $exeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($inputStream)) }
        finally { $inputStream.Dispose() }
        if ($verified.($pair[1]) -cnotmatch '\A[a-fA-F0-9]{64}\z' -or $exeHash -ine $verified.($pair[1])) { throw 'Executable bytes differ from the verified build.' }
    } finally { $zip.Dispose() }
}
$source = Invoke-GhJson @("repos/$repo/git/commits/$SourceCommit")
$distribution = Invoke-GhJson @("repos/$repo/git/commits/$DistributionCommit")
$main = Invoke-GhJson @("repos/$repo/git/ref/heads/main")
$currentFeedBlob = Invoke-GhJson @("repos/$repo/contents/version.json?ref=$($main.object.sha)")
if ($currentFeedBlob.type -cne 'file' -or $currentFeedBlob.encoding -cne 'base64' -or $currentFeedBlob.size -gt 1048576) { throw 'Cannot verify the current discovery feed.' }
$currentFeed = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($currentFeedBlob.content)) | ConvertFrom-Json
$currentVersion = $null
if (-not [version]::TryParse([string]$currentFeed.version, [ref]$currentVersion) -or $currentVersion -ge [version]$version -or
    $currentFeed.downloadUrl -like "*/releases/download/$tag/*" -or $currentFeed.updaterUrl -like "*/releases/download/$tag/*") {
    throw 'The discovery feed is already activated, invalid or newer than this release. Reconcile before publication.'
}
if ($source.sha -cne $SourceCommit -or $source.tree.sha -cne $sourceTree -or $distribution.sha -cne $DistributionCommit -or
    $DistributionCommit -ceq $SourceCommit -or @($distribution.parents).Count -ne 1 -or $distribution.parents[0].sha -cne $main.object.sha) {
    throw 'Source identity or distribution snapshot ancestry does not match the approved release.'
}
$distributionTree = Invoke-GhJson @("repos/$repo/git/trees/$($distribution.tree.sha)?recursive=1")
if ($distributionTree.truncated -or @($distributionTree.tree).Count -ne 2 -or
    (Compare-Object (@($distributionTree.tree | ForEach-Object { $_.path }) | Sort-Object) @('DISTRIBUTION.json','README.md') -CaseSensitive) -or
    @($distributionTree.tree | Where-Object { $_.type -cne 'blob' -or $_.mode -cne '100644' }).Count) {
    throw 'The distribution tag must contain only regular README.md and DISTRIBUTION.json metadata files, with no source, old binaries or workflows.'
}
$distributionMetadata = (Read-BlobText (($distributionTree.tree | Where-Object path -CEQ 'DISTRIBUTION.json').sha)) | ConvertFrom-Json
if ($distributionMetadata.repository -cne $repo -or $distributionMetadata.version -cne $version -or
    $distributionMetadata.sourceCommit -cne $SourceCommit -or $distributionMetadata.tag -cne $tag -or
    $distributionMetadata.downloadUrl -cne "$releaseBase/DIndex.zip" -or $distributionMetadata.updaterUrl -cne "$releaseBase/DIndexUpdater.zip" -or
    $distributionMetadata.releaseNotes -cne "$releaseBase/release-notes.json" -or $distributionMetadata.sourceArchiveUrl -cne "$releaseBase/DIndex-1.1.2-source.zip") {
    throw 'Distribution metadata does not identify the exact source commit and immutable release assets.'
}
$distributionReadme = Read-BlobText (($distributionTree.tree | Where-Object path -CEQ 'README.md').sha)
if (-not $distributionReadme.Contains($SourceCommit) -or $distributionReadme -notmatch '(?i)distribution snapshot' -or
    $distributionReadme -notmatch '(?i)not the application source') { throw 'Distribution README must explicitly distinguish the tag snapshot from the exact application source.' }
# Paginate all releases: an older draft can exist without a corresponding tag.
$releasePages = @(Invoke-GhJson @('--paginate','--slurp',"repos/$repo/releases?per_page=100"))
foreach ($page in $releasePages) { foreach ($existing in $page) {
    if ($existing.tag_name -ceq $tag) { throw 'The release already exists; reconcile its state manually before any retry.' }
} }
$tags = @(Invoke-GhJson @("repos/$repo/git/matching-refs/tags/$tag"))
if (@($tags | Where-Object ref -CEQ "refs/tags/$tag").Count) { throw 'The release tag already exists; it will not be replaced.' }

# Keep the verified build directory untouched. Every subsequent phase uses fresh copies.
$stage = Join-Path $env:RUNNER_TEMP ('dindex-publish-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -ErrorAction Stop | Out-Null
$assets = Join-Path $stage 'assets'
$verification = Join-Path $stage 'download-verification'
$legacyEvidence = Join-Path $stage 'legacy-validation'
foreach ($path in @($assets,$verification,$legacyEvidence)) { New-Item -ItemType Directory -Path $path | Out-Null }
foreach ($name in $baseAssets) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination (Join-Path $assets $name) -ErrorAction Stop }
foreach ($asset in $verified.assets) { Assert-Hash (Join-Path $assets $asset.name) $asset.sha256 }
$sourceZip = Join-Path $assets 'DIndex-1.1.2-source.zip'
$null = Invoke-Git @('archive','--format=zip','--prefix=DIndex-source/',"--output=$sourceZip",$SourceCommit)
$provenance = [ordered]@{ product='DIndex'; repository=$repo; version=$version; sourceCommit=$SourceCommit;
    sourceTree=$sourceTree; distributionCommit=$DistributionCommit; distributionParent=$main.object.sha;
    sourceArchive='DIndex-1.1.2-source.zip'; sourceArchiveSha256=(Get-FileHash -LiteralPath $sourceZip -Algorithm SHA256).Hash.ToLowerInvariant(); distributionTag=$tag;
    workflowRun="https://github.com/$repo/actions/runs/$env:GITHUB_RUN_ID"; sdk=$verified.sdk; runtime=$verified.runtime;
    appExeSha256=$verified.appExeSha256; updaterExeSha256=$verified.updaterExeSha256;
    signed=$false; profile='%LOCALAPPDATA%\DIndex' }
$provenance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $assets 'build-provenance.json') -Encoding utf8NoBOM
$assetNames = @($baseAssets + @('DIndex-1.1.2-source.zip','build-provenance.json'))
$hashLines = foreach ($name in $assetNames) { "$((Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $assets $name)).Hash.ToLowerInvariant())  $name" }
$hashLines | Set-Content -LiteralPath (Join-Path $assets 'SHA256SUMS.txt') -Encoding utf8NoBOM
$assetNames += 'SHA256SUMS.txt'
$expectedHashes = @{}
foreach ($name in $assetNames) { $expectedHashes[$name] = (Get-FileHash -LiteralPath (Join-Path $assets $name) -Algorithm SHA256).Hash }
$pendingText = 'Public-asset compatibility validation is in progress. The update feed has not been activated. This prerelease is promoted only after the isolated legacy upgrade test passes.'
$body = @"
Application source: https://github.com/$repo/tree/$SourceCommit
Application source archive: DIndex-1.1.2-source.zip. This release tag identifies a distribution snapshot; GitHub's automatic tag archives are not the application source.

Windows x64, self-contained .NET 10. The redundant close-to-tray balloon is removed. Existing public v1.0.1 users retain the normal DIndex profile and can update via the existing updater after the discovery feed is activated.

Unsigned executables. SHA-256 verifies asset integrity, not publisher identity. Do not bypass operating-system security warnings.

$pendingText
"@
$requestPath = Join-Path $stage 'release-request.json'
$statePath = Join-Path $stage 'publication-state.json'
$state = [ordered]@{ version=$version; sourceCommit=$SourceCommit; distributionCommit=$DistributionCommit; phase='validated-local-and-remote'; releaseId=$null; releaseUrl=$null; feedChanged=$false }
function Set-Phase([string]$Phase) {
    $state.phase = $Phase
    $state | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $statePath -Encoding utf8NoBOM
    Write-Host "Publication phase: $Phase. Evidence retained: $stage"
}
function Assert-MainUnchanged {
    $currentMain = Invoke-GhJson @("repos/$repo/git/ref/heads/main")
    if ($currentMain.object.type -cne 'commit' -or $currentMain.object.sha -cne $main.object.sha) { throw 'Main changed during publication; reconcile the discovery feed and distribution parent before continuing.' }
}
$remoteAssetIds = @{}
function Assert-PublishedTag {
    $actual = Invoke-GhJson @("repos/$repo/git/ref/tags/$tag")
    if ($actual.ref -cne "refs/tags/$tag" -or $actual.object.type -cne 'commit' -or $actual.object.sha -cne $DistributionCommit) { throw 'Published tag no longer identifies the exact distribution snapshot.' }
}
function Assert-ReleaseState($Release, [bool]$Draft, [bool]$Prerelease) {
    if ($Release.id -ne $state.releaseId -or $Release.tag_name -cne $tag -or $Release.draft -cne $Draft -or $Release.prerelease -cne $Prerelease -or
        $Release.target_commitish -cne $DistributionCommit -or
        (-not $Draft -and $Release.html_url -cne "https://github.com/$repo/releases/tag/$tag")) { throw 'Unexpected remote release identity or state.' }
    $remoteAssets = @(Invoke-GhJson @('--paginate','--slurp',"repos/$repo/releases/$($state.releaseId)/assets?per_page=100") | ForEach-Object { $_ })
    if ($remoteAssets.Count -ne $assetNames.Count -or
        (Compare-Object ($remoteAssets.name | Sort-Object) ($assetNames | Sort-Object) -CaseSensitive)) { throw 'Missing, duplicate or unexpected remote release assets.' }
    foreach ($asset in $remoteAssets) {
        if ($asset.state -cne 'uploaded' -or $asset.size -ne (Get-Item -LiteralPath (Join-Path $assets $asset.name)).Length -or
            $asset.id -le 0 -or $asset.url -cne "https://api.github.com/repos/$repo/releases/assets/$($asset.id)" -or
            (-not $Draft -and $asset.browser_download_url -cne "$releaseBase/$($asset.name)")) { throw 'Remote release asset identity, state or size mismatch.' }
        if ($remoteAssetIds.ContainsKey($asset.name)) {
            if ($remoteAssetIds[$asset.name] -ne $asset.id) { throw 'A hash-verified release asset was replaced during publication.' }
        } elseif ($Draft) { $remoteAssetIds[$asset.name] = $asset.id }
        else { throw 'Remote asset identity was not verified in the draft phase.' }
    }
}
try {
    @{ tag_name=$tag; target_commitish=$DistributionCommit; name='DIndex 1.1.2'; body=$body; draft=$true; prerelease=$true; make_latest='false' } |
        ConvertTo-Json | Set-Content -LiteralPath $requestPath -Encoding utf8NoBOM
    Assert-MainUnchanged
    Set-Phase 'creating-draft-outcome-not-yet-confirmed'
    $release = Invoke-GhJson @('--method','POST',"repos/$repo/releases",'--input',$requestPath)
    if (-not $release.draft -or -not $release.prerelease -or $release.tag_name -cne $tag -or $release.target_commitish -cne $DistributionCommit) { throw 'Unexpected release creation result.' }
    $state.releaseId = $release.id; $state.releaseUrl = $release.html_url
    Set-Phase 'draft-created'
    $assetPaths = @($assetNames | ForEach-Object { Join-Path $assets $_ })
    & gh release upload $tag @assetPaths --repo "github.com/$repo"
    if ($LASTEXITCODE -ne 0) { throw 'Upload failed. The incomplete draft is retained for inspection.' }
    & gh release download $tag --repo "github.com/$repo" --dir $verification
    if ($LASTEXITCODE -ne 0) { throw 'Draft asset download verification failed; draft remains unpublished.' }
    $downloaded = @(Get-ChildItem -LiteralPath $verification -Force)
    if ($downloaded.Count -ne $assetNames.Count -or (Compare-Object ($downloaded.Name | Sort-Object) ($assetNames | Sort-Object) -CaseSensitive)) { throw 'Unexpected draft download file set.' }
    foreach ($name in $assetNames) { Assert-Hash (Join-Path $verification $name) $expectedHashes[$name] }
    $readback = Invoke-GhJson @("repos/$repo/releases/$($release.id)")
    Assert-ReleaseState $readback $true $true
    Set-Phase 'draft-assets-hash-verified'
    @{ draft=$false; prerelease=$true; make_latest='false' } | ConvertTo-Json | Set-Content -LiteralPath $requestPath -Encoding utf8NoBOM
    Assert-MainUnchanged
    Set-Phase 'publishing-prerelease-outcome-not-yet-confirmed'
    $public = Invoke-GhJson @('--method','PATCH',"repos/$repo/releases/$($release.id)",'--input',$requestPath)
    Assert-ReleaseState $public $false $true
    Assert-PublishedTag
    Set-Phase 'public-prerelease-awaiting-legacy-validation'
    & (Join-Path $PSScriptRoot 'Test-PublishedLegacyUpgrade.ps1') -ExpectedManifestPath (Join-Path $assets 'version.json') -OutputDirectory $legacyEvidence
    if (-not $?) { throw 'Legacy published-asset upgrade validation failed; release remains prerelease and feed is unchanged.' }
    $resultFiles = @(Get-ChildItem -LiteralPath $legacyEvidence -Filter 'test-results.json' -File -Recurse)
    if ($resultFiles.Count -ne 1) { throw 'The legacy test did not produce one unambiguous result record.' }
    $legacy = Get-Content -LiteralPath $resultFiles[0].FullName -Raw | ConvertFrom-Json
    if ($legacy.success -cne $true -or $legacy.legacyAppExecuted -cne $false -or $legacy.release -cne $tag -or
        $legacy.appZipSha256 -ine $manifest.downloadSha256 -or $legacy.updaterZipSha256 -ine $manifest.updaterSha256 -or
        @($legacy.cleanupErrors).Count -ne 0) { throw 'Legacy validation evidence does not confirm a clean pass for these exact assets.' }
    Assert-PublishedTag
    $readback = Invoke-GhJson @("repos/$repo/releases/$($release.id)")
    Assert-ReleaseState $readback $false $true
    Set-Phase 'public-prerelease-legacy-validation-passed'
    $finalBody = $body.Replace($pendingText,
        'The published-asset legacy CLI replacement test passed against an isolated copy of the original v1.0.1 executable. The test omits --restart to avoid executing the legacy application against a real profile. Old-UI end-to-end interaction and human GUI acceptance are not claimed. The discovery feed is activated separately after release verification.')
    @{ prerelease=$false; make_latest='true'; body=$finalBody } | ConvertTo-Json | Set-Content -LiteralPath $requestPath -Encoding utf8NoBOM
    Assert-MainUnchanged
    Set-Phase 'promoting-stable-outcome-not-yet-confirmed'
    $final = Invoke-GhJson @('--method','PATCH',"repos/$repo/releases/$($release.id)",'--input',$requestPath)
    Assert-ReleaseState $final $false $false
    Assert-PublishedTag
    $latest = Invoke-GhJson @("repos/$repo/releases/latest")
    if ($latest.id -ne $release.id -or $latest.tag_name -cne $tag -or $latest.draft -or $latest.prerelease) { throw 'Stable promotion latest-release readback did not match.' }
    Set-Phase 'stable-release-verified-feed-not-activated'
    Write-Host "Stable release verified: $($final.html_url)"
    Write-Host 'Main version.json was NOT changed by this job. Activate it only after reviewing these results.'
    Get-Content -LiteralPath (Join-Path $assets 'version.json') -Raw | Write-Host
    $provenance | ConvertTo-Json -Depth 6 | Write-Host
    $hashLines | Write-Host
} catch {
    Write-Warning "Publication stopped in phase '$($state.phase)'. No release/tag was deleted or retried. Inspect $statePath and the remote state before taking further action. The discovery feed was not changed."
    throw
}
