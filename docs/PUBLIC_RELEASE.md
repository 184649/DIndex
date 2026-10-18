# Public stable release and existing-user update

The public v1.0.1 executable reads `https://raw.githubusercontent.com/184649/DIndex/main/version.json`. The public `main` branch is a distribution repository, not the application source branch. Its five-file historical tree contains binaries and update metadata.

## Version 1.1.2

- Stable self-contained Windows x64 `DIndex.exe`, packaged as the sole root entry of `DIndex.zip`.
- Stable self-contained Windows x64 `DIndexUpdater.exe`, packaged as the sole root entry of `DIndexUpdater.zip`.
- Exact release URLs and SHA-256 digests in `version.json`. The updater additionally embeds the finalized application ZIP URL and hash for legacy callers that cannot supply a digest.
- `%LOCALAPPDATA%\DIndex` remains the normal profile. Existing settings/cache are not deleted or moved by the updater. Preview profiles remain separate.
- Close-to-tray no longer displays the redundant “running in tray” information balloon. Update/error notifications are retained.
- Neither executable is publisher-signed. SHA-256 detects package corruption or mismatch; it does not replace a publisher signature or protect against compromise of the trusted repository. Do not bypass operating-system security warnings.

## Legacy and current update paths

The actual public v1.0.1 EXE was statically inspected. Its updater arguments are `--processId`, `--appPath`, `--appZip` (URL), and `--restart`. It downloads a new updater on each requested update. The release updater accepts this legacy argument shape only when the URL matches its embedded release pin, and verifies the application ZIP against the embedded SHA-256 before installation.

The old application exits immediately after starting its updater, before package verification; this cannot be retroactively changed. Newer applications prepare and verify packages before the ready/proceed shutdown handshake. Both paths retain a backup and use the updater's safe replacement checks. Updates are initiated by the existing confirmation UI, not silently installed.

A preview build is not the public stable distribution. Its source kit/MSI and isolated data directory are not silently migrated to the stable profile. A public stable ZIP is not an MSI in-place update; MSI-managed installations remain excluded from the portable updater.

## Publication order

1. Validate source, updater legacy compatibility, data safety, Windows build and package contents.
2. Produce the stable app ZIP, compute its hash, then compile the updater with that exact release pin. Produce both package hashes only after final bytes exist.
3. Record the exact application source commit in release metadata and provide a dedicated source archive from that exact commit. The repository historically tracks generated bin/obj files and old root DIndex.zip/DIndexUpdater.zip; only these generated artifacts are excluded from the curated source archive and build snapshot. A companion source manifest records every included path/blob hash and every excluded path, so the archive is not presented as a raw full-commit archive. The release tag denotes a **distribution snapshot**, not the application source commit; GitHub's automatically generated tag archives are therefore not the application source archive.
4. Upload and verify all release assets. Publish only DIndex v1.1.2. No DHub release is included.
5. Before changing the discovery feed, run the published-asset legacy CLI upgrade test against a disposable copy of the actual old EXE, without starting that old WPF application or using a real profile.
6. Only after asset hashes and the upgrade test pass, update `main/version.json`. This is a distribution-file update, not a merge of application source into main.
7. Read back the public feed and verify its immutable URLs and hashes. Existing v1.0.1 clients can then discover the update through their existing check.

## Verification limits

The isolated legacy test does not invoke the old application's live update UI. It omits automatic restart when using the real legacy EXE as a fixture, because that old EXE ignores the test profile override and might access the runner's normal profile during failure recovery. The old argument parsing, restart semantics and recovery paths also have separate unit tests. A Windows CI result is not a human GUI acceptance test or a guarantee of every user's permissions, antivirus policy or filesystem behavior.

The close notification removal and previously improved initial result display are narrow application changes. MFT/USN indexing, Everything integration and “faster than Everything” claims are not part of this release.
