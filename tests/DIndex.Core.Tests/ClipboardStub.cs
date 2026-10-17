// Only clipboard interaction is stubbed. Search, traversal and watchers are production code.
namespace System.Windows;
internal static class Clipboard
{
    public static void SetText(string text) => throw new PlatformNotSupportedException("Clipboard is outside these portable core tests.");
}
