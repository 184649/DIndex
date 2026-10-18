"""Cross-platform source contracts; Windows tray/UI acceptance remains separate."""
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]


class WindowCloseContract(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.window = (ROOT / "src/DIndex/MainWindow.xaml.cs").read_text(encoding="utf-8-sig")
        cls.tray = (ROOT / "src/DIndex/TrayService.cs").read_text(encoding="utf-8-sig")

    def test_close_handler_remains_connected(self):
        window = ET.parse(ROOT / "src/DIndex/MainWindow.xaml").getroot()
        self.assertEqual(window.get("Closing"), "Window_Closing")

    def test_close_only_cancels_and_hides_unless_exiting(self):
        # Pin this deliberately small handler: no balloon, dialog, or shutdown.
        # Whitespace is ignored; an intentional lifecycle change must update this contract.
        handler = re.search(
            r"private void Window_Closing\([^)]*\)\s*\{([^{}]*)\}", self.window
        )
        self.assertIsNotNone(handler, "Expected the close-to-tray event handler")
        self.assertEqual(
            re.sub(r"\s+", "", handler.group(1)),
            "if(_allowClose)return;e.Cancel=true;Hide();",
        )

    def test_tray_restore_and_explicit_exit_remain_available(self):
        self.assertIn('AddMenuItem("開く", (_, _) => _window.ShowFromTray()', self.tray)
        self.assertIn("_notifyIcon.MouseDoubleClick +=", self.tray)
        self.assertIn('AddMenuItem("終了", (_, _) => _window.ExitApplication());', self.tray)
        self.assertIn("_allowClose = true;", self.window)
        self.assertIn("_trayService?.Dispose();", self.window)
        self.assertIn("System.Windows.Application.Current.Shutdown();", self.window)

    def test_update_notifications_and_balloon_service_are_preserved(self):
        self.assertIn(
            '_trayService?.ShowBalloon("DIndex", result.Message + " トレイの更新確認から開始できます。");',
            self.window,
        )
        self.assertIn('_trayService?.ShowBalloon("DIndex", result.Message);', self.window)
        self.assertIn("_notifyIcon.ShowBalloonTip(3000);", self.tray)


if __name__ == "__main__":
    unittest.main(verbosity=2)
