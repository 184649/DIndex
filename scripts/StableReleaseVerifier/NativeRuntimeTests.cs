using System.Text;

internal static class NativeRuntimeTests
{
    internal static byte[] Fixture()
    {
        var bytes = new byte[4096];
        void U16(int offset, ushort value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
        void U32(int offset, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
        void U64(int offset, ulong value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; U32(0x3c, 0x80);
        U32(0x80, 0x4550); U16(0x84, 0x8664); U16(0x86, 1); U32(0x88, 0x12345678);
        U16(0x94, 240); U16(0x96, 0x22);
        const int optional = 0x98;
        U16(optional, 0x20b); U32(optional + 16, 0x1400); U64(optional + 24, 0x140000000);
        U32(optional + 32, 0x1000); U32(optional + 36, 0x200);
        U32(optional + 56, 0x2000); U32(optional + 60, 0x200); U16(optional + 68, 3);
        U32(optional + 108, 16); U32(optional + 112, 0x1000); U32(optional + 116, 0x200);
        const int section = optional + 240;
        Encoding.ASCII.GetBytes(".rdata").CopyTo(bytes, section);
        U32(section + 8, 0xe00); U32(section + 12, 0x1000); U32(section + 16, 0xe00); U32(section + 20, 0x200); U32(section + 36, 0x40000040);
        var names = NativeRuntime.RequiredExports.Append("DotNetRuntimeContractDescriptor").ToArray();
        U32(0x210, 1); U32(0x214, (uint)names.Length); U32(0x218, (uint)names.Length);
        U32(0x21c, 0x1040); U32(0x220, 0x1080); U32(0x224, 0x10c0);
        var text = 0x300;
        for (var i = 0; i < names.Length; i++)
        {
            U32(0x240 + i * 4, names[i] == "DotNetRuntimeInfo" ? 0x1300u : (uint)(0x1400 + i * 8));
            U32(0x280 + i * 4, (uint)(text - 0x200 + 0x1000)); U16(0x2c0 + i * 2, (ushort)i);
            var name = Encoding.ASCII.GetBytes(names[i] + "\0"); name.CopyTo(bytes, text); text += name.Length;
        }
        Encoding.ASCII.GetBytes("DotNetRuntimeInfo\0").CopyTo(bytes, 0x500);
        U32(0x514, 2); bytes[0x518] = 8; U32(0x519, 0x12345678); U32(0x51d, 0x2000);
        U32(0x560, 10); U32(0x564, 0); U32(0x568, 1326); U32(0x56c, 12345);
        return bytes;
    }
    internal static void Run(Action<string, Action> test, Action<Action> reject)
    {
        var reference = new NativeRuntime.Reference("10.0.13", "10.0.1326.12345", "7856341200200000", new string('A', 64));
        test("native PE exports/runtime ABI match exact pack identity", () => NativeRuntime.Match(NativeRuntime.Inspect(Fixture()), reference));
        test("accept legitimate zero-initialized PE data export", () => { var bytes = Fixture(); BitConverter.GetBytes(0x1000u).CopyTo(bytes, 0x98 + 240 + 8); BitConverter.GetBytes(0x1f00u).CopyTo(bytes, 0x244); NativeRuntime.Match(NativeRuntime.Inspect(bytes), reference); });
        test("reject runtime export outside PE virtual image", () => { var bytes = Fixture(); BitConverter.GetBytes(0x2000u).CopyTo(bytes, 0x244); reject(() => NativeRuntime.Inspect(bytes)); });
        test("reject missing static CoreCLR export", () => { var bytes = Fixture(); bytes[0x300] = (byte)'x'; reject(() => NativeRuntime.Inspect(bytes)); });
        test("reject forwarded native CoreCLR export", () => { var bytes = Fixture(); BitConverter.GetBytes(0x1100u).CopyTo(bytes, 0x240); reject(() => NativeRuntime.Inspect(bytes)); });
        test("reject missing native export directory", () => { var bytes = Fixture(); BitConverter.GetBytes(0u).CopyTo(bytes, 0x98 + 112); reject(() => NativeRuntime.Inspect(bytes)); });
        test("reject invalid native runtime ABI version", () => { var bytes = Fixture(); BitConverter.GetBytes(3u).CopyTo(bytes, 0x514); reject(() => NativeRuntime.Inspect(bytes)); });
        test("reject incorrect native runtime data signature", () => { var bytes = Fixture(); bytes[0x500] = (byte)'x'; reject(() => NativeRuntime.Inspect(bytes)); });
        test("reject invalid Windows module index", () => { var bytes = Fixture(); bytes[0x518] = 7; reject(() => NativeRuntime.Inspect(bytes)); });
        test("reject absent .NET10 runtime contract export", () => { var bytes = Fixture(); BitConverter.GetBytes(5u).CopyTo(bytes, 0x218); reject(() => NativeRuntime.Inspect(bytes)); });
        test("reject stale statically linked runtime version", () => reject(() => NativeRuntime.Match(NativeRuntime.Inspect(Fixture()), reference with { FileVersion = "10.0.1226.12345" })));
        test("reject different native runtime module identity", () => reject(() => NativeRuntime.Match(NativeRuntime.Inspect(Fixture()), reference with { ModuleIndex = "0000000000000000" })));
        test("reject arbitrary unpinned runtime reference path", () => reject(() => NativeRuntime.ReadReference(Path.Combine(Path.GetTempPath(), "coreclr.dll"), "10.0.13")));
        test("raw export-name strings cannot prove static runtime", () => reject(() => NativeRuntime.Inspect(Encoding.ASCII.GetBytes(string.Join('\0', NativeRuntime.RequiredExports)))));
    }
}
