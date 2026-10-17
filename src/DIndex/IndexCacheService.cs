using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DIndex;

public sealed class IndexCacheService
{
    private const string Header = "#DIndexCache\t2";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string _cachePath;

    public IndexCacheService(string? cachePath = null) => _cachePath = Path.GetFullPath(cachePath ?? AppPaths.CachePath);
    public string? LastLoadWarning { get; private set; }

    public async Task SaveAsync(IEnumerable<SearchRecord> records, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();
        var gate = AtomicFileStorage.GateFor(_cachePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            temporary = AtomicFileStorage.CreateTemporaryPath(_cachePath);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
            {
                await using (var writer = new StreamWriter(stream, Utf8, 128 * 1024, leaveOpen: true) { NewLine = "\n" })
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    await writer.WriteLineAsync(Header.AsMemory(), cancellationToken).ConfigureAwait(false);
                    long count = 0;
                    foreach (var record in records)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ValidateRecord(record);
                        var line = string.Join('\t', Escape(record.FullPath), Escape(record.Name), Escape(record.Folder), Escape(record.Extension),
                            record.IsDirectory ? "1" : "0", record.SizeBytes.ToString(CultureInfo.InvariantCulture), record.UpdatedTicks.ToString(CultureInfo.InvariantCulture));
                        hash.AppendData(Utf8.GetBytes(line + "\n"));
                        await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                        count++;
                    }
                    var footer = $"#END\t{count.ToString(CultureInfo.InvariantCulture)}\t{Convert.ToHexString(hash.GetHashAndReset())}";
                    await writer.WriteLineAsync(footer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                stream.Flush(flushToDisk: true);
            }
            // No cancellation is observed after this commit point: success means the new snapshot is installed.
            cancellationToken.ThrowIfCancellationRequested();
            AtomicFileStorage.Commit(temporary, _cachePath);
        }
        finally
        {
            if (temporary is not null) AtomicFileStorage.DeleteTemporary(temporary);
            gate.Release();
        }
    }

    public async Task<List<SearchRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gate = AtomicFileStorage.GateFor(_cachePath);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastLoadWarning = null;
            Exception? primaryError = null;
            if (File.Exists(_cachePath))
            {
                try { return await ReadCompleteAsync(_cachePath, cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidDataException)
                {
                    primaryError = ex;
                }
            }
            if (File.Exists(_cachePath + ".bak"))
            {
                List<SearchRecord> recovered;
                try { recovered = await ReadCompleteAsync(_cachePath + ".bak", cancellationToken).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException or InvalidDataException)
                {
                    throw new InvalidDataException("索引キャッシュとバックアップを読み込めません。索引を再作成してください。", primaryError is null ? ex : new AggregateException(primaryError, ex));
                }
                cancellationToken.ThrowIfCancellationRequested();
                AtomicFileStorage.RestoreBackup(_cachePath);
                LastLoadWarning = "索引キャッシュをバックアップから復元しました。";
                return recovered;
            }
            if (primaryError is not null) throw new InvalidDataException("索引キャッシュが壊れています。索引を再作成してください。", primaryError);
            return new List<SearchRecord>();
        }
        finally { gate.Release(); }
    }

    private static async Task<List<SearchRecord>> ReadCompleteAsync(string path, CancellationToken cancellationToken)
    {
        var records = new List<SearchRecord>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 128 * 1024, true);
        using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, 128 * 1024);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var versioned = line == Header;
        if (versioned) line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        while (line is not null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (versioned && line.StartsWith("#END\t", StringComparison.Ordinal))
            {
                var footer = line.Split('\t');
                if (footer.Length != 3 || !long.TryParse(footer[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count != records.Count ||
                    !string.Equals(footer[2], Convert.ToHexString(hash.GetHashAndReset()), StringComparison.OrdinalIgnoreCase) ||
                    await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidDataException("索引キャッシュの整合性を確認できません。");
                return records;
            }
            if (versioned) hash.AppendData(Utf8.GetBytes(line + "\n"));
            // Legacy TSV is accepted strictly, then upgraded on the next successful save.
            var parts = line.Split('\t');
            if (parts.Length != 7 || (parts[4] != "0" && parts[4] != "1") ||
                !long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
                !long.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
                throw new InvalidDataException("索引キャッシュに不正なレコードがあります。");
            var fullPath = Unescape(parts[0]);
            var name = Unescape(parts[1]);
            var record = new SearchRecord
            {
                FullPath = fullPath, Name = name, Folder = Unescape(parts[2]), Extension = Unescape(parts[3]),
                IsDirectory = parts[4] == "1", SizeBytes = size, UpdatedTicks = ticks,
                NameLower = name.ToLowerInvariant(), FullPathLower = fullPath.ToLowerInvariant()
            };
            ValidateRecord(record);
            records.Add(record);
            line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (versioned) throw new InvalidDataException("索引キャッシュが途中で切れています。");
        return records;
    }

    private static void ValidateRecord(SearchRecord record)
    {
        if (record is null || string.IsNullOrEmpty(record.FullPath) || string.IsNullOrEmpty(record.Name) || record.Folder is null || record.Extension is null ||
            record.SizeBytes < 0 || record.UpdatedTicks < 0 || record.UpdatedTicks > DateTime.MaxValue.Ticks)
            throw new InvalidDataException("索引キャッシュに不正な値があります。");
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");

    private static string Unescape(string value)
    {
        var decoded = new StringBuilder(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\') { decoded.Append(value[i]); continue; }
            if (++i == value.Length) throw new InvalidDataException("索引キャッシュに不正なエスケープがあります。");
            decoded.Append(value[i] switch
            {
                '\\' => '\\', 't' => '\t', 'r' => '\r', 'n' => '\n',
                _ => throw new InvalidDataException("索引キャッシュに不正なエスケープがあります。")
            });
        }
        return decoded.ToString();
    }
}
