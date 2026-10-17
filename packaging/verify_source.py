"""Fast cross-platform authoring checks; not a replacement for Windows MSI tests."""
import json
import re
from pathlib import Path
import unittest
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent
NS = {"w": "http://schemas.microsoft.com/wix/2006/wi",
      "u": "http://schemas.microsoft.com/wix/UtilExtension"}


class PackagingContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.product = ET.parse(ROOT / "Product.wxs").getroot()
        cls.ui = ET.parse(ROOT / "PreviewUI.wxs").getroot()
        cls.config = json.loads((ROOT / "Product.json").read_text())
        cls.build = (ROOT / "Build-Installer.ps1").read_text()
        cls.tests = (ROOT / "Test-Installer.ps1").read_text()
        cls.registration = (ROOT / "MsiRegistration.ps1").read_text()
        cls.preflight = (ROOT / "Test-MsiRegistrationApi.ps1").read_text()

    def test_limited_per_user_only(self):
        package = self.product.find("w:Product/w:Package", NS)
        self.assertEqual(package.get("InstallScope"), "perUser")
        self.assertEqual(package.get("InstallPrivileges"), "limited")
        self.assertEqual(package.get("Platform"), "x64")
        for value in self.product.findall(".//w:RegistryValue", NS):
            self.assertEqual(value.get("Root"), "HKCU")
        self.assertIsNotNone(self.product.find(".//w:Directory[@Id='LocalAppDataFolder']/w:Directory[@Name='Programs']/w:Directory[@Id='AppInstallFolder']", NS))

    def test_transactional_upgrade_and_downgrade(self):
        upgrade = self.product.find("w:Product/w:MajorUpgrade", NS)
        self.assertEqual(upgrade.get("Schedule"), "afterInstallInitialize")
        self.assertEqual(upgrade.get("AllowDowngrades"), "no")
        self.assertEqual(upgrade.get("AllowSameVersionUpgrades"), "no")
        self.assertTrue(upgrade.get("DowngradeErrorMessage"))
        self.assertIn('Get-StableGuid "$upgradeCode/product/$PackageVersion"', self.build)

    def test_real_os_version_not_compatibility_version(self):
        search = self.product.find(".//w:RegistrySearch[@Id='FindWindowsMajor']", NS)
        self.assertEqual(search.get("Name"), "CurrentMajorVersionNumber")
        self.assertEqual(search.get("Type"), "raw")
        self.assertEqual(search.get("Win64"), "yes")
        source = (ROOT / "Product.wxs").read_text()
        self.assertIn('WINDOWS_MAJOR = "#10" AND WINDOWS_BUILD &gt;= 14393', source)
        self.assertNotIn('VersionNT &gt;= 1000', source)

    def test_only_opt_in_shortcuts(self):
        core = self.product.find(".//w:Feature[@Id='Core']", NS)
        self.assertEqual(core.get("Absent"), "disallow")
        self.assertEqual(core.get("Level"), "1")
        for feature in ("DesktopShortcutFeature", "StartupShortcutFeature"):
            self.assertEqual(self.product.find(f".//w:Feature[@Id='{feature}']", NS).get("Level"), "2")
        self.assertIsNotNone(core.find("w:ComponentRef[@Id='StartMenuShortcut']", NS))
        self.assertNotIn("LicenseAgreement", (ROOT / "PreviewUI.wxs").read_text())
        self.assertIsNone(self.ui.find(".//w:Publish[@Dialog='FeaturesDlg'][@Control='Next']", NS))

    def test_running_app_detection_never_terminates(self):
        detector = self.product.find(".//u:CloseApplication", NS)
        for attribute in ("CloseMessage", "EndSessionMessage", "ElevatedCloseMessage", "ElevatedEndSessionMessage", "RebootPrompt", "PromptToContinue"):
            self.assertEqual(detector.get(attribute), "no")
        self.assertIsNone(detector.get("TerminateProcess"))
        self.assertEqual(detector.get("Property"), "APP_RUNNING")
        self.assertGreaterEqual(int(detector.get("Timeout", "5")), 1)
        self.assertEqual(self.product.find(".//w:Property[@Id='MSIRESTARTMANAGERCONTROL']", NS).get("Value"), "DisableShutdown")
        self.assertNotIn("Stop-Process", self.tests)
        self.assertNotIn("taskkill", self.tests.lower())

    def test_no_wildcard_data_deletion(self):
        self.assertFalse(self.product.findall(".//w:RemoveFile", NS))
        self.assertNotIn("RemoveFolderEx", self.build)
        self.assertNotIn("Remove-Item -Recurse", self.build)
        self.assertNotIn("Remove-Item -Recurse", self.tests)

    def test_full_runtime_and_msi_marker(self):
        for value in ("'--self-contained', 'true'", "'-p:PublishSingleFile=false'", "'-p:PublishTrimmed=false'", "installed-by-msi.json", "updateMode = 'msi'", "'coreclr.dll'", "'PresentationFramework.dll'", "includedFrameworks", "RuntimeFrameworkVersion"):
            self.assertIn(value, self.build)
        self.assertIn("'10.0.13'", self.build)

    def test_official_tool_detection_and_notice_bundle(self):
        self.assertIn("Find-WixToolset.ps1", self.build)
        self.assertIn("Find-PreinstalledWix -WixBin $WixBin", self.build)
        detector = (ROOT / "Find-WixToolset.ps1").read_text()
        self.assertIn("3.14.1.8722", detector)
        self.assertNotIn("$version.FileBuildPart -ne 1", detector)
        self.assertIn("numericFileVersion", self.build)
        self.assertIn("b40e9a32c24033e11b77baf2c91a704382f898ed", self.build)
        self.assertIn("-p:RestoreSources=https://api.nuget.org/v3/index.json", self.build)
        self.assertIn("WiX-LICENSE.TXT", self.build)
        self.assertIn("Third-party", self.build.replace("third-party", "Third-party"))
        for forbidden in ("dotnet-install", "dotnet tool install", "choco install", "winget install", "-sval", "-sice"):
            self.assertNotIn(forbidden, self.build.lower())

    def test_validation_retains_only_conditional_ice91_exception(self):
        policy = (ROOT / "Assert-MsiPolicy.ps1").read_text()
        self.assertIn("Assert-PerUserMsiPolicy -MsiPath $msi", self.build)
        self.assertIn("Get-AllowedWixWarnings -Lines $lightOutput", self.build)
        self.assertIn("validationWarnings = $validationWarnings", self.build)
        self.assertIn("NOT ALLUSERS", policy)
        self.assertIn("NOT MSIINSTALLPERUSER", policy)
        self.assertIn("Public destination directory is forbidden", policy)
        self.assertIn("outside the permitted per-user directories", policy)
        self.assertIn("Unapproved WiX warning", policy)
        self.assertIn("LGHT1076 : ICE91: The file", policy)
        self.assertIn("testUserIsAdministrator", self.tests)
        self.assertIn("standardUserInstallEstablished", self.tests)

    def test_com_queries_emit_only_rows_or_scalar_values(self):
        policy = (ROOT / "Assert-MsiPolicy.ps1").read_text()
        for script in (policy, self.tests):
            self.assertIn("$null = $view.Execute()", script)
            self.assertIn("$null = $view.Close()", script)
            self.assertNotRegex(script, r"(?m)^\s*\$view\.(?:Execute|Close)\(\)")
        self.assertIn("[pscustomobject]@{ Values = $values }", policy)

    def test_warning_allowlist_rejects_unrelated_diagnostics(self):
        policy = (ROOT / "Assert-MsiPolicy.ps1").read_text()
        pattern = re.search(r'if \(\$line -notmatch "(.*)"\) \{', policy).group(1)
        warning = ("Product.wxs(1) : warning LGHT1076 : ICE91: The file 'f123' will be "
                   "installed to the per user directory 'AppInstallFolder' that doesn't "
                   "vary based on ALLUSERS value. This file won't be copied to each "
                   "user's profile even if a per machine installation is desired.")
        self.assertRegex(warning, pattern)
        self.assertRegex(warning.replace("doesn't", "does not"), pattern)
        for bad in (warning.replace("ICE91", "ICE64"), warning.replace("LGHT1076", "LGHT9999"),
                    warning.replace("The file", "The shortcut"), warning + " Extra diagnostic.",
                    "arbitrary warning text"):
            self.assertNotRegex(bad, pattern)

    def test_registration_uses_supported_context_api(self):
        self.assertNotIn("CurrentVersion\\Uninstall", self.tests)
        for required in ("ProductsEx($ProductCode, $querySid, $context)", "@(2, 1, 4)",
                         "$currentUserSid = $identity.User.Value", "InstallProperty('State')",
                         "InstallProperty('VersionString')", "$registration.userSid -eq $currentUserSid",
                         "$registration.state -eq '5'", "$registration.version -eq $Build.packageVersion",
                         "Assert-ProductRegistration $v1 $false", "Assert-ProductRegistration $v3 $false",
                         "Assert-ProductRegistration $v2 $false", "registrationChecks = $registrationChecks",
                         "ALLUSERS=1", "MSIINSTALLPERUSER=1"):
            self.assertIn(required, self.tests + self.registration)
        self.assertNotIn("InstallProperty('ProductState')", self.registration)

    def test_registration_api_preflight_precedes_builds(self):
        self.assertIn("$querySid = if ($context -eq 4) { [string]::Empty }", self.registration)
        self.assertNotIn("$querySid = if ($context -eq 4) { $null }", self.registration)
        preflight = self.tests.index("Assert-ProductRegistration $preflight $false")
        build = self.tests.index("$v1 = & (Join-Path $PSScriptRoot 'Build-Installer.ps1')")
        self.assertLess(preflight, build)
        self.assertIn("productCode = [guid]::NewGuid().ToString('B').ToUpperInvariant()", self.tests)

    def test_scope_probe_checks_effective_context_not_ignored_property_rejection(self):
        policy = (ROOT / "Assert-MsiPolicy.ps1").read_text()
        self.assertNotIn("rejectsMsiInstallPerUserOverride", policy)
        self.assertIn("msiInstallPerUserBehavior", policy)
        self.assertIn("Invoke-Msi 'reject-allusers'", self.tests)
        self.assertIn("Invoke-Msi 'msiinstallperuser-fixed-scope'", self.tests)
        self.assertIn("Assert-ProductRegistration $v1 $false 'Scope probe uninstall'", self.tests)
        probe = self.tests[self.tests.index("Invoke-Msi 'msiinstallperuser-fixed-scope'"):self.tests.index("# The guard fixture")]
        self.assertIn("Assert-Installed $v1", probe)
        self.assertIn("-not (Test-Path $installPath)", probe)
        self.assertIn("Assert-Sentinel", probe)

    def test_com_product_identity_uses_explicit_property_dispatch(self):
        for name in ("ProductCode", "Context", "UserSid"):
            self.assertIn(f"InvokeMember('{name}', $getProperty", self.registration)
            self.assertNotIn(f"$product.{name}", self.registration)
        self.assertIn("[Reflection.BindingFlags]::GetProperty", self.registration)
        self.assertIn("$exception.InnerException", self.registration)
        self.assertIn("HRESULT=0x", self.registration)
        self.assertIn("$productIdentity = Get-MsiProductIdentity $product", self.registration)
        preflight = self.tests.index("Assert-MsiProductIdentityApi $preflight.productCode")
        build = self.tests.index("$v1 = & (Join-Path $PSScriptRoot 'Build-Installer.ps1')")
        self.assertLess(preflight, build)

    def test_shared_read_only_api_probe_and_staged_fixture_builds(self):
        for script in (self.tests, self.preflight):
            self.assertIn("'MsiRegistration.ps1'", script)
        self.assertIn("Assert-MsiProductIdentityApi $productCode $currentUserSid", self.preflight)
        self.assertIn("Get-MsiProductRegistrations $productCode $currentUserSid", self.preflight)
        for forbidden in ("msiexec", "Build-Installer", "Set-ItemProperty", "Set-Content"):
            self.assertNotIn(forbidden, self.preflight)
        repair = self.tests.index("Record-Pass 'Same-version repair restores deleted payload'")
        v2 = self.tests.index("$v2 = & (Join-Path $PSScriptRoot 'Build-Installer.ps1')")
        self.assertLess(repair, v2)
        self.assertNotIn("$v2", self.tests[:v2])
        self.assertNotIn("$v3", self.tests[:v2])

    def test_fixture_isolation_and_coverage(self):
        uuid.UUID(self.config["upgradeCode"])
        self.assertTrue(self.config["installFolder"].endswith("-Preview"))
        self.assertIn("-MsiFixture-", self.build)
        self.assertIn("AllowDisposableUserProfile", self.tests)
        for scenario in ("running-app-blocked", "running-repair-blocked", "running-upgrade-blocked", "running-uninstall-blocked", "install-v1", "repair-v1", "upgrade-v2", "reject-downgrade", "rollback-failed-upgrade", "uninstall-v2", "WIXFAILWHENDEFERRED=1", "Assert-Sentinel"):
            self.assertIn(scenario, self.tests)
        self.assertIn('<?if $(var.FixtureBuild) = 1 ?>', (ROOT / "Product.wxs").read_text())
        self.assertIn("Write-MsiFailureDiagnostics $log", self.tests)
        self.assertIn("Select-Object -Last 80", self.tests)


if __name__ == "__main__":
    unittest.main(verbosity=2)
