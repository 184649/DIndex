using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;

internal static class ReleaseTests
{
    static int passed;
    static void Test(string name, Action run) { run(); passed++; Console.WriteLine("PASS " + name); }
    static void Reject(Action run)
    {
        try { run(); } catch (Exception ex) when (ex is InvalidDataException or JsonException or BadImageFormatException or EndOfStreamException or KeyNotFoundException) { return; }
        throw new Exception("Expected fail-closed rejection.");
    }
    internal static byte[] Bundle(params (string Name, byte[] Bytes)[] entries)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(new byte[64]);
        var locations = new List<(string Name, long Offset, byte[] Bytes)>();
        foreach (var entry in entries) { locations.Add((entry.Name, stream.Position, entry.Bytes)); writer.Write(entry.Bytes); }
        var offset = stream.Position;
        writer.Write(6u); writer.Write(0u); writer.Write(entries.Length); writer.Write("synthetic-test");
        for (var i = 0; i < 4; i++) writer.Write(0L);
        writer.Write(0UL);
        foreach (var entry in locations)
        {
            writer.Write(entry.Offset); writer.Write((long)entry.Bytes.Length); writer.Write(0L); writer.Write((byte)1); writer.Write(entry.Name);
        }
        stream.Position = 8; writer.Write(offset); writer.Write(Program.Signature);
        return stream.ToArray();
    }
    static void Zip(string path, params string[] names)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var name in names) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write("synthetic payload " + name); }
    }
    static Dictionary<string, string> Feed(string directory)
    {
        var url = Program.ReleaseBase("1.1.2");
        return new()
        {
            ["version"] = "1.1.2", ["downloadUrl"] = url + "/DIndex.zip", ["updaterUrl"] = url + "/DIndexUpdater.zip",
            ["releaseNotes"] = url + "/release-notes.json", ["downloadSha256"] = Program.Hash(Path.Combine(directory, "DIndex.zip")),
            ["updaterSha256"] = Program.Hash(Path.Combine(directory, "DIndexUpdater.zip"))
        };
    }
    static void WriteFeed(string directory, Dictionary<string, string> feed) => File.WriteAllBytes(Path.Combine(directory, "version.json"), JsonSerializer.SerializeToUtf8Bytes(feed));
    static void WriteSums(string directory) => File.WriteAllLines(Path.Combine(directory, "SHA256SUMS"), Program.Assets.Select(x => Program.Hash(Path.Combine(directory, x)) + "  " + x));
    internal static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dindex-release-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Test("canonical stable version", () => Program.Require(Program.ReleaseBase("1.1.2").EndsWith("/v1.1.2"), "Version mismatch"));
            foreach (var version in new[] { "", "1.1", "1.1.2.0", "1.1.2-preview.1", "v1.1.2", "01.1.2", "1.1.2+foo", "1.1.2\n", "-1.1.2", "65535.1.2", "9999999999999.1.2" })
                Test("reject malformed version " + version.Replace("\n", "\\n"), () => Reject(() => Program.ReleaseBase(version)));
            Test("reject duplicate JSON fields", () => Reject(() => Program.Json(Encoding.UTF8.GetBytes("{\"version\":\"1.1.2\",\"version\":\"1.0.1\"}"))));
            var appZip = Path.Combine(directory, "DIndex.zip");
            var updaterZip = Path.Combine(directory, "DIndexUpdater.zip");
            Zip(appZip, "DIndex.exe"); Zip(updaterZip, "DIndexUpdater.exe");
            Test("single root EXE accepted", () => Program.Require(Program.ReadSingleExe(appZip, "DIndex.exe").Length > 0, "Empty payload"));
            foreach (var entry in new[] { "DIndex.pdb", "data/settings.json", "index-cache.tsv", "../DIndex.exe", "folder/DIndex.exe", "DINDEX.exe" })
                Test("reject extra/stale ZIP entry " + entry, () => { Zip(appZip, "DIndex.exe", entry); Reject(() => Program.ReadSingleExe(appZip, "DIndex.exe")); });
            Test("reject nested-only EXE", () => { Zip(appZip, "folder/DIndex.exe"); Reject(() => Program.ReadSingleExe(appZip, "DIndex.exe")); });
            Zip(appZip, "DIndex.exe");
            WriteFeed(directory, Feed(directory));
            Test("both exact ZIP hashes accepted", () => Program.VerifyFeed(directory, "1.1.2"));
            foreach (var key in new[] { "downloadSha256", "updaterSha256" })
            {
                Test("reject wrong " + key, () => { var feed = Feed(directory); feed[key] = new string('0', 64); WriteFeed(directory, feed); Reject(() => Program.VerifyFeed(directory, "1.1.2")); });
                Test("reject malformed " + key, () => { var feed = Feed(directory); feed[key] = "a"; WriteFeed(directory, feed); Reject(() => Program.VerifyFeed(directory, "1.1.2")); });
            }
            foreach (var url in new[] { "https://github.com/184649/DIndex/releases/latest/download/DIndex.zip", "https://raw.githubusercontent.com/184649/DIndex/main/DIndex.zip", "http://github.com/184649/DIndex/releases/download/v1.1.2/DIndex.zip", Program.ReleaseBase("1.1.2") + "/DIndex.zip?x=1", Program.ReleaseBase("1.1.2") + "/DIndex.zip#fragment", Program.ReleaseBase("1.1.1") + "/DIndex.zip", "https://github.com.evil.invalid/184649/DIndex/releases/download/v1.1.2/DIndex.zip" })
                Test("reject nonexact URL " + url, () => { var feed = Feed(directory); feed["downloadUrl"] = url; WriteFeed(directory, feed); Reject(() => Program.VerifyFeed(directory, "1.1.2")); });
            foreach (var key in new[] { "updaterUrl", "releaseNotes" })
                Test("reject wrong tagged " + key, () => { var feed = Feed(directory); feed[key] += "?stale=1"; WriteFeed(directory, feed); Reject(() => Program.VerifyFeed(directory, "1.1.2")); });
            WriteFeed(directory, Feed(directory));
            File.WriteAllText(Path.Combine(directory, "release-notes.json"), "{\"version\":\"1.1.2\",\"notes\":[]}");
            File.WriteAllText(Path.Combine(directory, "THIRD-PARTY-NOTICES.txt"), "synthetic license fixture");
            WriteSums(directory);
            Test("all asset checksums accepted", () => Program.VerifyAllChecksums(directory));
            foreach (var name in Program.Assets)
                Test("detect changed asset " + name, () => { var path = Path.Combine(directory, name); var original = File.ReadAllBytes(path); File.AppendAllText(path, "tampered"); Reject(() => Program.VerifyAllChecksums(directory)); File.WriteAllBytes(path, original); });
            Test("reject stale output", () => { var path = Path.Combine(directory, "old-version.zip"); File.WriteAllText(path, "stale"); Reject(() => Program.VerifyAllChecksums(directory)); File.Delete(path); });
            Test("reject duplicate checksum", () => { var path = Path.Combine(directory, "SHA256SUMS"); var lines = File.ReadAllLines(path); lines[1] = lines[0]; File.WriteAllLines(path, lines); Reject(() => Program.VerifyAllChecksums(directory)); WriteSums(directory); });
            var assembly = File.ReadAllBytes(Assembly.GetExecutingAssembly().Location);
            Test("inspect compiled versions and embedded resource without executing", () =>
            {
                var result = Program.InspectAssembly(assembly, "StableReleaseVerifier", "1.1.2", "DIndexUpdater.LegacyUpdate.json");
                Program.Require(Encoding.UTF8.GetString(result).Trim() == "{\"fixture\":true}", "Resource did not round-trip");
            });
            Test("reject stale compiled assembly version", () => Reject(() => Program.InspectAssembly(assembly, "StableReleaseVerifier", "1.1.1")));
            Test("reject missing embedded updater pin", () => Reject(() => Program.InspectAssembly(assembly, "StableReleaseVerifier", "1.1.2", "missing.json")));
            var pin = new Dictionary<string, string> { ["version"] = "1.1.2", ["downloadUrl"] = Program.ReleaseBase("1.1.2") + "/DIndex.zip", ["downloadSha256"] = new string('a', 64) };
            Test("accept exact compiled pin contract", () => Program.VerifyPin(JsonSerializer.SerializeToUtf8Bytes(pin), "1.1.2", new string('A', 64)));
            foreach (var field in new[] { "version", "downloadUrl", "downloadSha256" })
                Test("reject compiled pin mismatch " + field, () => { var wrong = new Dictionary<string, string>(pin) { [field] = "wrong" }; Reject(() => Program.VerifyPin(JsonSerializer.SerializeToUtf8Bytes(wrong), "1.1.2", new string('A', 64))); });
            var bundle = Bundle(("StableReleaseVerifier.dll", assembly));
            Test("inspect assembly bytes from final single-file bundle", () => Program.InspectAssembly(Program.ReadBundle(bundle)["StableReleaseVerifier.dll"], "StableReleaseVerifier", "1.1.2"));
            Test("reject missing single-file marker", () => Reject(() => Program.ReadBundle(new byte[100])));
            Test("reject bundle header beyond file", () => { var bad = (byte[])bundle.Clone(); BitConverter.GetBytes(long.MaxValue).CopyTo(bad, 8); Reject(() => Program.ReadBundle(bad)); });
            foreach (var name in new[] { "../data", "settings.json", "index-cache.tsv", "DIndex.pdb", "installed-by-msi.json" })
                Test("reject forbidden bundle content " + name, () => Reject(() => Program.ReadBundle(Bundle((name, new byte[1])))));
            Test("reject duplicate case-folded bundle paths", () => Reject(() => Program.ReadBundle(Bundle(("DIndex.dll", new byte[1]), ("dindex.dll", new byte[1])))));
            var runtime = new Dictionary<string, byte[]> { ["coreclr.dll"] = [], ["clrjit.dll"] = [], ["System.Private.CoreLib.dll"] = [], ["DIndexUpdater.dll"] = [], ["DIndexUpdater.runtimeconfig.json"] = Encoding.UTF8.GetBytes("{\"runtimeOptions\":{\"includedFrameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.13\"}]}}") };
            Test("accept exact self-contained runtime manifest", () => Program.InspectRuntime(runtime, "DIndexUpdater", "10.0.13", false));
            Test("reject stale runtime manifest", () => Reject(() => Program.InspectRuntime(runtime, "DIndexUpdater", "10.0.12", false)));
            Test("reject framework-dependent runtime manifest", () => { runtime["DIndexUpdater.runtimeconfig.json"] = Encoding.UTF8.GetBytes("{\"runtimeOptions\":{\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"10.0.13\"}}}"); Reject(() => Program.InspectRuntime(runtime, "DIndexUpdater", "10.0.13", false)); });
            Console.WriteLine($"PASS {passed} bounded release-verifier tests (synthetic files only).");
        }
        finally { Directory.Delete(directory, true); }
    }
}
