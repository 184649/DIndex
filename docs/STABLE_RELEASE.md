# Stable portable release: DIndex 1.1.2

The stable channel retains the existing portable contract: `DIndex.zip` contains
only `DIndex.exe`, and `DIndexUpdater.zip` contains only `DIndexUpdater.exe`.
Both executables are Windows x64 self-contained .NET 10 single-file applications.
The separately maintained MSI remains a preview build. This procedure does not
change either project's preview defaults or the existing MSI entry points.

## Local Windows/CI build (no publication)

Use an official, preinstalled stable .NET 10 SDK, PowerShell 7.2+, and Git. The
script selects an installed .NET 10 SDK in a private `global.json` context; it
does not install tools or rewrite the repository's SDK selection. Restore is
restricted to the official NuGet source. The pinned runtime defaults to 10.0.13.

```powershell
./tests/StableRelease.Tests.ps1
./scripts/Build-StableRelease.ps1 -Version 1.1.2 `
  -SourceCommit (git rev-parse HEAD) `
  -OutputDirectory "$env:RUNNER_TEMP/stable-release"
```

`OutputDirectory` must not exist, including an empty existing directory. The
builder never deletes/reuses old output. It also verifies the exact source
commit and rejects changed/untracked application or verifier sources. It builds
a fresh `git archive` snapshot of that commit, so old checkout `obj`/`bin` trees
from earlier validation steps cannot enter SDK compile globs. A failure
leaves diagnostic files but no `verified-release.json` success marker. Choose a
new output path before retrying.

Production builds must use the committed `net10.0-windows`/`net10.0` project
targets. All assembly/file/informational version properties are explicitly
overridden to `1.1.2.0`/`1.1.2`; source revision suffixes are disabled. The stable
app therefore keeps `%LocalAppData%\DIndex`, while the MSI preview retains its
separate preview profile. No app or updater is launched by this packaging step.

The security-sensitive sequence is:

1. Build the app in fresh intermediate/publish directories and require exactly
   one `DIndex.exe`. Build its final ZIP, then compute its SHA-256. Both ZIP entry
   timestamps are fixed to 1980-01-01 UTC to avoid timestamp-only hash churn.
   This is not a cross-SDK/compiler byte-reproducibility guarantee.
2. Create a UTF-8 pin containing `version`, `downloadUrl`, and `downloadSha256`.
   Build the updater with `-p:LegacyUpdateMetadataPath=<absolute pin path>`.
   The pin is compiled as `DIndexUpdater.LegacyUpdate.json` into the updater.
3. Create its one-EXE ZIP and hash it. Generate the feed with both hashes and
   exact `https://github.com/184649/DIndex/releases/download/v1.1.2/` asset URLs.
4. Preserve the official runtime licenses/notices as a separate release asset.
   Create checksums for every generated release asset.
5. Run synthetic verifier regressions and verify the actual final EXEs inside
   the ZIPs: x64 native apphost versions, bundled managed assembly versions,
   informational versions with no preview suffix, bundled self-contained runtime
   versions, and the compiled updater pin matching the app ZIP and exact URL.

`release/` contains `DIndex.zip`, `DIndexUpdater.zip`, `version.json`,
`release-notes.json`, `THIRD-PARTY-NOTICES.txt`, and `SHA256SUMS`. No PDB, profile,
cache or MSI marker is accepted. `verified-release.json` beside `release/`
records the source commit, repository, SDK/runtime, both ZIP and EXE hashes, all
asset hashes, and `verified: true`, `published: false`.

The verifier intentionally rejects extra/stale files in its release directory.
A separate approved publication step can copy these verified assets to a new
staging directory and add the source archive/provenance plus complete checksums
there. Do not edit an already verified ZIP: that would invalidate both the feed
and the updater's compiled app pin.

## Tests and limits

`tests/StableRelease.Tests.ps1` checks PowerShell syntax, invalid versions/URLs,
invalid source/runtime inputs, and preserving existing output. It performs no
build or profile access. The verifier's `self-test` exercises synthetic archive,
checksum, stale artifact, bundle, compiled metadata and runtime failures.

For an already-installed .NET 8 SDK on a non-Windows test host:

```sh
bash tests/run-stable-release-tests.sh /path/to/official/dotnet-sdk
```

This direct Roslyn test-only fallback changes no production target and downloads
nothing. It is not evidence that the .NET 10 Windows app or updater built or ran.
The actual Windows packaging command and runtime/legacy update acceptance tests
must pass before publication is called ready.

Publishing remains a separate explicitly approved operation. Keep the existing
`main/version.json` unchanged until the release assets exist and the exact
legacy updater command has passed against a disposable synthetic installation.
Do not add a production URL override or network backdoor to simulate the old
client. A source branch update, successful local package, or draft release alone
does not authorize switching the public feed.

Bundle validation uses the official .NET format 6 layout documented in
[Manifest.cs](https://github.com/dotnet/runtime/blob/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/Manifest.cs),
[FileEntry.cs](https://github.com/dotnet/runtime/blob/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/FileEntry.cs),
and [Bundler.cs](https://github.com/dotnet/runtime/blob/v10.0.0/src/installer/managed/Microsoft.NET.HostModel/Bundle/Bundler.cs).
It rejects compressed/unknown formats instead of guessing.
