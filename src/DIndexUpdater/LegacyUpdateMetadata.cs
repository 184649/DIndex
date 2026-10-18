using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DIndex.Updating;

namespace DIndexUpdater;

// A release updater may bridge the public v1.0.1 CLI, which did not pass a hash.
// Trust only metadata embedded by the release build after the final app ZIP exists.
// Never obtain the missing hash from the caller, a mutable feed, or a sidecar file.
internal sealed record LegacyUpdateMetadata(string Version, string DownloadUrl, string DownloadSha256)
{
    internal const string ResourceName = "DIndexUpdater.LegacyUpdate.json";
    private const int MaximumMetadataBytes = 16 * 1024;

    internal static LegacyUpdateMetadata LoadEmbedded()
    {
        using var stream = typeof(LegacyUpdateMetadata).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidDataException("This updater does not include a verified legacy update package. Update manually from the official release.");
        using var bytes = new MemoryStream();
        var buffer = new byte[1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (bytes.Length + read > MaximumMetadataBytes) throw new InvalidDataException("The embedded legacy update metadata is too large.");
            bytes.Write(buffer, 0, read);
        }
        return Parse(bytes.ToArray());
    }

    internal static LegacyUpdateMetadata Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumMetadataBytes) throw new InvalidDataException("The embedded legacy update metadata is too large.");
        ReadOnlyMemory<byte> json = bytes;
        if (bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf) json = json[3..];
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid embedded legacy update metadata.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Name is not ("version" or "downloadUrl" or "downloadSha256") ||
                    property.Value.ValueKind != JsonValueKind.String || !names.Add(property.Name))
                    throw new InvalidDataException("Invalid embedded legacy update metadata.");
            if (names.Count != 3) throw new InvalidDataException("Incomplete embedded legacy update metadata.");
            var metadata = new LegacyUpdateMetadata(document.RootElement.GetProperty("version").GetString()!,
                document.RootElement.GetProperty("downloadUrl").GetString()!, document.RootElement.GetProperty("downloadSha256").GetString()!);
            metadata.Validate();
            return metadata;
        }
        catch (JsonException ex) { throw new InvalidDataException("Invalid embedded legacy update metadata.", ex); }
    }

    private void Validate()
    {
        UpdatePackageSafety.ValidateHash(DownloadSha256);
        var uri = UpdatePackageSafety.ValidatePackageUrl(DownloadUrl);
        // Compatibility is release-specific; mutable raw/main URLs cannot enable it.
        if (uri.Host != "github.com" ||
            !(uri.AbsolutePath == $"/Chairman-bits/DIndex/releases/download/v{Version}/DIndex.zip" ||
              uri.AbsolutePath == $"/184649/DIndex/releases/download/v{Version}/DIndex.zip"))
            throw new InvalidDataException("The legacy package must be pinned to an official versioned release.");
        _ = ParseVersion(Version);
    }

    internal string GetVerifiedHash(string requestedUrl, string appPath, Func<string, Version>? versionReader = null)
    {
        Validate();
        if (!string.Equals(requestedUrl, DownloadUrl, StringComparison.Ordinal))
            throw new InvalidDataException("The requested legacy package does not exactly match this release updater.");
        var installed = (versionReader ?? ReadExecutableVersion)(appPath);
        if (ParseVersion(Version) <= installed)
            throw new InvalidDataException("The legacy update must be newer than the installed executable.");
        return DownloadSha256;
    }

    internal static Version ReadExecutableVersion(string appPath)
    {
        var version = FileVersionInfo.GetVersionInfo(appPath);
        if (version.FileVersion is null) throw new InvalidDataException("The installed executable version could not be verified.");
        return ParseVersion(version.FileVersion);
    }

    internal void ValidateExecutableVersion(string executable, Func<string, Version>? versionReader = null)
    {
        if ((versionReader ?? ReadExecutableVersion)(executable) != ParseVersion(Version))
            throw new InvalidDataException("The legacy package executable does not match the pinned release version.");
    }

    private static Version ParseVersion(string text)
    {
        if (!System.Version.TryParse(text, out var version) || version.Build < 0)
            throw new InvalidDataException("A numeric release version is required for a legacy update.");
        return new Version(version.Major, version.Minor, version.Build, Math.Max(0, version.Revision));
    }
}
