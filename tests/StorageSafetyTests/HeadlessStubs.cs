// Only presentation/log sinks are replaced. Tests link the actual production storage and updater source.
namespace DIndex
{
    internal static class ErrorLogger { public static void Write(System.Exception exception) { } }
}
namespace System.Windows
{
    internal enum MessageBoxButton { OK }
    internal enum MessageBoxImage { Error }
    internal static class MessageBox
    {
        internal static string? LastMessage { get; private set; }
        internal static void Show(string message, string caption, MessageBoxButton button, MessageBoxImage image) => LastMessage = message;
    }
}
