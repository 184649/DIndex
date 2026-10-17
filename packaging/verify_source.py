"""Fast cross-platform authoring checks; not a replacement for Windows MSI tests."""
import json
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

    def test_limited_per_user_only(self):
        package = self.product.find("w:Product/w:Package", NS)
        self.assertEqual(package.get("InstallScope"), "perUser")
        self.assertEqual(package.get("InstallPrivileges"), "limited")
        self.assertEqual(package.get("Platform"), "x64")
        for value in self.product.findall(".//w:RegistryValue", NS):
            self.assertEqual(value.get("Root"), "HKCU")
        self.assertIsNotNone(self.product.find(".//w:Directory[@Id='LocalAppDataFolder']/w:Directory[@Name='Programs']/w:Directory[@Id='INSTALLFOLDER']", NS))

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
