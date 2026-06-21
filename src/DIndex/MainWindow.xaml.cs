using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DIndex;

public partial class MainWindow : System.Windows.Window
{
    private readonly SettingsRepository _settingsRepository = new();
    private readonly IndexCacheService _cacheService = new();
    private readonly FileIndexService _indexService = new();
    private readonly UpdateService _updateService = new();
    private readonly ObservableCollection<SearchResultItem> _results = new();
    private readonly DispatcherTimer _searchTimer;
    private AppSettings _settings;
    private TrayService? _trayService;
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _searchCts;
    private bool _allowClose;

    public MainWindow()
    {
        // InitializeComponent() 中に CheckBox の Checked/Unchecked イベントが発火する場合があるため、
        // QueueSearch() で使用する検索タイマーは必ず先に初期化します。
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await ExecuteSearchAsync();
        };

        InitializeComponent();

        _settings = _settingsRepository.Load();
        ResultsGrid.ItemsSource = _results;
        PathSearchCheckBox.IsChecked = _settings.SearchPath;
        IncludeDirectoriesCheckBox.IsChecked = _settings.IncludeDirectories;
        RefreshRootList();
        _indexService.CountChanged += count => Dispatcher.Invoke(() => StatusText.Text = $"索引: {count:N0} 件");
        _indexService.StatusChanged += text => Dispatcher.Invoke(() => StatusText.Text = text);

        VersionText.Text = $"Version {_updateService.CurrentVersion} / GitHub main 自動アップデート対応";

        // 初回起動時はウィンドウを表示しないため、Loadedイベントは発火しない場合があります。
        // タスクトレイアイコンとバックグラウンド処理はコンストラクターで開始します。
        _trayService = new TrayService(this);

