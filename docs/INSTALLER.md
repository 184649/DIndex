# DIndex Preview: current-user MSI

## Scope and current validation status

This installer is for the separate **DIndex Preview** channel. It is not a production release and is not published by these scripts. Build output stays in a local output directory. The MSI is currently **unsigned**: Windows may show an unknown-publisher warning. Public distribution requires a separate signing, release and supported-Windows acceptance review.

The authoring has cross-platform contract tests. A successful source check is not a successful Windows build or installation test. Run the Windows integration tests below and retain their JSON results and MSI logs before handing an installer to a user. A standard, non-administrator Windows account and interactive wizard acceptance must also be checked; an elevated hosted-runner account is not evidence that this manual check passed.

## New users

1. Quit DIndex completely, including its notification-area icon if present. Setup conservatively detects any `DIndex.exe`, including a portable copy, and asks the user to close it. It never sends a close message, kills a process, or starts the app after installation.
2. Open the MSI as the intended Windows user, without “Run as administrator”. The destination is `%LocalAppData%\Programs\DIndex-Preview`.
3. Keep the required application feature selected. **Desktop shortcut** and **Start when you sign in** start deselected and are optional. A Start menu shortcut is always created.
4. Start **DIndex Preview** from the Start menu. The complete win-x64 .NET runtime and Windows Desktop runtime are bundled; users do not install .NET separately.

