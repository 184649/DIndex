using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace DIndex;

public sealed class AppSettings
{
    public List<string> SearchRoots { get; set; } = new();
    public bool SearchPath { get; set; } = true;
    public bool IncludeDirectories { get; set; } = true;
    public int ResultLimit { get; set; } = 500;
    public List<string> ExcludeFolderNames { get; set; } = new() { "$Recycle.Bin", "System Volume Information", "node_modules", ".git", "bin", "obj" };
}

public sealed class SearchRecord
{
    public string FullPath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Folder { get; init; } = string.Empty;
    public string Extension { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long SizeBytes { get; init; }
    public long UpdatedTicks { get; init; }
    public string NameLower { get; init; } = string.Empty;
    public string FullPathLower { get; init; } = string.Empty;

    [JsonIgnore]
    public DateTime Updated => UpdatedTicks <= 0 ? DateTime.MinValue : new DateTime(UpdatedTicks);

    public static SearchRecord FromFile(string path)
    {
        var info = new FileInfo(path);
        return new SearchRecord
        {
            FullPath = info.FullName,
            Name = info.Name,
            Folder = info.DirectoryName ?? string.Empty,
            Extension = string.IsNullOrWhiteSpace(info.Extension) ? "file" : info.Extension.TrimStart('.'),
            IsDirectory = false,
            SizeBytes = info.Exists ? info.Length : 0,
            UpdatedTicks = info.Exists ? info.LastWriteTime.Ticks : 0,
            NameLower = info.Name.ToLowerInvariant(),
            FullPathLower = info.FullName.ToLowerInvariant()
        };
    }

    public static SearchRecord FromDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        return new SearchRecord
        {
            FullPath = info.FullName,
            Name = info.Name,
            Folder = info.Parent?.FullName ?? string.Empty,
            Extension = "folder",
            IsDirectory = true,
            SizeBytes = 0,
            UpdatedTicks = info.Exists ? info.LastWriteTime.Ticks : 0,
            NameLower = info.Name.ToLowerInvariant(),
            FullPathLower = info.FullName.ToLowerInvariant()
        };
    }
}

public sealed class SearchResultItem : INotifyPropertyChanged
{
    private string _icon = string.Empty;
    private string _name = string.Empty;
    private string _folder = string.Empty;
    private string _type = string.Empty;
    private string _size = string.Empty;
    private string _updated = string.Empty;
    private string _fullPath = string.Empty;

    public string Icon { get => _icon; set => SetField(ref _icon, value); }
    public string Name { get => _name; set => SetField(ref _name, value); }
    public string Folder { get => _folder; set => SetField(ref _folder, value); }
    public string Type { get => _type; set => SetField(ref _type, value); }
    public string Size { get => _size; set => SetField(ref _size, value); }
    public string Updated { get => _updated; set => SetField(ref _updated, value); }
    public string FullPath { get => _fullPath; set => SetField(ref _fullPath, value); }

    public static SearchResultItem FromRecord(SearchRecord record)
    {
        return new SearchResultItem
        {
            Icon = record.IsDirectory ? "📁" : "📄",
            Name = record.Name,
            Folder = record.Folder,
            Type = record.IsDirectory ? "フォルダ" : record.Extension,
            Size = record.IsDirectory ? "-" : FormatSize(record.SizeBytes),
            Updated = record.Updated == DateTime.MinValue ? "-" : record.Updated.ToString("yyyy/MM/dd HH:mm"),
            FullPath = record.FullPath
        };
    }

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.##} {units[unit]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class UpdateInfo
{
    public string Version { get; set; } = string.Empty;
    public string DownloadUrl { get; set; } = string.Empty;
    public string UpdaterUrl { get; set; } = string.Empty;
    public string ReleaseNotes { get; set; } = string.Empty;
}
