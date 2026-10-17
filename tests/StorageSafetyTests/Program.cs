using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DIndex;
using DIndex.Updating;
using Updater = DIndexUpdater.Program;

namespace StorageSafetyTests;

internal static class Program
{
    private static readonly List<object> Results = new();
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "DIndex-StorageSafety-" + Guid.NewGuid().ToString("N"));
    private static int _failed;
    private static int _passed;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--exit-immediately") return 0;
        Directory.CreateDirectory(Root);
        // No production profile is used, even by tests which exercise default constructors.
        Environment.SetEnvironmentVariable("DINDEX_DATA_DIR", Path.Combine(Root, "profile"));
        await CacheTests();
        await SettingsTests();
        await SettingsSchemaTests();
        await PackageTests();
        await UpdaterTests();
        await InstalledModeTests();
        Console.WriteLine($"Storage safety: {_passed} passed, {_failed} failed.");
        if (args.Length > 0) File.WriteAllText(Path.GetFullPath(args[0]), JsonSerializer.Serialize(new { passed = _passed, failed = _failed, results = Results }, new JsonSerializerOptions { WriteIndented = true }));
        try { Directory.Delete(Root, true); } catch (IOException) { }
        return _failed == 0 ? 0 : 1;
    }

    private static async Task Test(string name, Func<Task> action)
    {
        try
        {
            await action();
            _passed++;
            Results.Add(new { name, passed = true });
            Console.WriteLine("PASS " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Results.Add(new { name, passed = false, error = ex.ToString() });
            Console.WriteLine("FAIL " + name + ": " + ex);
        }
    }

    private static string Fixture(string name)
    {
        var path = Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static SearchRecord Record(string name, long size = 123) => new()
    {
        FullPath = "C:\\notes\\" + name, Name = name, Folder = "C:\\notes", Extension = "txt", SizeBytes = size, UpdatedTicks = 638000000000000000,
        NameLower = name.ToLowerInvariant(), FullPathLower = ("C:\\notes\\" + name).ToLowerInvariant()
    };

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected [{expected}], actual [{actual}].");
    }
    private static void True(bool value, string message) { if (!value) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static Task Run(Action action) { action(); return Task.CompletedTask; }

    private static async Task CacheTests()
    {
        foreach (var name in new[] { @"notes\report.txt", @"nested\new\text.txt", @"literal\n\r\t.txt", "日本語 résumé.txt", "actual\tTAB\rCR\nNL.txt", @"\\server\new\tab.txt", @"trailing\" })
        {
            await Test("Cache round trip: " + name.Replace("\n", "<LF>").Replace("\r", "<CR>").Replace("\t", "<TAB>"), async () =>
            {
                var service = new IndexCacheService(Path.Combine(Fixture("roundtrip"), "cache.tsv"));
                var record = Record(name);
                await service.SaveAsync(new[] { record }, CancellationToken.None);
                var loaded = (await service.LoadAsync(CancellationToken.None)).Single();
                Equal(record.FullPath, loaded.FullPath); Equal(record.Name, loaded.Name); Equal(record.Folder, loaded.Folder);
                Equal(record.Extension, loaded.Extension); Equal(record.SizeBytes, loaded.SizeBytes); Equal(record.UpdatedTicks, loaded.UpdatedTicks);
            });
        }
        await Test("Cache empty snapshot has valid complete footer", async () =>
        {
            var path = Path.Combine(Fixture("empty"), "cache.tsv");
            var service = new IndexCacheService(path);
            await service.SaveAsync(Array.Empty<SearchRecord>(), CancellationToken.None);
            Equal(0, (await service.LoadAsync(CancellationToken.None)).Count);
            True(File.ReadAllText(path).Contains("#END\t0\t"), "Missing empty snapshot footer.");
        });
        await Test("Cache pre-canceled save/load preserve original bytes", async () =>
        {
            var path = Path.Combine(Fixture("pre-cancel"), "cache.tsv");
            var service = new IndexCacheService(path);
            await service.SaveAsync(new[] { Record("old") }, CancellationToken.None);
            var before = File.ReadAllBytes(path);
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Throws<OperationCanceledException>(() => service.SaveAsync(new[] { Record("new") }, cts.Token));
            await Throws<OperationCanceledException>(() => service.LoadAsync(cts.Token));
            True(before.SequenceEqual(File.ReadAllBytes(path)), "Canceled save changed the cache.");
        });
        await Test("Cache mid-save cancellation preserves original and cleans temporary", async () =>
        {
            var directory = Fixture("mid-cancel"); var path = Path.Combine(directory, "cache.tsv");
            var service = new IndexCacheService(path);
            await service.SaveAsync(new[] { Record("old") }, CancellationToken.None);
            var before = File.ReadAllBytes(path); using var cts = new CancellationTokenSource();
            IEnumerable<SearchRecord> CanceledRecords() { yield return Record("new"); cts.Cancel(); yield return Record("never"); }
            await Throws<OperationCanceledException>(() => service.SaveAsync(CanceledRecords(), cts.Token));
            True(before.SequenceEqual(File.ReadAllBytes(path)), "Mid-save cancellation changed the cache.");
            Equal(0, Directory.GetFiles(directory, "*.tmp").Length);
        });
        await Test("Cache enumeration failure surfaces and preserves original", async () =>
        {
            var path = Path.Combine(Fixture("enumeration"), "cache.tsv"); var service = new IndexCacheService(path);
            await service.SaveAsync(new[] { Record("old") }, CancellationToken.None); var before = File.ReadAllBytes(path);
            IEnumerable<SearchRecord> Broken() { yield return Record("new"); throw new IOException("Injected enumeration failure."); }
            await Throws<IOException>(() => service.SaveAsync(Broken(), CancellationToken.None));
            True(before.SequenceEqual(File.ReadAllBytes(path)), "Enumeration failure changed the cache.");
        });
        await Test("Cache invalid record surfaces and preserves original", async () =>
        {
            var path = Path.Combine(Fixture("bad-record"), "cache.tsv"); var service = new IndexCacheService(path);
            await service.SaveAsync(new[] { Record("old") }, CancellationToken.None); var before = File.ReadAllBytes(path);
            await Throws<InvalidDataException>(() => service.SaveAsync(new[] { Record("bad", -1) }, CancellationToken.None));
            True(before.SequenceEqual(File.ReadAllBytes(path)), "Validation failure changed the cache.");
        });
        await Test("Cache write failure is surfaced", async () =>
        {
            var path = Path.Combine(Fixture("write-failure"), "cache.tsv"); Directory.CreateDirectory(path);
            var marker = Path.Combine(path, "original.txt"); File.WriteAllText(marker, "retain");
            await Throws<IOException>(() => new IndexCacheService(path).SaveAsync(new[] { Record("new") }, CancellationToken.None));
            Equal("retain", File.ReadAllText(marker));
        });
        var invalidRows = new[] { "invalid", "C:\\\\file\tfile\tC:\\\\\tfile\t0\tbad\t0", "C:\\\\file\tfile\tC:\\\\\tfile\t2\t1\t0", "C:\\\\file\tfile\tC:\\\\\tfile\t0\t1\t9999999999999999999", "bad\\q\tfile\tfolder\tfile\t0\t1\t0" };
        for (var i = 0; i < invalidRows.Length; i++)
        {
            var row = invalidRows[i];
            await Test("Cache corrupt legacy record rejects entire load " + i, async () =>
            {
                var path = Path.Combine(Fixture("bad-load"), "cache.tsv");
                File.WriteAllText(path, "C:\\\\notes\\\\a\ta\tC:\\\\notes\ttxt\t0\t1\t0\n" + row + "\n");
                await Throws<InvalidDataException>(() => new IndexCacheService(path).LoadAsync(CancellationToken.None));
            });
        }
        await Test("Cache legacy Windows path migration preserves backslashes", async () =>
        {
            var path = Path.Combine(Fixture("legacy"), "cache.tsv");
            File.WriteAllText(path, "C:\\\\notes\\\\text.txt\ttext.txt\tC:\\\\notes\ttxt\t0\t1\t0\n");
            var service = new IndexCacheService(path); var records = await service.LoadAsync(CancellationToken.None);
            Equal(@"C:\notes\text.txt", records.Single().FullPath);
            await service.SaveAsync(records, CancellationToken.None);
            True(File.ReadAllText(path).StartsWith("#DIndexCache\t2"), "Legacy cache was not upgraded.");
        });
        foreach (var corruption in new[] { "truncated", "hash", "count", "trailing", "utf8" })
        {
            await Test("Cache rejects " + corruption, async () =>
            {
                var path = Path.Combine(Fixture(corruption), "cache.tsv"); var service = new IndexCacheService(path);
                await service.SaveAsync(new[] { Record("good") }, CancellationToken.None);
                var lines = File.ReadAllLines(path);
                if (corruption == "truncated") File.WriteAllLines(path, lines.Take(lines.Length - 1));
                if (corruption == "hash") File.WriteAllText(path, File.ReadAllText(path).Replace("good", "evil"));
                if (corruption == "count") File.WriteAllText(path, File.ReadAllText(path).Replace("#END\t1", "#END\t2"));
                if (corruption == "trailing") File.AppendAllText(path, "unexpected\n");
                if (corruption == "utf8") File.WriteAllBytes(path, new byte[] { 0xff, 0xfe, 0xff });
                await Throws<InvalidDataException>(() => service.LoadAsync(CancellationToken.None));
            });
        }
        foreach (var missing in new[] { false, true })
        {
            await Test("Cache restores complete backup when primary " + (missing ? "missing" : "corrupt"), async () =>
            {
                var path = Path.Combine(Fixture("backup"), "cache.tsv"); var service = new IndexCacheService(path);
                await service.SaveAsync(new[] { Record("old") }, CancellationToken.None);
                await service.SaveAsync(new[] { Record("new") }, CancellationToken.None);
                if (missing) File.Delete(path); else File.WriteAllText(path, "broken");
                Equal("old", (await service.LoadAsync(CancellationToken.None)).Single().Name);
                True(service.LastLoadWarning is not null, "Recovery warning missing.");
                Equal("old", (await new IndexCacheService(path).LoadAsync(CancellationToken.None)).Single().Name);
            });
        }
        await Test("Cache corrupt primary and backup never partially load", async () =>
        {
            var path = Path.Combine(Fixture("both-bad"), "cache.tsv"); File.WriteAllText(path, "bad"); File.WriteAllText(path + ".bak", "bad");
            await Throws<InvalidDataException>(() => new IndexCacheService(path).LoadAsync(CancellationToken.None));
        });
        await Test("Cache concurrent saves across service instances stay complete", async () =>
        {
            var path = Path.Combine(Fixture("concurrent"), "cache.tsv");
            await Task.WhenAll(Enumerable.Range(0, 24).Select(index => new IndexCacheService(path).SaveAsync(Enumerable.Range(0, 100).Select(n => Record(index + "-" + n)), CancellationToken.None)));
            var loaded = await new IndexCacheService(path).LoadAsync(CancellationToken.None);
            Equal(100, loaded.Count); Equal(1, loaded.Select(r => r.Name.Split('-')[0]).Distinct().Count());
            True(File.Exists(path + ".bak"), "Previous snapshot missing.");
        });
        await Test("Cache numeric values are culture-independent", async () =>
        {
            var prior = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
                var service = new IndexCacheService(Path.Combine(Fixture("culture"), "cache.tsv"));
                await service.SaveAsync(new[] { Record("number", 123456789) }, CancellationToken.None);
                Equal(123456789L, (await service.LoadAsync(CancellationToken.None)).Single().SizeBytes);
            }
            finally { CultureInfo.CurrentCulture = prior; }
        });
    }

    private static async Task SettingsTests()
    {
        await Test("Settings atomic save, backup and intentional empty roots", () => Run(() =>
        {
            var path = Path.Combine(Fixture("settings"), "settings.json"); var repository = new SettingsRepository(path);
            repository.Save(new AppSettings { SearchRoots = new() { @"C:\notes" }, ResultLimit = 200 });
            repository.Save(new AppSettings { SearchRoots = new(), ResultLimit = 300 });
            Equal(0, repository.Load().SearchRoots.Count); Equal(300, repository.Load().ResultLimit);
            Equal(200, JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path + ".bak"))!.ResultLimit);
        }));
        foreach (var missing in new[] { false, true })
            await Test("Settings backup recovery " + missing, () => Run(() =>
            {
                var path = Path.Combine(Fixture("settings-recovery"), "settings.json"); var repository = new SettingsRepository(path);
                repository.Save(new AppSettings { ResultLimit = 201 }); repository.Save(new AppSettings { ResultLimit = 301 });
                if (missing) File.Delete(path); else File.WriteAllText(path, "broken");
                Equal(201, repository.Load().ResultLimit); True(repository.LastLoadWarning is not null, "No settings recovery warning.");
                Equal(201, new SettingsRepository(path).Load().ResultLimit);
            }));
        await Test("Settings corrupt originals archived before later preference save", () => Run(() =>
        {
            var directory = Fixture("settings-archive"); var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "primary broken"); File.WriteAllText(path + ".bak", "backup broken");
            var repository = new SettingsRepository(path); var defaults = repository.Load();
            True(repository.LastLoadWarning is not null, "No fallback warning."); Equal("primary broken", File.ReadAllText(path));
            repository.Save(defaults); True(Directory.GetFiles(directory, "*.corrupt-*").Any(p => File.ReadAllText(p) == "primary broken"), "Corrupt primary was lost.");
            repository.Save(defaults); True(Directory.GetFiles(directory, "*.corrupt-*").Any(p => File.ReadAllText(p) == "backup broken"), "Corrupt backup was lost.");
        }));
        foreach (var invalid in new[] { "null", "{\"SearchRoots\":null}", "{\"ExcludeFolderNames\":null}", "{\"ResultLimit\":-1}", "{\"SearchRoots\":[null]}" })
            await Test("Settings validation rejects " + invalid, () => Run(() =>
            {
                var path = Path.Combine(Fixture("settings-invalid"), "settings.json"); File.WriteAllText(path, invalid);
                var repository = new SettingsRepository(path); _ = repository.Load(); True(repository.LastLoadWarning is not null, "Invalid settings were accepted.");
                Equal(invalid, File.ReadAllText(path));
            }));
        await Test("Settings save validation preserves original", async () =>
        {
            var path = Path.Combine(Fixture("settings-bad-save"), "settings.json"); var repository = new SettingsRepository(path);
            repository.Save(new AppSettings()); var original = File.ReadAllText(path);
            await Throws<InvalidDataException>(() => Run(() => repository.Save(new AppSettings { ResultLimit = 0 })));
            Equal(original, File.ReadAllText(path));
        });
        await Test("Settings invalid files do not silently enable whole-profile scanning", () => Run(() =>
        {
            var path = Path.Combine(Fixture("settings-no-scan"), "settings.json"); File.WriteAllText(path, "broken");
            Equal(0, new SettingsRepository(path).Load().SearchRoots.Count);
        }));
        await Test("Settings backup recovery preserves original corrupt bytes", () => Run(() =>
        {
            var directory = Fixture("settings-preserve-recovery"); var path = Path.Combine(directory, "settings.json");
            var repository = new SettingsRepository(path); repository.Save(new AppSettings()); repository.Save(new AppSettings { ResultLimit = 123 });
            File.WriteAllText(path, "corrupt but recoverable by user"); _ = repository.Load();
            True(Directory.GetFiles(directory, "*.corrupt-*").Any(p => File.ReadAllText(p) == "corrupt but recoverable by user"), "Recovery discarded corrupt original.");
        }));
        await Test("Settings external edits after load are preserved", async () =>
        {
            var path = Path.Combine(Fixture("settings-external"), "settings.json");
            new SettingsRepository(path).Save(new AppSettings { ResultLimit = 100 });
            var repository = new SettingsRepository(path); var loaded = repository.Load();
            var external = "{\"ResultLimit\":321,\"SearchRoots\":[]}"; File.WriteAllText(path, external);
            loaded.ResultLimit = 200;
            await Throws<InvalidOperationException>(() => Run(() => repository.Save(loaded)));
            Equal(external, File.ReadAllText(path));
            Equal(321, repository.Load().ResultLimit);
            repository.Save(new AppSettings { ResultLimit = 322 }); Equal(322, repository.Load().ResultLimit);
        });
        await Test("Settings external edits after save are preserved", async () =>
        {
            var path = Path.Combine(Fixture("settings-external-save"), "settings.json"); var repository = new SettingsRepository(path);
            repository.Save(new AppSettings()); File.WriteAllText(path, "externally edited, temporarily invalid");
            await Throws<InvalidOperationException>(() => Run(() => repository.Save(new AppSettings())));
            Equal("externally edited, temporarily invalid", File.ReadAllText(path));
        });
        await Test("Settings external deletion is not silently recreated", async () =>
        {
            var path = Path.Combine(Fixture("settings-deleted"), "settings.json"); var repository = new SettingsRepository(path);
            repository.Save(new AppSettings()); File.Delete(path);
            await Throws<InvalidOperationException>(() => Run(() => repository.Save(new AppSettings())));
            True(!File.Exists(path), "External deletion was undone.");
        });
        await Test("Settings newly created external file is not overwritten", async () =>
        {
            var path = Path.Combine(Fixture("settings-external-create"), "settings.json"); var repository = new SettingsRepository(path);
            _ = repository.Load(); File.WriteAllText(path, "{\"ResultLimit\":333}");
            await Throws<InvalidOperationException>(() => Run(() => repository.Save(new AppSettings())));
            Equal("{\"ResultLimit\":333}", File.ReadAllText(path));
        });
        await Test("Settings two loaded repositories detect competing saves", async () =>
        {
            var path = Path.Combine(Fixture("settings-competing"), "settings.json"); new SettingsRepository(path).Save(new AppSettings());
            var first = new SettingsRepository(path); var second = new SettingsRepository(path); var a = first.Load(); var b = second.Load();
            a.ResultLimit = 101; first.Save(a); b.ResultLimit = 202;
            await Throws<InvalidOperationException>(() => Run(() => second.Save(b)));
            Equal(101, new SettingsRepository(path).Load().ResultLimit);
        });
        await Test("Settings BOM decoding and fingerprint refer to identical bytes", () => Run(() =>
        {
            var path = Path.Combine(Fixture("settings-bom"), "settings.json");
            File.WriteAllText(path, "{\"ResultLimit\":321,\"SearchRoots\":[]}", new UTF8Encoding(true));
            var repository = new SettingsRepository(path); var settings = repository.Load(); Equal(321, settings.ResultLimit);
            settings.ResultLimit = 322; repository.Save(settings); Equal(322, repository.Load().ResultLimit);
        }));
        await Test("Settings concurrent saves are complete JSON", async () =>
        {
            var path = Path.Combine(Fixture("settings-concurrent"), "settings.json");
            await Task.WhenAll(Enumerable.Range(1, 24).Select(i => Task.Run(() => new SettingsRepository(path).Save(new AppSettings { ResultLimit = i }))));
            var settings = new SettingsRepository(path).Load(); True(settings.ResultLimit is >= 1 and <= 24, "Concurrent setting result invalid.");
            _ = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path + ".bak"));
        });
    }

    private static byte[] Executable(byte marker = 42)
    {
        // Minimal PE-shaped synthetic fixture, never executed by these tests.
        var bytes = new byte[256]; bytes[0] = 0x4d; bytes[1] = 0x5a; bytes[0x3c] = 0x80;
        bytes[0x80] = 0x50; bytes[0x81] = 0x45; bytes[0x84] = 0x64; bytes[0x85] = 0x86;
        bytes[0x98] = 0x0b; bytes[0x99] = 0x02; bytes[0xf0] = marker; return bytes;
    }
    private static string Zip(string directory, IEnumerable<(string Name, byte[] Bytes, int Attributes)> entries)
    {
        var path = Path.Combine(directory, "package.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var item in entries)
        {
            var entry = zip.CreateEntry(item.Name); entry.ExternalAttributes = item.Attributes;
            using var output = entry.Open(); output.Write(item.Bytes);
        }
        return path;
    }

    private static async Task PackageTests()
    {
        await Test("Package verified SHA-256 and single executable extraction", async () =>
        {
            var directory = Fixture("valid-package"); var zip = Zip(directory, new[] { ("release/DIndex.exe", Executable(), 0) });
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
            await UpdatePackageSafety.VerifyFileHashAsync(zip, hash, CancellationToken.None);
            var exe = await UpdatePackageSafety.ExtractExecutableAsync(zip, Path.Combine(directory, "output"), "DIndex.exe", CancellationToken.None);
            True(Executable().SequenceEqual(File.ReadAllBytes(exe)), "Extracted content differs.");
        });
        foreach (var badName in new[] { "../DIndex.exe", "/DIndex.exe", "C:/DIndex.exe", "nested/../../DIndex.exe", "..\\DIndex.exe", "nested./DIndex.exe", "nested /DIndex.exe", "nested//DIndex.exe", "folder/DIndex.exe:stream", "folder/../DIndex.exe" })
            await Test("Package rejects path " + badName, async () =>
            {
                var directory = Fixture("bad-path"); var zip = Zip(directory, new[] { (badName, Executable(), 0) });
                await Throws<InvalidDataException>(() => UpdatePackageSafety.ExtractExecutableAsync(zip, Path.Combine(directory, "output"), "DIndex.exe", CancellationToken.None));
                True(!File.Exists(Path.Combine(directory, "DIndex.exe")), "ZIP escaped its extraction directory.");
            });
        foreach (var scenario in new[] { "symlink", "reparse", "duplicate", "case-duplicate", "wrong-name", "extra-file", "not-pe", "bad-offset" })
            await Test("Package rejects " + scenario, async () =>
            {
                var directory = Fixture(scenario); var entries = new List<(string, byte[], int)> { ("DIndex.exe", Executable(), 0) };
                if (scenario == "symlink") entries[0] = ("DIndex.exe", Executable(), unchecked((int)0xA0000000));
                if (scenario == "reparse") entries[0] = ("DIndex.exe", Executable(), 0x400);
                if (scenario == "duplicate") entries.Add(("DIndex.exe", Executable(), 0));
                if (scenario == "case-duplicate") entries.Add(("dindex.EXE", Executable(), 0));
                if (scenario == "wrong-name") entries[0] = ("Other.exe", Executable(), 0);
                if (scenario == "extra-file") entries.Add(("payload.dll", Executable(), 0));
                if (scenario == "not-pe") entries[0] = ("DIndex.exe", new byte[256], 0);
                if (scenario == "bad-offset") { var bytes = Executable(); bytes[0x3f] = 0x7f; entries[0] = ("DIndex.exe", bytes, 0); }
                var zip = Zip(directory, entries);
                await Throws<InvalidDataException>(() => UpdatePackageSafety.ExtractExecutableAsync(zip, Path.Combine(directory, "output"), "DIndex.exe", CancellationToken.None));
                True(!File.Exists(Path.Combine(directory, "output", "DIndex.exe")), "Rejected executable remained on disk.");
            });
        await Test("Package refuses to overwrite or delete a preexisting output file", async () =>
        {
            var directory = Fixture("package-existing"); var zip = Zip(directory, new[] { ("DIndex.exe", Executable(), 0) });
            var outputDirectory = Path.Combine(directory, "output"); Directory.CreateDirectory(outputDirectory);
            var output = Path.Combine(outputDirectory, "DIndex.exe"); File.WriteAllText(output, "existing executable");
            await Throws<IOException>(() => UpdatePackageSafety.ExtractExecutableAsync(zip, outputDirectory, "DIndex.exe", CancellationToken.None));
            Equal("existing executable", File.ReadAllText(output));
        });
        await Test("Manifest accepts Windows PowerShell UTF-8 BOM", () => Run(() =>
        {
            var bytes = new byte[] { 0xef, 0xbb, 0xbf }.Concat(Encoding.UTF8.GetBytes("{\"version\":\"1.2.3\"}")).ToArray();
            Equal("1.2.3", UpdateService.DeserializeManifest(bytes)!.Version);
        }));
        await Test("Package rejects changed hash and missing hash", async () =>
        {
            var path = Zip(Fixture("hash"), new[] { ("DIndex.exe", Executable(), 0) });
            await Throws<InvalidDataException>(() => UpdatePackageSafety.VerifyFileHashAsync(path, new string('0', 64), CancellationToken.None));
            await Throws<InvalidDataException>(() => UpdatePackageSafety.VerifyFileHashAsync(path, "", CancellationToken.None));
        });
        await Test("Package cancellation does not extract files", async () =>
        {
            var directory = Fixture("package-cancel"); var path = Zip(directory, new[] { ("DIndex.exe", Executable(), 0) });
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Throws<OperationCanceledException>(() => UpdatePackageSafety.ExtractExecutableAsync(path, Path.Combine(directory, "output"), "DIndex.exe", cts.Token));
            True(!Directory.Exists(Path.Combine(directory, "output")), "Canceled extraction created files.");
        });
        foreach (var url in new[] { "http://raw.githubusercontent.com/Chairman-bits/DIndex/main/DIndex.zip", "https://evil.example/DIndex.zip", "https://raw.githubusercontent.com/attacker/DIndex/main/DIndex.zip", "https://raw.githubusercontent.com@evil.example/DIndex.zip", "https://github.com/Chairman-bits/DIndex/other/DIndex.zip", "https://raw.githubusercontent.com/Chairman-bits/DIndex/main/DIndex.zip?redirect=1" })
            await Test("Package rejects untrusted URL " + url, () => Throws<InvalidDataException>(() => Run(() => UpdatePackageSafety.ValidatePackageUrl(url))));
        await Test("Package accepts official raw and release URLs", () => Run(() =>
        {
            UpdatePackageSafety.ValidatePackageUrl("https://raw.githubusercontent.com/Chairman-bits/DIndex/main/DIndex.zip");
            UpdatePackageSafety.ValidatePackageUrl("https://github.com/Chairman-bits/DIndex/releases/download/v1.0.1/DIndex.zip");
        }));
        await Test("Update missing hashes stops before download with manual-update guidance", async () =>
        {
            var result = await new UpdateService().StartUpdateAsync(new UpdateInfo
            {
                Version = "999.0.0", DownloadUrl = "https://raw.githubusercontent.com/Chairman-bits/DIndex/main/DIndex.zip",
                UpdaterUrl = "https://raw.githubusercontent.com/Chairman-bits/DIndex/main/DIndexUpdater.zip"
            }, CancellationToken.None);
            Equal(false, result); True(System.Windows.MessageBox.LastMessage?.Contains("手動") == true, "Manual-update guidance missing.");
        });
    }

    private static async Task UpdaterTests()
    {
        await Test("Updater atomic install retains backup and rollback restores original", () => Run(() =>
        {
            var directory = Fixture("install"); var app = Path.Combine(directory, "DIndex.exe"); var staged = Path.Combine(directory, "new.exe");
            File.WriteAllBytes(app, Executable(1)); File.WriteAllBytes(staged, Executable(2));
            Updater.InstallVerifiedExecutable(staged, app);
            True(Executable(2).SequenceEqual(File.ReadAllBytes(app)), "New executable not installed.");
            True(Executable(1).SequenceEqual(File.ReadAllBytes(app + ".bak")), "Original backup missing.");
            Updater.Rollback(app);
            True(Executable(1).SequenceEqual(File.ReadAllBytes(app)), "Original not restored.");
            True(Executable(2).SequenceEqual(File.ReadAllBytes(app + ".failed")), "Failed update was not preserved.");
        }));
        await Test("Updater recovers Windows partial replacement failure", async () =>
        {
            var directory = Fixture("install-partial"); var app = Path.Combine(directory, "DIndex.exe"); var staged = Path.Combine(directory, "new.exe");
            File.WriteAllBytes(app, Executable(1)); File.WriteAllBytes(staged, Executable(2));
            await Throws<IOException>(() => Run(() => Updater.InstallVerifiedExecutable(staged, app, (_, target, backup) =>
            {
                File.Move(target, backup); throw new IOException("Simulated ERROR_UNABLE_TO_MOVE_REPLACEMENT_2");
            })));
            True(Executable(1).SequenceEqual(File.ReadAllBytes(app)), "Partial replacement did not restore the original executable.");
            True(Executable(1).SequenceEqual(File.ReadAllBytes(app + ".bak")), "Partial replacement discarded backup.");
        });
        await Test("Updater recovers replacement failure after target changed", async () =>
        {
            var directory = Fixture("install-changed"); var app = Path.Combine(directory, "DIndex.exe"); var staged = Path.Combine(directory, "new.exe");
            File.WriteAllBytes(app, Executable(1)); File.WriteAllBytes(staged, Executable(2));
            await Throws<IOException>(() => Run(() => Updater.InstallVerifiedExecutable(staged, app, (source, target, backup) =>
            {
                File.Copy(target, backup); File.Copy(source, target, true); throw new IOException("Simulated post-replacement failure");
            })));
            True(Executable(1).SequenceEqual(File.ReadAllBytes(app)), "Changed target was not rolled back.");
            True(Executable(2).SequenceEqual(File.ReadAllBytes(app + ".failed")), "Failed new executable was not preserved.");
        });
        await Test("Updater rollback restores absent target and retains backup", () => Run(() =>
        {
            var directory = Fixture("rollback-missing"); var app = Path.Combine(directory, "DIndex.exe"); File.WriteAllBytes(app + ".bak", Executable(1));
            Updater.Rollback(app);
            True(Executable(1).SequenceEqual(File.ReadAllBytes(app)), "Absent target not restored.");
            True(File.Exists(app + ".bak"), "Rollback consumed the only backup.");
        }));
        await Test("Updater invalid executable never replaces original", async () =>
        {
            var directory = Fixture("install-invalid"); var app = Path.Combine(directory, "DIndex.exe"); var staged = Path.Combine(directory, "new.exe");
            File.WriteAllBytes(app, Executable(1)); File.WriteAllText(staged, "not executable");
            await Throws<InvalidDataException>(() => Run(() => Updater.InstallVerifiedExecutable(staged, app)));
            True(Executable(1).SequenceEqual(File.ReadAllBytes(app)), "Invalid executable replaced original.");
        });
        await Test("Updater refuses duplicate/unknown/missing arguments", async () =>
        {
            await Throws<ArgumentException>(() => Run(() => Updater.ParseArgs(new[] { "--restart", "--restart" })));
            await Throws<ArgumentException>(() => Run(() => Updater.ParseArgs(new[] { "--unknown" })));
            await Throws<ArgumentException>(() => Run(() => Updater.ParseArgs(new[] { "--appPath" })));
            await Throws<ArgumentException>(() => Run(() => Updater.ParseArgs(new[] { "trailing" })));
        });
        await Test("Updater preserves selected profile and quoted argument boundaries", () => Run(() =>
        {
            var profile = Path.Combine(Root, "custom profile with spaces");
            var start = Updater.CreateRestartStartInfo(Path.Combine(Root, "DIndex.exe"), profile);
            Equal(false, start.UseShellExecute); Equal(profile, start.Environment["DINDEX_DATA_DIR"]); Equal(0, start.ArgumentList.Count);
            var updaterStart = UpdateService.CreateUpdaterStartInfo(Path.Combine(Root, "DIndexUpdater.exe"), 42, Path.Combine(Root, "DIndex.exe"), Path.Combine(Root, "app.zip"), new string('a', 64), profile, Path.Combine(Root, "ready"), Path.Combine(Root, "proceed"));
            var parsed = Updater.ParseArgs(updaterStart.ArgumentList.ToArray()); Equal(profile, parsed["dataDirectory"]); Equal("42", parsed["processId"]);
        }));
        await Test("Updater handshake needs explicit proceed", async () =>
        {
            var directory = Fixture("handshake"); var ready = Path.Combine(directory, "ready"); var proceed = Path.Combine(directory, "proceed");
            Updater.SignalReady(ready);
            await UpdateService.WaitUntilReadyAsync(Process.GetCurrentProcess(), ready, TimeSpan.FromSeconds(1), CancellationToken.None);
            await Throws<TimeoutException>(() => Updater.WaitForProceedAsync(proceed, TimeSpan.FromMilliseconds(20), CancellationToken.None));
            UpdateService.SignalProceed(proceed);
            await Updater.WaitForProceedAsync(proceed, TimeSpan.FromSeconds(1), CancellationToken.None);
        });
        await Test("Updater canceled handshake never creates proceed signal", async () =>
        {
            var directory = Fixture("handshake-cancel"); var ready = Path.Combine(directory, "ready"); var proceed = Path.Combine(directory, "proceed");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Throws<OperationCanceledException>(() => UpdateService.WaitUntilReadyAsync(Process.GetCurrentProcess(), ready, TimeSpan.FromSeconds(1), cancellation.Token));
            await Throws<OperationCanceledException>(() => Updater.WaitForProceedAsync(proceed, TimeSpan.FromSeconds(1), cancellation.Token));
            True(!File.Exists(proceed), "Canceled handshake committed an update.");
        });
        await Test("Updater handshake rejects invalid signal and preserves existing files", async () =>
        {
            var signal = Path.Combine(Fixture("handshake-invalid"), "signal"); File.WriteAllText(signal, "existing");
            await Throws<InvalidDataException>(() => Updater.WaitForProceedAsync(signal, TimeSpan.FromSeconds(1), CancellationToken.None));
            await Throws<IOException>(() => Run(() => Updater.SignalReady(signal)));
            await Throws<IOException>(() => Run(() => UpdateService.SignalProceed(signal)));
            Equal("existing", File.ReadAllText(signal));
        });
        await Test("Updater readiness timeout leaves calling process running", async () =>
        {
            var ready = Path.Combine(Fixture("ready-timeout"), "ready");
            await Throws<TimeoutException>(() => UpdateService.WaitUntilReadyAsync(Process.GetCurrentProcess(), ready, TimeSpan.FromMilliseconds(20), CancellationToken.None));
            True(!Process.GetCurrentProcess().HasExited, "Calling application was terminated.");
        });
        await Test("Updater startup failure is detected before application shutdown", async () =>
        {
            var start = new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--exit-immediately");
            using var exited = Process.Start(start)!; await exited.WaitForExitAsync();
            await Throws<InvalidOperationException>(() => UpdateService.WaitUntilReadyAsync(exited, Path.Combine(Root, "never-ready"), TimeSpan.FromSeconds(1), CancellationToken.None));
        });
        await Test("Updater wrong process is never killed", async () =>
        {
            await Throws<InvalidOperationException>(() => Updater.WaitForProcessExitAsync(Environment.ProcessId, Path.Combine(Root, "DIndex.exe"), TimeSpan.FromMilliseconds(30)));
            True(!Process.GetCurrentProcess().HasExited, "Current test process was killed.");
        });
        await Test("Updater timeout never force-kills the application", async () =>
        {
            await Throws<TimeoutException>(() => Updater.WaitForProcessExitAsync(Environment.ProcessId, Environment.ProcessPath!, TimeSpan.FromMilliseconds(30)));
            True(!Process.GetCurrentProcess().HasExited, "Current test process was killed.");
        });
    }
    private static async Task InstalledModeTests()
    {
        await Test("Portable mode remains available without an MSI marker", () => Run(() =>
        {
            var directory = Fixture("portable-mode");
            File.WriteAllText(Path.Combine(directory, "unrelated.json"), "{}");
            True(!InstallationMode.IsMsiManaged(directory), "Portable folder treated as MSI-managed.");
            InstallationMode.ThrowIfMsiManaged(directory);
        }));
        foreach (var content in new[] { "{\"schemaVersion\":1,\"app\":\"DIndex\",\"updateMode\":\"msi\"}", "broken json", "" })
            await Test("MSI marker blocks all EXE update paths: " + (content.Length == 0 ? "empty" : content), async () =>
            {
                var directory = Fixture("installed-mode");
                var marker = Path.Combine(directory, InstallationMode.MarkerFileName);
                File.WriteAllText(marker, content);
                var service = new UpdateService(directory);
                True(service.IsMsiInstalled, "Marker was ignored.");
                True(!service.IsPreviewBuild, "Test must exercise non-preview assembly version.");
                var check = await service.CheckAsync(new CancellationToken(canceled: true));
                True(!check.HasUpdate && check.Info is null, "Installed check offered an EXE update.");
                Equal(InstallationMode.UpdateMessage, check.Message);
                var started = await service.StartUpdateAsync(new UpdateInfo { Version = "999.0.0" }, CancellationToken.None);
                True(!started, "Installed app started an EXE update.");
                Equal(InstallationMode.UpdateMessage, System.Windows.MessageBox.LastMessage);
                var app = Path.Combine(directory, "DIndex.exe"); var staged = Path.Combine(directory, "new.exe");
                File.WriteAllBytes(app, Executable(1)); File.WriteAllBytes(staged, Executable(2));
                await Throws<InvalidOperationException>(() => Run(() => Updater.InstallVerifiedExecutable(staged, app)));
                True(Executable(1).SequenceEqual(File.ReadAllBytes(app)), "MSI application was changed.");
                True(Executable(2).SequenceEqual(File.ReadAllBytes(staged)), "Refusal consumed staged executable.");
                True(!File.Exists(app + ".bak"), "Refusal mutated installer files.");
                Equal(content, File.ReadAllText(marker));
            });
        await Test("MSI marker directory also fails closed", async () =>
        {
            var directory = Fixture("marker-directory");
            Directory.CreateDirectory(Path.Combine(directory, InstallationMode.MarkerFileName));
            True(InstallationMode.IsMsiManaged(directory), "Marker directory was ignored.");
            await Throws<InvalidOperationException>(() => Run(() => InstallationMode.ThrowIfMsiManaged(directory)));
        });
        await Test("Updater direct invocation refuses MSI before staging or waiting", async () =>
        {
            var directory = Fixture("installed-updater-direct"); var app = Path.Combine(directory, "DIndex.exe");
            File.WriteAllBytes(app, Executable(1));
            File.WriteAllText(Path.Combine(directory, InstallationMode.MarkerFileName), "broken marker");
            var method = typeof(Updater).GetMethod("Main", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            using var error = new StringWriter(); var previousError = Console.Error;
            int exitCode;
            try
            {
                Console.SetError(error);
                exitCode = await (Task<int>)method.Invoke(null, new object[] { new[] {
                    "--processId", Environment.ProcessId.ToString(), "--appPath", app,
                    "--appSha256", new string('a', 64), "--appZipPath", Path.Combine(directory, "missing.zip"),
                    "--dataDirectory", Path.Combine(directory, "profile") } })!;
            }
            finally { Console.SetError(previousError); }
            Equal(1, exitCode);
            True(error.ToString().Contains(InstallationMode.UpdateMessage), "Direct updater did not refuse installed mode first.");
            True(Executable(1).SequenceEqual(File.ReadAllBytes(app)), "Direct updater changed MSI application.");
            True(!File.Exists(app + ".bak"), "Direct updater created a backup.");
            Equal(0, Directory.GetFiles(directory, "*.staged").Length);
        });
    }

    private static async Task SettingsSchemaTests()
    {
        await Test("Settings unknown fields survive preference edits and reload", () => Run(() =>
        {
            var path = Path.Combine(Fixture("settings-extension"), "settings.json");
            File.WriteAllText(path, "{\"SearchRoots\":[],\"FutureOption\":{\"enabled\":true,\"values\":[1,2]},\"OtherFlag\":\"keep\"}");
            var repository = new SettingsRepository(path); var settings = repository.Load();
            settings.ResultLimit = 1000; repository.Save(settings);
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            Equal(true, document.RootElement.GetProperty("FutureOption").GetProperty("enabled").GetBoolean());
            Equal(2, document.RootElement.GetProperty("FutureOption").GetProperty("values").GetArrayLength());
            Equal("keep", document.RootElement.GetProperty("OtherFlag").GetString());
            Equal(1000, new SettingsRepository(path).Load().ResultLimit);
        }));
        foreach (var schema in new[] { "99", "-1", "2147483648", "\"future\"" })
            await Test("Settings unsupported schema never rolls back primary: " + schema, async () =>
            {
                var directory = Fixture("settings-future"); var path = Path.Combine(directory, "settings.json");
                var original = "{\"SchemaVersion\":" + schema + ",\"SearchRoots\":[\"future-data\"]}";
                File.WriteAllText(path, original); File.WriteAllText(path + ".bak", "{\"SearchRoots\":[\"old-backup\"]}");
                var backup = File.ReadAllText(path + ".bak"); var repository = new SettingsRepository(path);
                Equal(0, repository.Load().SearchRoots.Count);
                True(repository.LastLoadWarning is not null, "Unsupported schema was hidden.");
                await Throws<InvalidOperationException>(() => Run(() => repository.Save(new AppSettings())));
                Equal(original, File.ReadAllText(path)); Equal(backup, File.ReadAllText(path + ".bak"));
                Equal(0, Directory.GetFiles(directory, "*.corrupt-*").Length);
            });
        await Test("Settings future backup blocks fallback and later replacement", async () =>
        {
            var directory = Fixture("settings-future-backup"); var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "corrupt"); File.WriteAllText(path + ".bak", "{\"SchemaVersion\":99}");
            var repository = new SettingsRepository(path); Equal(0, repository.Load().SearchRoots.Count);
            await Throws<InvalidOperationException>(() => Run(() => repository.Save(new AppSettings())));
            Equal("corrupt", File.ReadAllText(path)); Equal("{\"SchemaVersion\":99}", File.ReadAllText(path + ".bak"));
            Equal(0, Directory.GetFiles(directory, "*.corrupt-*").Length);
        });
        await Test("Settings save without load refuses future existing primary", async () =>
        {
            var directory = Fixture("settings-save-future"); var path = Path.Combine(directory, "settings.json");
            const string future = "{\"SchemaVersion\":99}"; File.WriteAllText(path, future);
            await Throws<InvalidOperationException>(() => Run(() => new SettingsRepository(path).Save(new AppSettings())));
            Equal(future, File.ReadAllText(path)); Equal(1, Directory.GetFiles(directory).Length);
        });
    }

}
