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
    private readonly DispatcherTimer _searchTimer;
    private AppSettings _settings;
    private TrayService? _trayService;
    private CancellationTokenSource? _indexCts;
    private CancellationTokenSource? _searchCts;
    private bool _allowClose;
    private bool _uiReady;
    private long _searchRequest;
    private long _rebuildRequest;
    private readonly DispatcherTimer _statusTimer;
    private string? _pendingStatus;
    private int _refreshPending;
    private readonly CancellationTokenSource _lifetime = new();

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
        ResultsGrid.ItemsSource = Array.Empty<SearchResultItem>();
        PathSearchCheckBox.IsChecked = _settings.SearchPath;
        IncludeDirectoriesCheckBox.IsChecked = _settings.IncludeDirectories;
        RefreshRootList();
        // Coalesce worker notifications: never block an indexing worker on the UI dispatcher.
        _indexService.CountChanged += count => Interlocked.Exchange(ref _pendingStatus, $"索引: {count:N0} 件");
        _indexService.StatusChanged += text => Interlocked.Exchange(ref _pendingStatus, text);
        _indexService.SnapshotChanged += _ => Interlocked.Exchange(ref _refreshPending, 1);
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _statusTimer.Tick += (_, _) =>
        {
            var status = Interlocked.Exchange(ref _pendingStatus, null);
            if (status is not null) StatusText.Text = status;
            // Finish the current captured snapshot before refreshing it. Continuous filesystem
            // writes must not starve a broad search; new user input still cancels immediately.
            if (_searchCts is null && Interlocked.Exchange(ref _refreshPending, 0) != 0) QueueSearch();
        };
        _statusTimer.Start();
        if (!string.IsNullOrWhiteSpace(_settingsRepository.LastLoadWarning))
            ShowPersistentWarning(_settingsRepository.LastLoadWarning);

        VersionText.Text = $"Version {_updateService.CurrentVersion}" + (_updateService.IsMsiInstalled ? " / MSI管理" : _updateService.IsPreviewBuild ? " / 開発ブランチ検証版" : " / GitHub更新対応");

        // 初回起動時はウィンドウを表示しないため、Loadedイベントは発火しない場合があります。
        // タスクトレイアイコンとバックグラウンド処理はコンストラクターで開始します。
        _trayService = new TrayService(this);
        _uiReady = true;

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
            if (!File.Exists(AppPaths.SettingsPath)) SaveSettings();
            Process.Start(new ProcessStartInfo { FileName = AppPaths.SettingsPath, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
        }
    }

    public void ExitApplication()
    {
        if (_allowClose) return;
        _allowClose = true;
        _lifetime.Cancel();
        _searchTimer.Stop();
        _statusTimer.Stop();
        _trayService?.Dispose();
        _indexCts?.Cancel();
        _searchCts?.Cancel();
        // UI changes are saved when made; never overwrite externally edited JSON on exit.
        _indexService.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private async Task LoadCacheAndStartIndexAsync()
    {
        if (_allowClose || _rebuildRequest != 0) return;
        try
        {
            IndexProgressBar.IsIndeterminate = true;
            StatusText.Text = "前回索引を読み込み中...";
            var startupRequest = _rebuildRequest;
            var indexRequestVersion = _indexService.RequestVersion;
            var cache = await Task.Run(() => _cacheService.LoadAsync(_lifetime.Token), _lifetime.Token);
            if (_allowClose || startupRequest != _rebuildRequest) return;
            if (cache.Count > 0)
            {
                if (!await _indexService.ReplaceAllAsync(cache, _settings, indexRequestVersion, _lifetime.Token)) return;
                if (_allowClose || startupRequest != _rebuildRequest) return;
                StatusText.Text = _cacheService.LastLoadWarning ?? $"前回索引を読み込みました: {_indexService.Count:N0} 件（再整合中）";
                if (_cacheService.LastLoadWarning is not null) ShowPersistentWarning(_cacheService.LastLoadWarning);
                QueueSearch();
            }
            await RebuildIndexAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
            ShowPersistentWarning($"前回索引を読み込めませんでした。再作成します: {ex.Message}");
            if (!_allowClose) await RebuildIndexAsync();
        }
        finally
        {
            if (_indexCts is null) IndexProgressBar.IsIndeterminate = false;
        }
    }

    private async Task RebuildIndexAsync()
    {
        if (_allowClose) return;
        var request = ++_rebuildRequest;
        _indexCts?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _indexCts = cts;
        var token = cts.Token;
        var settings = new AppSettings
        {
            SearchRoots = _settings.SearchRoots.ToList(),
            ExcludeFolderNames = _settings.ExcludeFolderNames.ToList(),
            IncludeDirectories = _settings.IncludeDirectories,
            SearchPath = _settings.SearchPath,
            ResultLimit = _settings.ResultLimit
        };

        try
        {
            IndexProgressBar.IsIndeterminate = true;
            StatusText.Text = "索引を作成中...";
            await _indexService.RebuildAsync(settings, (count, dir) =>
            {
                if (request == Interlocked.Read(ref _rebuildRequest))
                    Interlocked.Exchange(ref _pendingStatus, $"索引中: {count:N0} 件 / {dir}");
            }, token);
            token.ThrowIfCancellationRequested();
            if (request != _rebuildRequest) return;
            await Task.Run(() => _cacheService.SaveAsync(_indexService.Snapshot(), token), token);
            if (_allowClose || request != _rebuildRequest) return;
            token.ThrowIfCancellationRequested();
            Interlocked.Exchange(ref _pendingStatus, null);
            StatusText.Text = $"索引完了: {_indexService.Count:N0} 件";
            QueueSearch();
        }
        catch (OperationCanceledException)
        {
            if (request == _rebuildRequest && !_allowClose) StatusText.Text = "索引をキャンセルしました。前回の索引を保持しています。";
        }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
            if (request == _rebuildRequest && !_allowClose) StatusText.Text = $"索引／保存エラー: {ex.Message}";
        }
        finally
        {
            if (request == _rebuildRequest)
            {
                _indexCts = null;
                IndexProgressBar.IsIndeterminate = false;
            }
        }
    }

    private void QueueSearch()
    {
        // 起動直後やXAML初期化中のイベントでは、検索UIの準備が完了していない場合があります。
        // その場合は何もせず、Loaded後または次回入力時に検索します。
        if (!_uiReady || !IsInitialized || SearchBox is null || _allowClose) return;
        ++_searchRequest;
        _searchCts?.Cancel();

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async Task ExecuteSearchAsync()
    {
        if (_allowClose) return;
        var request = _searchRequest;
        _searchCts?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _searchCts = cts;
        var token = cts.Token;
        var snapshotVersion = _indexService.SnapshotVersion;
        var keyword = SearchBox.Text;
        var pathSearch = PathSearchCheckBox.IsChecked == true;
        var limit = Math.Clamp(_settings.ResultLimit, 1, 100000);

        try
        {
            var sw = Stopwatch.StartNew();
            var items = await Task.Run(() =>
            {
                var records = _indexService.Search(keyword, pathSearch, limit, token);
                return records.Select(record =>
                {
                    token.ThrowIfCancellationRequested();
                    return SearchResultItem.FromRecord(record);
                }).ToArray();
            }, token);
            token.ThrowIfCancellationRequested();
            if (_allowClose || request != _searchRequest) return;
            var snapshotAdvanced = snapshotVersion != _indexService.SnapshotVersion;
            if (snapshotAdvanced) Interlocked.Exchange(ref _refreshPending, 1);
            // One view reset rather than up to ResultLimit collection notifications.
            ResultsGrid.ItemsSource = items;
            sw.Stop();
            ResultCountText.Text = $"表示 {items.Length:N0} 件（上限 {limit:N0}）";
            Interlocked.Exchange(ref _pendingStatus, null);
            StatusText.Text = $"検索完了: {items.Length:N0} 件 / {sw.ElapsedMilliseconds} ms / 索引 {_indexService.Count:N0} 件" + (snapshotAdvanced ? " / 索引更新中（表示を再確認します）" : _indexService.IsDirty ? " / 要再整合" : string.Empty);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
            if (request == _searchRequest && !_allowClose) StatusText.Text = $"検索エラー: {ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) _searchCts = null;
        }
    }

    private async Task CheckUpdateSilentlyAsync()
    {
        try
        {
            await Task.Delay(1200, _lifetime.Token);
            if (!_allowClose) await CheckUpdateAsync(showLatestMessage: false);
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckUpdateAsync(bool showLatestMessage)
    {
        UpdateButton.IsEnabled = false;
        try
        {
            StatusText.Text = "更新確認中...";
            var result = await _updateService.CheckAsync(_lifetime.Token);
            StatusText.Text = result.Message;
            if (result.HasUpdate && result.Info is not null)
            {
                if (!showLatestMessage)
                {
                    _trayService?.ShowBalloon("DIndex", result.Message + " トレイの更新確認から開始できます。");
                    return;
                }
                var answer = System.Windows.MessageBox.Show($"{result.Message}\r\n今すぐ更新しますか？", "DIndex Update", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Information);
                if (answer == System.Windows.MessageBoxResult.Yes)
                {
                    var started = await _updateService.StartUpdateAsync(result.Info, _lifetime.Token);
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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
            if (!_allowClose) StatusText.Text = $"更新エラー: {ex.Message}";
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    private void ShowPersistentWarning(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        StartupWarningText.Text = string.IsNullOrWhiteSpace(StartupWarningText.Text)
            ? message : StartupWarningText.Text + "\n" + message;
        StartupWarningText.Visibility = System.Windows.Visibility.Visible;
    }

    private void SaveSettings()
    {
        try { _settingsRepository.Save(_settings); }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
            if (!_allowClose)
            {
                StatusText.Text = $"設定を保存できませんでした: {ex.Message}";
                System.Windows.MessageBox.Show($"設定の変更は今回の起動中だけ有効です。保存先の空き容量・権限を確認してください。\n{ex.Message}",
                    "DIndex 設定保存エラー", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
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
        TryShellAction(() => FileIndexService.Open(SelectedItem.FullPath));
    }

    private void OpenSelectedParent()
    {
        if (SelectedItem is null) return;
        TryShellAction(() => FileIndexService.OpenParent(SelectedItem.FullPath));
    }

    private void CopySelectedPath()
    {
        if (SelectedItem is null) return;
        TryShellAction(() => FileIndexService.CopyPath(SelectedItem.FullPath));
    }

    private void TryShellAction(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
            StatusText.Text = $"操作できませんでした: {ex.Message}";
        }
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
        if (!_uiReady || _settings is null) return;
        _settings.SearchPath = PathSearchCheckBox.IsChecked == true;
        SaveSettings();
        QueueSearch();
    }

    private void InvalidateResultsForScopeChange()
    {
        ++_searchRequest;
        _searchCts?.Cancel();
        _searchTimer.Stop();
        ResultsGrid.ItemsSource = Array.Empty<SearchResultItem>();
        ResultCountText.Text = "対象を変更しています...";
    }

    private async void IncludeDirectories_Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (!_uiReady || _settings is null) return;
        InvalidateResultsForScopeChange();
        _settings.IncludeDirectories = IncludeDirectoriesCheckBox.IsChecked == true;
        SaveSettings();
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
            InvalidateResultsForScopeChange();
            _settings.SearchRoots.Add(dialog.SelectedPath);
            SaveSettings();
            RefreshRootList();
            await RebuildIndexAsync();
        }
    }

    private async void RemoveRootButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (RootList.SelectedItem is not string selected) return;
        InvalidateResultsForScopeChange();
        _settings.SearchRoots.RemoveAll(x => string.Equals(x, selected, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
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

