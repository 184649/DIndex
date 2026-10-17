using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Threading;

namespace DIndex;

public partial class App : System.Windows.Application
{
    private MainWindow? _mainWindow;
    private Mutex? _instanceMutex;
    private bool _ownsMutex;
    private FileStream? _profileLock;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        try
        {
        base.OnStartup(e);

        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        var profileId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(AppPaths.AppDataDirectory).ToUpperInvariant())));
        _instanceMutex = new Mutex(false, "Local\\DIndex-" + profileId);
        try { _ownsMutex = _instanceMutex.WaitOne(0); }
        catch (AbandonedMutexException) { _ownsMutex = true; }
        if (!_ownsMutex)
        {
            System.Windows.MessageBox.Show("このデータフォルダーを使うDIndexは既に起動しています。タスクトレイから開いてください。", "DIndex");
            Shutdown();
            return;
        }
        Directory.CreateDirectory(AppPaths.AppDataDirectory);
        // A file lock also covers path aliases and multiple Windows sessions.
        _profileLock = new FileStream(Path.Combine(AppPaths.AppDataDirectory, ".instance.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Hide();
        }
        catch (Exception ex)
        {
            ErrorLogger.Write(ex);
            try
            {
                System.Windows.MessageBox.Show($"DIndexを起動できませんでした。保存先・権限・同じデータを使う別プロセスを確認してください。\n{ex.GetBaseException().Message}",
                    "DIndex 起動エラー", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally { Shutdown(1); }
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        _profileLock?.Dispose();
        if (_ownsMutex) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLogger.Write(e.Exception);
        System.Windows.MessageBox.Show(e.Exception.ToString(), "DIndex Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ErrorLogger.Write(ex);
        }
    }
}

internal static class ErrorLogger
{
    public static void Write(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppDataDirectory);
            File.AppendAllText(AppPaths.ErrorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\r\n{ex}\r\n\r\n");
        }
        catch
        {
        }
    }
}

