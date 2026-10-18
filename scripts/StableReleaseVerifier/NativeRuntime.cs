using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

// .NET 10 Windows singlefilehost links coreclr_static, which links clrjit_static.
// Inspect actual PE exports/RVAs and the RuntimeInfo ABI, never substring presence.
// Sources: dotnet/runtime v10.0.0 src/native/corehost/apphost/static/{CMakeLists.txt,singlefilehost.def},
// src/coreclr/dlls/mscoree/coreclr/CMakeLists.txt, src/coreclr/debug/inc/runtimeinfo.h,
// and eng/native/genmoduleindex.cmd (Windows module index = timestamp + SizeOfImage).
internal static class NativeRuntime
{
    internal sealed record Identity(string FileVersion, string ModuleIndex, string[] Exports);
    internal sealed record Reference(string PackageVersion, string FileVersion, string ModuleIndex, string Sha256);
    internal static readonly string[] RequiredExports = ["g_CLREngineMetrics", "CLRJitAttachState", "DotNetRuntimeInfo", "g_dacTable", "MetaDataGetDispenser"];
    static bool IsImageRva(PEReader pe, int rva) => pe.PEHeaders.SectionHeaders.Any(section =>
        rva >= section.VirtualAddress && rva < (long)section.VirtualAddress + section.VirtualSize);
    internal static Identity Inspect(byte[] exe)
    {
        using var pe = new PEReader(new MemoryStream(exe, false));
        Program.Require(pe.PEHeaders.CoffHeader.Machine == Machine.Amd64 && !pe.HasMetadata, "Expected native x64 static-runtime apphost.");
        var exports = pe.PEHeaders.PEHeader!.ExportTableDirectory;
        Program.Require(exports.RelativeVirtualAddress > 0 && exports.Size >= 40, "Native runtime PE export directory is missing.");
        var table = pe.GetSectionData(exports.RelativeVirtualAddress).GetReader(0, 40);
        table.Offset = 20;
        var functionCount = checked((int)table.ReadUInt32());
        var nameCount = checked((int)table.ReadUInt32());
        var functionsRva = checked((int)table.ReadUInt32());
        var namesRva = checked((int)table.ReadUInt32());
        var ordinalsRva = checked((int)table.ReadUInt32());
        Program.Require(functionCount is > 0 and <= 4096 && nameCount is > 0 and <= 4096 && nameCount <= functionCount, "Invalid native runtime export table counts.");
        var functions = pe.GetSectionData(functionsRva).GetReader(0, checked(functionCount * 4));
        var names = pe.GetSectionData(namesRva).GetReader(0, checked(nameCount * 4));
        var ordinals = pe.GetSectionData(ordinalsRva).GetReader(0, checked(nameCount * 2));
        var entries = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < nameCount; i++)
        {
            var nameRva = checked((int)names.ReadUInt32());
            var section = pe.GetSectionData(nameRva);
            var text = section.GetReader(0, Math.Min(section.Length, 256));
            var builder = new StringBuilder();
            var terminated = false;
            while (text.RemainingBytes > 0)
            {
                var value = text.ReadByte();
                if (value == 0) { terminated = true; break; }
                Program.Require(value is >= 32 and <= 126, "Invalid native export name.");
                builder.Append((char)value);
            }
            Program.Require(terminated && builder.Length > 0, "Unterminated native export name.");
            var ordinal = ordinals.ReadUInt16();
            Program.Require(ordinal < functionCount, "Native export ordinal is out of range.");
            functions.Offset = ordinal * 4;
            var rva = checked((int)functions.ReadUInt32());
            Program.Require(entries.TryAdd(builder.ToString(), rva), "Duplicate native export name.");
        }
        foreach (var name in RequiredExports)
        {
            Program.Require(entries.TryGetValue(name, out var rva) && rva > 0 &&
                !(rva >= exports.RelativeVirtualAddress && rva < (long)exports.RelativeVirtualAddress + exports.Size) &&
                IsImageRva(pe, rva), "Missing or forwarded static CoreCLR export: " + name);
        }
        var data = pe.GetSectionData(entries["DotNetRuntimeInfo"]).GetReader(0, 112);
        var signature = data.ReadBytes(18);
        Program.Require(signature.AsSpan().SequenceEqual(Encoding.ASCII.GetBytes("DotNetRuntimeInfo\0")), "Invalid exported native runtime info signature.");
        data.Offset = 20; // C ABI alignment after char Signature[18].
        Program.Require(data.ReadInt32() == 2, "Unsupported native runtime info structure version.");
        var moduleIndex = data.ReadBytes(24);
        Program.Require(moduleIndex[0] == 8 && moduleIndex.AsSpan(9).IndexOfAnyExcept((byte)0) == -1, "Invalid Windows runtime module identity.");
        data.Offset = 96; // Signature/padding/version + three 24-byte module indices.
        var components = new[] { data.ReadInt32(), data.ReadInt32(), data.ReadInt32(), data.ReadInt32() };
        Program.Require(components.All(x => x is >= 0 and <= 65535), "Invalid native runtime version.");
        if (components[0] >= 10)
        {
            Program.Require(entries.TryGetValue("DotNetRuntimeContractDescriptor", out var descriptor) && descriptor > 0 &&
                !(descriptor >= exports.RelativeVirtualAddress && descriptor < (long)exports.RelativeVirtualAddress + exports.Size) &&
                IsImageRva(pe, descriptor), "Missing .NET 10 static runtime contract export.");
        }
        return new Identity(string.Join('.', components), Convert.ToHexString(moduleIndex.AsSpan(1, 8)), entries.Keys.Order(StringComparer.Ordinal).ToArray());
    }
    internal static Reference ReadReference(string path, string runtime)
    {
        var full = Path.GetFullPath(path).Replace('\\', '/');
        Program.Require(full.Contains("/microsoft.netcore.app.runtime.win-x64/" + runtime + "/", StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(path).Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase), "Reference must be coreclr.dll from the exact restored runtime pack.");
        using var input = File.OpenRead(path);
        using var pe = new PEReader(input);
        Program.Require(pe.PEHeaders.CoffHeader.Machine == Machine.Amd64 && !pe.HasMetadata, "Invalid official CoreCLR reference image.");
        var version = FileVersionInfo.GetVersionInfo(path);
        var parts = runtime.Split('.');
        Program.Require(version.FileMajorPart == int.Parse(parts[0]) && version.FileMinorPart == int.Parse(parts[1]), "CoreCLR reference major/minor mismatch.");
        var index = new byte[8];
        BitConverter.GetBytes(pe.PEHeaders.CoffHeader.TimeDateStamp).CopyTo(index, 0);
        BitConverter.GetBytes(pe.PEHeaders.PEHeader!.SizeOfImage).CopyTo(index, 4);
        return new Reference(runtime, $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}", Convert.ToHexString(index), Program.Hash(path));
    }
    internal static void Match(Identity identity, Reference reference)
    {
        Program.Require(identity.FileVersion == reference.FileVersion && identity.ModuleIndex == reference.ModuleIndex,
            "Statically linked CoreCLR version/module identity differs from the exact restored runtime pack.");
    }
}
