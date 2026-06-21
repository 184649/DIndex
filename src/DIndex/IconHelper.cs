using System.Drawing;
using System.IO;
using System.Reflection;

namespace DIndex;

public static class IconHelper
{
    public static Icon LoadTrayIcon()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("DIndex.AppIcon");
        if (stream is not null)
        {
            using var icon = new Icon(stream);
            return (Icon)icon.Clone();
        }

        var path = Path.Combine(System.AppContext.BaseDirectory, "Assets", "app.ico");
        return File.Exists(path) ? new Icon(path) : SystemIcons.Application;
    }

    public static Image? LoadMenuImage(int size)
    {
        try
        {
            using var icon = LoadTrayIcon();
            using var bitmap = icon.ToBitmap();
            return new Bitmap(bitmap, new Size(size, size));
        }
        catch
        {
            return null;
        }
    }
}