The installer rejects Windows 7/8/8.1 and builds below Windows 10 1607 (14393), using read-only OS registry detection rather than MSI’s compatibility-capped VersionNT. Windows 11 and Windows Server 2016 or later also pass this technical floor. This does not extend Microsoft’s OS lifecycle: use a currently supported edition and servicing version. As of the authoring review, Windows 10 support is limited to qualifying LTSC/Enterprise editions; see the current [official .NET Windows support table](https://learn.microsoft.com/en-us/dotnet/core/install/windows). This installer does not install a machine-wide runtime, service, scheduled task or machine-wide registry entry. The app and preview data are separate:

- Program: `%LocalAppData%\Programs\DIndex-Preview`
- Preview data: `%LocalAppData%\DIndex-Preview`
- Existing ordinary data: `%LocalAppData%\DIndex`, which setup does not touch

## Existing users and upgrades

- **Existing Preview MSI:** quit the app, then open a newer MSI. The same Preview UpgradeCode is retained. MSI removes the old version **inside its transaction**, installs the new version, migrates previously selected optional features, and rolls back to the old version if the new transaction fails.
- **Same installer/version:** open it again to change features, repair or uninstall. The ProductCode is deterministic for one three-part version. Never redistribute different application contents under an existing MSI version; increase the version first.
- **Older MSI:** installation is rejected when a newer version is present.
- **Existing portable/ordinary build:** setup leaves it and its data in place. It does not silently migrate, replace or delete that build. Preview starts with its own profile. Moving existing data is a separate, explicit backup/import decision, not an installer side effect.
- **Installed-mode updates:** `installed-by-msi.json` beside the EXE identifies `updateMode: "msi"`. The application/updater must refuse EXE-only replacement for this installation. Upgrade by MSI; do not copy a new EXE over MSI-owned files.

Windows Settings → Apps, or the MSI maintenance wizard, can uninstall the program. Uninstall removes only installer-owned files, shortcuts and HKCU registration. User data, backups and unrecognized files remain. No recursive AppData cleanup exists. Unknown files inside the program directory also remain, so the empty-folder cleanup can leave that directory behind.

For a full repair with logging:

```powershell
msiexec.exe /fa "DIndex-Preview-1.1.0-win-x64.msi" /norestart /l*v "$env:TEMP\DIndex-repair.log"
```

Quit the app before installation, repair, upgrade or uninstall. Silent operations fail with a log message if it is still running. Do not start the app while setup is active. Restart Manager automatic shutdown/restart is disabled; installation scripts do not force a reboot.

## Build locally on Windows

Prerequisites must already exist:

- PowerShell 7
- Official .NET SDK matching the target framework (normally .NET 10; .NET 8 is accepted for an explicit compatibility build)
- Official WiX **3.14.1.8722**, including `candle.exe`, `light.exe`, `WixUIExtension.dll` and `WixUtilExtension.dll`

The scripts detect these tools and fail if they are absent. WiX detection compares the exact reported `FileVersion` string `3.14.1.8722`, using one shared detector for the launcher and installer build. The official WiX native version resource encodes its numeric tuple differently (`3.14.8722.0`); it is logged for diagnosis, not misinterpreted as a different package version. Each candidate logs file, product and numeric versions, and the build manifest records all four tool/extension components. They never install an SDK, WiX or a third-party build tool. Ordinary NuGet restore is limited to the official NuGet source to retrieve Microsoft runtime dependencies. The preinstalled SDK builds a **self-contained .NET 10.0.13** payload by default (`8.0.32` for an explicitly selected .NET 8 target), independent of an older shared runtime on the build machine. Review and update those pins for future security servicing.

```powershell
$build = ./packaging/Build-Installer.ps1 `
    -TargetFramework net10.0-windows `
    -RuntimeFrameworkVersion 10.0.13 `
    -PackageVersion 1.1.0 `
    -OutputDirectory C:\InstallerOutput\DIndex
$build | ConvertTo-Json -Depth 5
Get-Item $build.msiPath
```

If `-TargetFramework` is omitted, the app project supplies it. `-WixBin` can point to the already-installed WiX `bin` directory. The script selects a preinstalled SDK of the correct major in an isolated SDK-resolution directory; it does not change `global.json` in the repository.

Every build creates a new `build-<GUID>` child. Existing output is never recursively deleted. Output includes:

- `DIndex-Preview-<version>-win-x64.msi`
- `build-manifest.json`: MSI SHA-256, identities, exact SDK/WiX/runtime versions, runtime SHA-256, payload count and unsigned status
- `payload/`: the complete published application, MSI marker, runtime licenses/notices and WiX notice
- `third-party/`: matching unmodified WiX source archive, its MS-RL license and attribution notice
- WiX authoring/intermediates for inspection

The WiX source archive is read-only documentation, never compiled, installed or executed. It is downloaded from the official repository at commit `b40e9a32c24033e11b77baf2c91a704382f898ed` (WiX 3.14.1). To avoid that download, supply a previously saved archive using `-WixSourceArchive C:\Sources\wix3-b40e9a32c24033e11b77baf2c91a704382f898ed.zip`. The source root/license and exact tool version are checked. Keep `third-party/` together with the MSI if it is later distributed; source URL and archive SHA-256 are recorded in the manifest. Runtime notices are copied from the exact restored Microsoft packages. Missing required Core notices or either runtime license stops the build; if the Desktop package has no separate third-party notice, that absence is explicitly reported. No placeholder EULA or new license-acceptance checkbox is displayed.

Do not use a fixture MSI as a deliverable. The build produces a separate random identity when `-FixtureId` is provided; that argument is for the test script only.

## MSI validation and the narrow ICE91 exception

All WiX/MSI ICE validation remains enabled. Candle treats every warning as an error. Light captures its complete output in `light-validation.log`; nonzero exit status or any warning outside the following exact allowlist fails the build:

- `warning LGHT1076 : ICE91: The file ... will be installed to the per user directory ...` with the documented ALLUSERS/per-machine warning text

This is not a blanket LGHT1076 or ICE91 suppression. Even another ICE91 message category is rejected. Microsoft [documents ICE91 as harmless for a package used exclusively per-user](https://learn.microsoft.com/en-us/windows/win32/msi/ice91). Before accepting it, `Assert-MsiPolicy.ps1` examines the actual linked MSI and requires:

- Explicit `perUser`/`limited` authoring and the linked package’s no-elevation summary flag
- No authored `ALLUSERS` or `MSIINSTALLPERUSER` value, with launch conditions rejecting either override
- A private, mixed-case `AppInstallFolder` directory fixed beneath the current user’s `LocalAppDataFolder/Programs`; no public destination directory properties
- Every component beneath that application directory or the current user’s Start menu/Desktop/Startup directory, plus HKCU-only registry writes

The ordinary command-line `INSTALLFOLDER=...` property cannot redirect the private directory. Altering a package using an external MSI transform is outside the supported installer contract. The manifest stores the mechanically checked policy, the exact allowlist, the number of accepted warnings and the full validation-log path. A nonzero accepted warning count must not be described as “zero warnings”. Both initial Windows linking attempts reported only this expected file warning (478 occurrences); later builds record their own counts.

## Automated Windows integration tests

Only run in a disposable VM or CI runner account. The test script deliberately changes that account’s MSI registration and creates temporary shortcuts. It refuses to proceed without the explicit switch below. It never uses the ordinary or Preview product UpgradeCode, install directory or profile, and never launches the real application.

```powershell
python ./packaging/verify_source.py
./packaging/Test-Installer.ps1 `
    -TargetFramework net10.0-windows `
    -RuntimeFrameworkVersion 10.0.13 `
    -OutputDirectory C:\InstallerOutput\DIndexTests `
    -AllowDisposableUserProfile
```

Tests build synthetic versions `1.1.0`, `1.1.1`, `1.1.2` with one random fixture UpgradeCode and verify:

1. Per-user/limited-privilege/x64 MSI metadata, optional-feature defaults and transaction sequencing; actual `ALLUSERS=1` and `MSIINSTALLPERUSER=1` rejection before creating files or registration
2. Running-process rejection before install, repair, upgrade and uninstall, without terminating the process
3. Clean install, complete bundled runtime, Start menu target and Windows Installer current-user unmanaged registration with the expected installed version
4. No desktop/startup shortcut without opt-in; explicit opt-in and feature retention on upgrade
5. Same-version repair after deleting a fixture DLL
6. Major upgrade and removal of old product registration
7. Rejection of a downgrade
8. Failed-upgrade rollback to the old marker, payload hash, registration and shortcuts
9. Uninstall removal of managed resources, while data sentinels outside and inside the installation folder survive

Only fixture builds include WiX’s `WixFailWhenDeferred` failure-injection action; it is excluded from ordinary MSI builds. Process checks use a tiny self-contained helper with the same EXE basename, which exits on its own after 45 seconds. There is no process kill. These tests do not prove that every real-app exit/save path works; that belongs in interactive acceptance.

Product registration is queried through Windows Installer's supported `ProductsEx` API rather than assuming a particular uninstall registry path. Each installed fixture must have exactly one current-user unmanaged registration (context `2`), matching the current SID, `State=5` and `VersionString`; managed-user (`1`) and machine (`4`) registrations must be absent. The same API checks old/failed/uninstalled product absence. Query errors fail the test rather than being treated as absence. Context, state and version observations are logged and retained in the test-results manifest. These API checks do not replace the interactive Apps UI check below.

Each MSI operation has a verbose log. `test-results-<GUID>.json` records passed checks or the first failure, plus whether the executing Windows token is an administrator. An administrator-run test explicitly records that real standard-user installation has not been established. Unexpected MSI exit codes print at most 80 relevant failure-log lines to CI, while preserving the complete local log. Fixture products are uninstalled in `finally`; any cleanup failure is reported. Test output and data sentinels are left for inspection and eventual disposable-environment removal. No upload, Release, signing, CI dispatch or user-computer installation is performed by either script.

## Interactive acceptance before distribution

Use a clean, supported Windows x64 VM with a standard user account and no separately installed .NET runtime. Check the wizard and maintenance UI, Windows Apps registration, first launch, loading/saving the isolated Preview profile, explicit app exit, optional shortcuts and startup behavior. Repeat the MSI upgrade/rollback/data-retention cases. Verify the shipping MSI has no fixture failure action and that its included runtime version matches the reviewed security-serviced version. Code-signing, publisher reputation and a reliable distribution channel remain separate release prerequisites.

## Design references

- [Windows Installer ProductsEx enumeration](https://learn.microsoft.com/en-us/windows/win32/msi/installer-productsex)
- [Product installation context](https://learn.microsoft.com/en-us/windows/win32/msi/product-context)
- [Context-aware installation properties](https://learn.microsoft.com/en-us/windows/win32/msi/product-installproperty)
- [Official SDK property literals, including State and VersionString](https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/um/Msi.h)

- [WiX 3 major-upgrade transaction scheduling](https://docs.firegiant.com/wix3/xsd/wix/majorupgrade/)
- [WiX detect-only CloseApplication options](https://docs.firegiant.com/wix3/xsd/util/closeapplication/)
- [Microsoft Restart Manager control](https://learn.microsoft.com/en-us/windows/win32/msi/msirestartmanagercontrol)
- [WiX source license for the pinned commit](https://github.com/wixtoolset/wix3/blob/b40e9a32c24033e11b77baf2c91a704382f898ed/LICENSE.TXT)