        Dispatcher.BeginInvoke(async () =>
        {
            await LoadCacheAndStartIndexAsync();
            _ = CheckUpdateSilentlyAsync();
        }, DispatcherPriority.ApplicationIdle);
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = System.Windows.WindowState.Normal;
        Activate();
        SearchBox.Focus();
    }

    public async Task RebuildIndexFromTrayAsync() => await RebuildIndexAsync();
    public async Task CheckUpdateFromTrayAsync() => await CheckUpdateAsync(showLatestMessage: true);

    public void OpenSettingsFile()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDirectory);
            if (!File.Exists(AppPaths.SettingsPath)) _settingsRepository.Save(_settings);
            Process.Start(new ProcessStartInfo { FileName = AppPaths.SettingsPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
        }
    }

    public void ExitApplication()
    {
        _allowClose = true;
        _trayService?.Dispose();
        _indexCts?.Cancel();
        _searchCts?.Cancel();
        _settingsRepository.Save(_settings);
        System.Windows.Application.Current.Shutdown();
    }

    private async Task LoadCacheAndStartIndexAsync()
    {
        try
        {
            IndexProgressBar.IsIndeterminate = true;
            StatusText.Text = "前回索引を読み込み中...";
            var cache = await _cacheService.LoadAsync(CancellationToken.None);
            if (cache.Count > 0)
            {
                _indexService.ReplaceAll(cache);
                StatusText.Text = $"前回索引を読み込みました: {cache.Count:N0} 件";
                QueueSearch();
            }
            await RebuildIndexAsync();
        }
        finally
        {
            IndexProgressBar.IsIndeterminate = false;
        }
    }

    private async Task RebuildIndexAsync()
    {
        _indexCts?.Cancel();
        _indexCts = new CancellationTokenSource();
        var token = _indexCts.Token;

        try
        {
            IndexProgressBar.IsIndeterminate = true;
            StatusText.Text = "索引を作成中...";
            await _indexService.RebuildAsync(_settings, (count, dir) => Dispatcher.Invoke(() => StatusText.Text = $"索引中: {count:N0} 件 / {dir}"), token);
            await _cacheService.SaveAsync(_indexService.Snapshot(), token);
            StatusText.Text = $"索引完了: {_indexService.Count:N0} 件";
            QueueSearch();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "索引をキャンセルしました。";
        }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
            StatusText.Text = $"索引エラー: {ex.Message}";
        }
        finally
        {
            IndexProgressBar.IsIndeterminate = false;
        }
    }

    private void QueueSearch()
    {
        // 起動直後やXAML初期化中のイベントでは、検索UIの準備が完了していない場合があります。
        // その場合は何もせず、Loaded後または次回入力時に検索します。
        if (!IsInitialized || SearchBox is null) return;

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async Task ExecuteSearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        var keyword = SearchBox.Text;
        var pathSearch = PathSearchCheckBox.IsChecked == true;
        var limit = Math.Max(50, _settings.ResultLimit);

        try
        {
            var sw = Stopwatch.StartNew();
            var records = await Task.Run(() => _indexService.Search(keyword, pathSearch, limit, token), token);
            token.ThrowIfCancellationRequested();

            _results.Clear();
            foreach (var record in records)
            {
                _results.Add(SearchResultItem.FromRecord(record));
            }
            sw.Stop();
            ResultCountText.Text = $"{records.Count:N0} 件";
            StatusText.Text = $"検索完了: {records.Count:N0} 件 / {sw.ElapsedMilliseconds} ms / 索引 {_indexService.Count:N0} 件";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
            StatusText.Text = $"検索エラー: {ex.Message}";
        }
    }

    private async Task CheckUpdateSilentlyAsync()
    {
        await Task.Delay(1200);
        await CheckUpdateAsync(showLatestMessage: false);
    }

    private async Task CheckUpdateAsync(bool showLatestMessage)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            StatusText.Text = "更新確認中...";
            var result = await _updateService.CheckAsync(CancellationToken.None);
            StatusText.Text = result.Message;
            if (result.HasUpdate && result.Info is not null)
            {
                var answer = System.Windows.MessageBox.Show($"{result.Message}\r\n今すぐ更新しますか？", "DIndex Update", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
                if (answer == System.Windows.MessageBoxResult.Yes)
                {
                    var started = await _updateService.StartUpdateAsync(result.Info, CancellationToken.None);
                    if (started) ExitApplication();
                }
                else
                {
                    _trayService?.ShowBalloon("DIndex", result.Message);
                }
            }
            else if (showLatestMessage)
            {
                System.Windows.MessageBox.Show(result.Message, "DIndex", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private void RefreshRootList()
    {
        RootList.ItemsSource = null;
        RootList.ItemsSource = _settings.SearchRoots;
    }

    private SearchResultItem? SelectedItem => ResultsGrid.SelectedItem as SearchResultItem;

    private void OpenSelected()
    {
        if (SelectedItem is null) return;
        FileIndexService.Open(SelectedItem.FullPath);
    }

    private void OpenSelectedParent()
    {
        if (SelectedItem is null) return;
        FileIndexService.OpenParent(SelectedItem.FullPath);
    }

    private void CopySelectedPath()
    {
        if (SelectedItem is null) return;
        FileIndexService.CopyPath(SelectedItem.FullPath);
        StatusText.Text = "パスをコピーしました。";
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => QueueSearch();

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == System.Windows.Input.ModifierKeys.Control)
        {
            OpenSelectedParent();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter)
        {
            OpenSelected();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            SearchBox.Clear();
            e.Handled = true;
        }
    }

    private void SearchOption_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.SearchPath = PathSearchCheckBox.IsChecked == true;
        _settingsRepository.Save(_settings);
        QueueSearch();
    }

    private async void IncludeDirectories_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.IncludeDirectories = IncludeDirectoriesCheckBox.IsChecked == true;
        _settingsRepository.Save(_settings);
        await RebuildIndexAsync();
    }

    private async void AddRootButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "DIndexで検索するフォルダを選択してください。",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = false
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
        if (!_settings.SearchRoots.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
        {
            _settings.SearchRoots.Add(dialog.SelectedPath);
            _settingsRepository.Save(_settings);
            RefreshRootList();
            await RebuildIndexAsync();
        }
    }

    private async void RemoveRootButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (RootList.SelectedItem is not string selected) return;
        _settings.SearchRoots.RemoveAll(x => string.Equals(x, selected, StringComparison.OrdinalIgnoreCase));
        _settingsRepository.Save(_settings);
        RefreshRootList();
        await RebuildIndexAsync();
    }

    private void OpenSettingsButton_Click(object sender, System.Windows.RoutedEventArgs e) => OpenSettingsFile();
    private async void UpdateButton_Click(object sender, System.Windows.RoutedEventArgs e) => await CheckUpdateAsync(showLatestMessage: true);
    private async void RefreshButton_Click(object sender, System.Windows.RoutedEventArgs e) => await RebuildIndexAsync();
    private void HideButton_Click(object sender, System.Windows.RoutedEventArgs e) => Hide();
    private void ResultsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenSelected();
    private void OpenSelectedMenu_Click(object sender, System.Windows.RoutedEventArgs e) => OpenSelected();
    private void OpenParentSelectedMenu_Click(object sender, System.Windows.RoutedEventArgs e) => OpenSelectedParent();
    private void CopyPathSelectedMenu_Click(object sender, System.Windows.RoutedEventArgs e) => CopySelectedPath();

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        Hide();
        _trayService?.ShowBalloon("DIndex", "DIndexはタスクトレイで動作中です。終了する場合はトレイメニューの「終了」を選択してください。");
    }
}
