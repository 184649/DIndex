using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

// The verify mode inspects files only; the separate smoke mode runs disposable
// copies of NEW executables with a synthetic profile (see RuntimeSmoke.cs).
// Bundle v6 layout: dotnet/runtime v10.0.0, Microsoft.NET.HostModel/Bundle/{Manifest,FileEntry}.cs.
internal static class Program
{
    internal static readonly byte[] Signature = Convert.FromHexString("8B1202B96A612038727B930214D7A03213F5B9E6EFAE3318EE3B2DCE24B36AAE");
    internal static readonly string[] Assets = ["DIndex.zip", "DIndexUpdater.zip", "release-notes.json", "THIRD-PARTY-NOTICES.txt", "version.json"];
    const int MaxExeBytes = 536870912;
    static int Main(string[] args)
    {
        try
        {
            if (args is ["self-test"]) { SelfTest(); return 0; }
            if (args is ["smoke", var app, var updater, var appVersion, var smokeRoot])
            {
                RuntimeSmoke.Run(app, updater, appVersion, smokeRoot);
                Console.WriteLine("PASS isolated new-app/new-updater managed startup smoke; no machine-wide settings changed.");
                return 0;
            }
            if (args is ["verify", var directory, var version, var runtime, var runtimeReference, var proofPath])
            {
                var proof = VerifyRelease(directory, version, runtime, runtimeReference);
                using (var output = new FileStream(proofPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    JsonSerializer.Serialize(output, proof, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                Console.WriteLine($"PASS stable release {version}: both ZIP hashes, all asset checksums, native and bundled versions, embedded updater pin, self-contained .NET {runtime}.");
                return 0;
            }
            throw new ArgumentException("Usage: StableReleaseVerifier self-test | verify <release-directory> <stable-version> <runtime-version> <runtime-pack-coreclr.dll> <new-proof-json> | smoke <new-app.exe> <new-updater.exe> <stable-version> <new-directory>");
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex.Message); return 1; }
    }
    internal static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    internal static string ReleaseBase(string version)
    {
        Require(Regex.IsMatch(version, @"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z") &&
            version.Split('.').All(x => int.TryParse(x, out var number) && number <= 65534), "Invalid stable version.");
        return $"https://github.com/184649/DIndex/releases/download/v{version}";
    }
    internal static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)); }
    internal static JsonDocument Json(byte[] bytes)
    {
        Require(bytes.Length <= 1048576, "JSON is too large.");
        var result = JsonDocument.Parse(bytes);
        try
        {
            Require(result.RootElement.ValueKind == JsonValueKind.Object, "Expected a JSON object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in result.RootElement.EnumerateObject()) Require(seen.Add(p.Name), "Duplicate JSON property: " + p.Name);
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    internal static string Text(JsonElement root, string name) => root.GetProperty(name).GetString() ?? throw new InvalidDataException("Null JSON property: " + name);
    internal static void RequireHash(string expected, string actual, string label) =>
        Require(Regex.IsMatch(expected, @"\A[0-9a-fA-F]{64}\z") && expected.Equals(actual, StringComparison.OrdinalIgnoreCase), "SHA-256 mismatch: " + label);
    internal static (string AppHash, string UpdaterHash) VerifyFeed(string directory, string version)
    {
        var releaseBase = ReleaseBase(version);
        using var feed = Json(File.ReadAllBytes(Path.Combine(directory, "version.json")));
        var root = feed.RootElement;
        Require(Text(root, "version") == version, "Feed version differs from requested version.");
        Require(Text(root, "downloadUrl") == releaseBase + "/DIndex.zip", "App URL must be the exact immutable tagged release URL.");
        Require(Text(root, "updaterUrl") == releaseBase + "/DIndexUpdater.zip", "Updater URL must be the exact immutable tagged release URL.");
        Require(Text(root, "releaseNotes") == releaseBase + "/release-notes.json", "Release notes URL must use the same exact tag.");
        var app = Hash(Path.Combine(directory, "DIndex.zip"));
        var updater = Hash(Path.Combine(directory, "DIndexUpdater.zip"));
        RequireHash(Text(root, "downloadSha256"), app, "DIndex.zip");
        RequireHash(Text(root, "updaterSha256"), updater, "DIndexUpdater.zip");
        return (app, updater);
    }
    internal static void VerifyAllChecksums(string directory)
    {
        var expectedNames = Assets.Append("SHA256SUMS").Order().ToArray();
        Require(Directory.GetFileSystemEntries(directory).Select(Path.GetFileName).Order().SequenceEqual(expectedNames), "Release directory has missing, stale or unexpected files.");
        var entries = File.ReadAllLines(Path.Combine(directory, "SHA256SUMS"));
        Require(entries.Length == Assets.Length, "Checksum list must cover every release asset exactly once.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in entries)
        {
            var match = Regex.Match(line, @"\A([0-9a-fA-F]{64})  ([A-Za-z0-9._-]+)\z");
            Require(match.Success, "Malformed checksum entry.");
            var name = match.Groups[2].Value;
            Require(Assets.Contains(name) && seen.Add(name), "Unexpected or duplicate checksum file: " + name);
            RequireHash(match.Groups[1].Value, Hash(Path.Combine(directory, name)), name);
        }
    }
    internal static byte[] ReadSingleExe(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        Require(zip.Entries.Count == 1 && zip.Entries[0].FullName == name, "ZIP must contain exactly one root-level " + name);
        var entry = zip.Entries[0];
        Require(entry.Length > 0 && entry.Length <= MaxExeBytes, "Invalid executable size.");
        Require(((entry.ExternalAttributes >> 16) & 0xF000) != 0xA000, "ZIP executable cannot be a symbolic link.");
        using var source = entry.Open();
        var bytes = new byte[checked((int)entry.Length)];
        source.ReadExactly(bytes);
        Require(source.ReadByte() == -1, "ZIP payload exceeds declared length.");
        return bytes;
    }
    internal static Dictionary<string, byte[]> ReadBundle(byte[] exe)
    {
        Require(exe.Length > Signature.Length + 8 && exe.Length <= MaxExeBytes, "Invalid executable size for bundle inspection.");
        // Native resources (including this app's icon) can place the apphost
        // marker beyond 4 MiB. Search the whole already-bounded executable.
        var marker = exe.AsSpan().IndexOf(Signature);
        Require(marker >= 8, "Missing .NET single-file bundle marker.");
        var offset = BitConverter.ToInt64(exe, marker - 8);
        Require(offset > marker + Signature.Length && offset < exe.Length - 12, "Invalid bundle header offset.");
        using var input = new MemoryStream(exe, false);
        using var reader = new BinaryReader(input, Encoding.UTF8);
        input.Position = offset;
        Require(reader.ReadUInt32() == 6 && reader.ReadUInt32() == 0, "Only uncompressed .NET bundle format 6.0 is supported.");
        var count = reader.ReadInt32();
        Require(count is > 0 and <= 2000, "Invalid bundle file count.");
        Require(reader.ReadString().Length is > 0 and <= 128, "Invalid bundle ID.");
        for (var i = 0; i < 4; i++) _ = reader.ReadInt64(); // deps/runtime-config ranges, verified via entries below
        Require(reader.ReadUInt64() == 0, "Unexpected bundle extraction compatibility flags.");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ranges = new List<(long Start, long End)>();
        long total = 0;
        for (var i = 0; i < count; i++)
        {
            var start = reader.ReadInt64();
            var size = reader.ReadInt64();
            Require(reader.ReadInt64() == 0, "Compressed bundle entries are disabled for this audited release format.");
            var type = reader.ReadByte();
            var name = reader.ReadString();
            Require(type <= 5 && name.Length is > 0 and <= 512 && !name.Contains('\\') &&
                !name.StartsWith('/') && !name.Contains(':') && name.Split('/').All(x => x is not ("" or "." or "..")) && names.Add(name), "Invalid/duplicate bundle path: " + name);
            Require(!name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) &&
                !new[] { "settings.json", "index-cache.tsv", "error.log", "installed-by-msi.json" }.Contains(Path.GetFileName(name), StringComparer.OrdinalIgnoreCase) &&
                !name.Split('/').Any(x => new[] { "data", "cache", "logs", "update" }.Contains(x, StringComparer.OrdinalIgnoreCase)), "Debug, profile or MSI content in stable bundle: " + name);
            Require(start >= marker + Signature.Length && size >= 0 && size <= MaxExeBytes && start <= offset && size <= offset - start, "Invalid bundle entry range.");
            total = checked(total + size);
            Require(total <= exe.Length, "Overlapping or excessive bundle payload.");
            ranges.Add((start, start + size));
            files.Add(name, exe.AsSpan(checked((int)start), checked((int)size)).ToArray());
        }
        var ordered = ranges.OrderBy(x => x.Start).ToArray();
        for (var i = 1; i < ordered.Length; i++) Require(ordered[i - 1].End <= ordered[i].Start, "Overlapping bundle file ranges.");
        return files;
    }
    internal static byte[] InspectAssembly(byte[] bytes, string name, string version, string? resourceName = null)
    {
        using var stream = new MemoryStream(bytes, false);
        using var pe = new PEReader(stream);
        Require(pe.HasMetadata, "Missing managed metadata.");
        var metadata = pe.GetMetadataReader();
        var assembly = metadata.GetAssemblyDefinition();
        Require(metadata.GetString(assembly.Name) == name && assembly.Version.ToString() == version + ".0", "Bundled assembly identity/version mismatch: " + name);
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (metadata.GetString(type.Namespace) != "System.Reflection") continue;
            var attributeName = metadata.GetString(type.Name);
            if (attributeName is not ("AssemblyInformationalVersionAttribute" or "AssemblyFileVersionAttribute")) continue;
            var blob = metadata.GetBlobReader(attribute.Value);
            Require(blob.ReadUInt16() == 1, "Invalid assembly version attribute.");
            Require(attributes.TryAdd(attributeName, blob.ReadSerializedString() ?? ""), "Duplicate version attribute.");
        }
        Require(attributes.GetValueOrDefault("AssemblyInformationalVersionAttribute") == version &&
            attributes.GetValueOrDefault("AssemblyFileVersionAttribute") == version + ".0", "Compiled file/informational version mismatch or preview build: " + name);
        if (resourceName == null) return [];
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            if (metadata.GetString(resource.Name) != resourceName) continue;
            Require(resource.Implementation.IsNil, "Updater pin must be embedded, not an external resource.");
            var resources = pe.PEHeaders.CorHeader!.ResourcesDirectory;
            Require(resource.Offset <= int.MaxValue, "Resource offset too large.");
            var reader = pe.GetSectionData(resources.RelativeVirtualAddress).GetReader(checked((int)resource.Offset), checked(resources.Size - (int)resource.Offset));
            var length = reader.ReadInt32();
            Require(length is > 0 and <= 1048576 && length <= reader.RemainingBytes, "Invalid embedded pin size.");
            return reader.ReadBytes(length);
        }
        throw new InvalidDataException("Missing compiled updater pin: " + resourceName);
    }
    internal static void InspectRuntime(Dictionary<string, byte[]> bundle, string name, string runtime, bool desktop)
    {
        // CoreCLR/JIT are statically linked into Windows singlefilehost and are
        // independently verified via native PE exports and runtime-pack identity.
        foreach (var file in new[] { "System.Private.CoreLib.dll", name + ".dll", name + ".runtimeconfig.json" })
            Require(bundle.ContainsKey(file), "Self-contained single-file payload is incomplete: " + file);
        if (desktop) Require(bundle.ContainsKey("PresentationFramework.dll"), "Missing bundled WindowsDesktop runtime.");
        using var json = Json(bundle[name + ".runtimeconfig.json"]);
        var options = json.RootElement.GetProperty("runtimeOptions");
        Require(!options.TryGetProperty("framework", out _) && !options.TryGetProperty("frameworks", out _), "Release depends on a machine-wide runtime.");
        var frameworks = options.GetProperty("includedFrameworks").EnumerateArray().ToArray();
        var expected = desktop ? new[] { "Microsoft.NETCore.App", "Microsoft.WindowsDesktop.App" } : ["Microsoft.NETCore.App"];
        Require(frameworks.Length == expected.Length && frameworks.Select(x => Text(x, "name")).Order().SequenceEqual(expected.Order()) &&
            frameworks.All(x => Text(x, "version") == runtime), "Bundled framework version/identity mismatch.");
    }
    internal static void InspectHost(byte[] exe, string version)
    {
        using (var pe = new PEReader(new MemoryStream(exe, false)))
            Require(pe.PEHeaders.CoffHeader.Machine == Machine.Amd64 && !pe.HasMetadata, "Expected a native Windows x64 single-file apphost.");
        var temporary = Path.Combine(Path.GetTempPath(), "dindex-verify-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) output.Write(exe);
            var info = FileVersionInfo.GetVersionInfo(temporary);
            Require($"{info.FileMajorPart}.{info.FileMinorPart}.{info.FileBuildPart}.{info.FilePrivatePart}" == version + ".0" &&
                info.ProductVersion == version, "Final EXE native file/product version mismatch.");
        }
        finally { File.Delete(temporary); }
    }
    internal static object VerifyRelease(string directory, string version, string runtime, string runtimeReference)
    {
        Require(Regex.IsMatch(runtime, @"\A10\.0\.(0|[1-9][0-9]*)\z"), "Expected a stable .NET 10 runtime.");
        VerifyAllChecksums(directory);
        var hashes = VerifyFeed(directory, version);
        using var notes = Json(File.ReadAllBytes(Path.Combine(directory, "release-notes.json")));
        Require(Text(notes.RootElement, "version") == version, "Release notes version mismatch.");
        var reference = NativeRuntime.ReadReference(runtimeReference, runtime);
        var nativeProofs = new Dictionary<string, NativeRuntime.Identity>(StringComparer.Ordinal);
        foreach (var name in new[] { "DIndex", "DIndexUpdater" })
        {
            var exe = ReadSingleExe(Path.Combine(directory, name + ".zip"), name + ".exe");
            InspectHost(exe, version);
            var nativeRuntime = NativeRuntime.Inspect(exe);
            NativeRuntime.Match(nativeRuntime, reference);
            nativeProofs.Add(name, nativeRuntime);
            var bundle = ReadBundle(exe);
            InspectRuntime(bundle, name, runtime, name == "DIndex");
            var pin = InspectAssembly(bundle[name + ".dll"], name, version, name == "DIndexUpdater" ? "DIndexUpdater.LegacyUpdate.json" : null);
            if (name != "DIndexUpdater") continue;
            VerifyPin(pin, version, hashes.AppHash);
        }
        return new { kind = "PE-exports-and-runtime-info", reference, executables = nativeProofs };
    }
    internal static void VerifyPin(byte[] pin, string version, string hash)
    {
        using var metadata = Json(pin);
        Require(Text(metadata.RootElement, "version") == version && Text(metadata.RootElement, "downloadUrl") == ReleaseBase(version) + "/DIndex.zip", "Compiled updater pin version/URL mismatch.");
        RequireHash(Text(metadata.RootElement, "downloadSha256"), hash, "compiled updater pin");
    }
    static void SelfTest() => ReleaseTests.Run();
}
