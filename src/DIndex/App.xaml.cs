using System.IO;
using System.Windows.Threading;

namespace DIndex;

public partial class App : System.Windows.Application
{
    private MainWindow? _mainWindow;

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        base.OnStartup(e);

        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
        Directory.CreateDirectory(AppPaths.AppDataDirectory);

        _mainWindow = new MainWindow();
        MainWindow = _mainWindow;
        _mainWindow.Hide();
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
