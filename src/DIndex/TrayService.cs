namespace DIndex;

public sealed class TrayService : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.ContextMenuStrip _menu;
    private readonly MainWindow _window;
    private readonly System.Drawing.Icon _trayIcon;
    private readonly System.Drawing.Image? _menuImage;
    private bool _disposed;

    public TrayService(MainWindow window)
    {
        _window = window;
        _trayIcon = IconHelper.LoadTrayIcon();
        _menuImage = IconHelper.LoadMenuImage(16);

        _menu = new System.Windows.Forms.ContextMenuStrip
        {
            ShowImageMargin = true,
            ShowCheckMargin = false
        };

        AddMenuItem("開く", (_, _) => _window.ShowFromTray(), withIcon: true);
        AddMenuItem("再索引", async (_, _) => await _window.RebuildIndexFromTrayAsync());
        AddMenuItem("更新確認", async (_, _) => await _window.CheckUpdateFromTrayAsync());
        AddMenuItem("設定ファイルを開く", (_, _) => _window.OpenSettingsFile());
        _menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        AddMenuItem("終了", (_, _) => _window.ExitApplication());

        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "DIndex",
            ContextMenuStrip = _menu,
            Visible = true
        };

        _notifyIcon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Left)
            {
                _window.ShowFromTray();
            }
        };

        // 右クリック時に確実に最新のメニューを持った状態で表示されるようにします。
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == System.Windows.Forms.MouseButtons.Right)
            {
                _notifyIcon.ContextMenuStrip = _menu;
            }
        };
    }

    private void AddMenuItem(string text, System.EventHandler onClick, bool withIcon = false)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text);
        if (withIcon && _menuImage is not null)
        {
            item.Image = _menuImage;
        }
        item.Click += onClick;
        _menu.Items.Add(item);
    }

    public void ShowBalloon(string title, string text, System.Windows.Forms.ToolTipIcon icon = System.Windows.Forms.ToolTipIcon.Info)
    {
        if (_disposed) return;
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.BalloonTipIcon = icon;
            _notifyIcon.ShowBalloonTip(3000);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _menuImage?.Dispose();
        _trayIcon.Dispose();
    }
}
