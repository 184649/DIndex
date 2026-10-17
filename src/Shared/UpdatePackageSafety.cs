using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace DIndex.Updating;

/// <summary>Accept only the official repository, validate complete packages before executing, and never extract arbitrary ZIP paths.</summary>
internal static class UpdatePackageSafety
{
    internal const long MaximumPackageBytes = 512L * 1024 * 1024;
    internal const long MaximumExecutableBytes = 1024L * 1024 * 1024;
    private static readonly HttpClient Http = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };

    internal static Uri ValidatePackageUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidDataException("更新 URL は公式リポジトリの HTTPS URL である必要があります。");
        var officialRaw = uri.Host.Equals("raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase) &&
            (uri.AbsolutePath.StartsWith("/Chairman-bits/DIndex/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/184649/DIndex/", StringComparison.Ordinal));
        var officialRelease = uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
            (uri.AbsolutePath.StartsWith("/Chairman-bits/DIndex/releases/download/", StringComparison.Ordinal) || uri.AbsolutePath.StartsWith("/184649/DIndex/releases/download/", StringComparison.Ordinal));
        if ((!officialRaw && !officialRelease) || !uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新 URL が DIndex の公式リポジトリではありません。");
        return uri;
    }

    internal static void ValidateHash(string expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256) || expectedSha256.Length != 64 || !IsHex(expectedSha256))
            throw new InvalidDataException("更新情報に SHA-256 がありません。自動更新は中止しました。公式リポジトリから手動で更新するか、新しいリリースをお待ちください。");
    }

    private static bool IsHex(string value)
    {
        foreach (var character in value)
            if (!(character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F')) return false;
        return true;
    }

    internal static async Task DownloadVerifiedAsync(string url, string destination, string expectedSha256, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        cancellationToken = timeout.Token;
        var uri = ValidatePackageUrl(url);
        ValidateHash(expectedSha256);
        cancellationToken.ThrowIfCancellationRequested();
        using var response = await GetResponseAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is long length && (length <= 0 || length > MaximumPackageBytes))
            throw new InvalidDataException("更新パッケージのサイズが不正です。");
        var ownsDestination = false;
        try
        {
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true);
            ownsDestination = true;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long total = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                total += read;
                if (total > MaximumPackageBytes) throw new InvalidDataException("更新パッケージが大きすぎます。");
                hash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            if (total == 0 || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(expectedSha256)))
                throw new InvalidDataException("更新パッケージの SHA-256 が一致しません。ファイルは使用されません。");
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
        }
        catch
        {
            if (ownsDestination) TryDelete(destination);
            throw;
        }
    }

    private static async Task<HttpResponseMessage> GetResponseAsync(Uri uri, CancellationToken cancellationToken)
    {
        for (var redirects = 0; redirects <= 5; redirects++)
        {
            var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))
                return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null) throw new InvalidDataException("更新 URL のリダイレクト先がありません。");
            uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
            // GitHub release assets use signed query strings on this exact host.
            if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || !string.IsNullOrEmpty(uri.UserInfo) ||
                !(uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                  uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("更新 URL のリダイレクト先を確認できません。");
        }
        throw new InvalidDataException("更新 URL のリダイレクト回数が多すぎます。");
    }

    internal static async Task VerifyFileHashAsync(string path, string expectedSha256, CancellationToken cancellationToken)
    {
        ValidateHash(expectedSha256);
        await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        if (input.Length <= 0 || input.Length > MaximumPackageBytes) throw new InvalidDataException("更新パッケージのサイズが不正です。");
        var actual = await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException("更新パッケージの SHA-256 が一致しません。");
    }

    internal static async Task<string> ExtractExecutableAsync(string zipPath, string directory, string expectedName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var zip = ZipFile.OpenRead(zipPath);
        if (zip.Entries.Count is < 1 or > 1024) throw new InvalidDataException("更新 ZIP の内容数が不正です。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ZipArchiveEntry? executable = null;
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = entry.FullName.Replace('\\', '/');
            var isDirectory = name.EndsWith("/", StringComparison.Ordinal);
            var normalized = isDirectory ? name[..^1] : name;
            if (string.IsNullOrEmpty(normalized) || name.StartsWith("/", StringComparison.Ordinal) || name.Contains(':') || name.Contains('\0') || !names.Add(normalized))
                throw new InvalidDataException("更新 ZIP に安全でないパスがあります。");
            foreach (var part in normalized.Split('/'))
                if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0)
                    throw new InvalidDataException("更新 ZIP に安全でないパスがあります。");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new InvalidDataException("更新 ZIP にリンクが含まれています。");
            total = checked(total + entry.Length);
            if (total > MaximumExecutableBytes || entry.Length > MaximumExecutableBytes)
                throw new InvalidDataException("更新 ZIP の展開サイズが大きすぎます。");
            if (isDirectory)
            {
                if (entry.Length != 0) throw new InvalidDataException("更新 ZIP のフォルダが不正です。");
                continue;
            }
            // Releases are single-file applications; refuse mixed or ambiguous packages.
            if (!Path.GetFileName(normalized).Equals(expectedName, StringComparison.OrdinalIgnoreCase) || executable is not null || entry.Length < 64)
                throw new InvalidDataException($"更新 ZIP には {expectedName} が 1 個だけ必要です。");
            executable = entry;
        }
        if (executable is null) throw new InvalidDataException($"更新 ZIP に {expectedName} がありません。");
        Directory.CreateDirectory(directory);
        var outputPath = Path.Combine(directory, expectedName);
        var ownsOutput = false;
        try
        {
            await using (var input = executable.Open())
            await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                ownsOutput = true;
                var buffer = new byte[128 * 1024];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    written += read;
                    if (written > executable.Length || written > MaximumExecutableBytes) throw new InvalidDataException("更新 ZIP のサイズが一致しません。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                }
                if (written != executable.Length) throw new InvalidDataException("更新 ZIP が途中で切れています。");
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            ValidatePortableExecutable(outputPath);
            return outputPath;
        }
        catch
        {
            if (ownsOutput) TryDelete(outputPath);
            throw;
        }
    }

    internal static void ValidatePortableExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("更新ファイルは Windows 実行ファイルではありません。");
        stream.Position = 0x3c;
        var offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 26) throw new InvalidDataException("更新ファイルの PE ヘッダーが不正です。");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("更新ファイルの PE ヘッダーが不正です。");
        var machine = reader.ReadUInt16();
        if (machine is not (0x8664 or 0xAA64 or 0x14c)) throw new InvalidDataException("更新ファイルの CPU 種別が不正です。");
        stream.Position = offset + 24;
        if (reader.ReadUInt16() is not (0x10b or 0x20b)) throw new InvalidDataException("更新ファイルの PE 形式が不正です。");
    }

    internal static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
