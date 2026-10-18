using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DIndex.Updating;
using DIndexUpdater;
using Updater = DIndexUpdater.Program;

namespace StorageSafetyTests;

internal static partial class Program
{
    private const string PinnedUrl = "https://github.com/184649/DIndex/releases/download/v1.1.2/DIndex.zip";
    private static readonly Version OldVersion = new(1, 0, 1, 0);
    private static readonly Version NewVersion = new(1, 1, 2, 0);

    private static string[] LegacyArgs(string app, string url = PinnedUrl) => new[]
    { "--processId", int.MaxValue.ToString(), "--appPath", app, "--appZip", url, "--restart" };

    private static byte[] PinnedJson(string hash, string url = PinnedUrl, string version = "1.1.2") =>
        JsonSerializer.SerializeToUtf8Bytes(new { version, downloadUrl = url, downloadSha256 = hash });

    private static Task<int> RunUpdaterAsync(string[] args, HttpClient client,
        Func<LegacyUpdateMetadata> metadata, Func<string, Version>? version = null, Func<ProcessStartInfo, Process>? start = null) =>
        Updater.RunAsync(args, client, metadata, version, start, (_, _) => { }); // Never log into a real user profile.

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _response;
        internal readonly List<Uri> Requests = new();
        internal StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> response) => _response = response;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!);
            return Task.FromResult(_response(request));
        }
    }

    private sealed class WaitingTestProcess : IDisposable
    {
        internal Process Process { get; }
        private readonly string _exitFile;

        internal WaitingTestProcess(Process process, string exitFile) { Process = process; _exitFile = exitFile; }

        public void Dispose()
        {
            try
            {
                if (Process.HasExited) return;
                File.WriteAllText(_exitFile, "exit");
                if (!Process.WaitForExit(5000))
                {
                    // This is only the child created and retained by this fixture, never a PID lookup.
                    Process.Kill(entireProcessTree: false);
                    if (!Process.WaitForExit(5000)) throw new TimeoutException("The owned test child did not stop.");
                }
            }
            finally { Process.Dispose(); }
        }
    }

    private static WaitingTestProcess StartWaitingTestProcess(string exitFile)
    {
        var start = new ProcessStartInfo { FileName = Environment.ProcessPath!, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--wait-for-exit-file"); start.ArgumentList.Add(exitFile);
        var fixture = new WaitingTestProcess(Process.Start(start)!, exitFile);
        try
        {
            // Process.Start does not guarantee that Windows has loaded MainModule yet.
            // Establish the controlled child's managed-code readiness before testing identity.
            var readyFile = exitFile + ".ready"; var elapsed = Stopwatch.StartNew();
            while (!File.Exists(readyFile))
            {
                if (fixture.Process.HasExited) throw new InvalidOperationException($"Test child {fixture.Process.Id} exited before readiness (code {fixture.Process.ExitCode}).");
                if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException($"Test child {fixture.Process.Id} did not signal readiness.");
                Thread.Sleep(10);
            }
            using var ready = JsonDocument.Parse(File.ReadAllText(readyFile));
            var childPid = ready.RootElement.GetProperty("processId").GetInt32();
            var childPath = ready.RootElement.GetProperty("processPath").GetString();
            var inspectedPath = fixture.Process.MainModule?.FileName;
            True(childPid == fixture.Process.Id && childPath is not null && inspectedPath is not null &&
                Path.GetFullPath(childPath).Equals(Path.GetFullPath(start.FileName), StringComparison.OrdinalIgnoreCase) &&
                Path.GetFullPath(inspectedPath).Equals(Path.GetFullPath(start.FileName), StringComparison.OrdinalIgnoreCase),
                $"Ready child identity mismatch: started PID={fixture.Process.Id}, ready PID={childPid}, expected='{start.FileName}', ready='{childPath ?? "<null>"}', inspected='{inspectedPath ?? "<null>"}'.");
            return fixture;
        }
        catch { fixture.Dispose(); throw; }
    }

    private static async Task LegacyUpdaterTests()
    {
        await Test("Legacy exact public CLI shape is recognized; modern URL/local SHA paths stay modern", () => Run(() =>
        {
            var old = Updater.ParseArgs(LegacyArgs(Path.Combine(Root, "DIndex.exe")));
            True(Updater.IsLegacyInvocation(old), "Public CLI was rejected.");
            old.Remove("restart");
            True(Updater.IsLegacyInvocation(old), "Optional restart could not be omitted for a nonlaunching update.");
            old.Add("appSha256", new string('a', 64));
            True(!Updater.IsLegacyInvocation(old), "Modern URL SHA path was treated as legacy.");
            old.Remove("appZip"); old.Add("appZipPath", Path.Combine(Root, "app.zip"));
            True(!Updater.IsLegacyInvocation(old), "Modern local SHA path was treated as legacy.");
        }));
        foreach (var scenario in new[] { "local-package", "no-app-url", "profile", "handshake", "one-handshake", "both-packages" })
            await Test("Legacy rejects unsupported no-hash arguments: " + scenario, async () =>
            {
                var options = Updater.ParseArgs(LegacyArgs(Path.Combine(Root, "DIndex.exe")));
                if (scenario == "local-package") { options.Remove("appZip"); options.Add("appZipPath", "local.zip"); }
                if (scenario == "no-app-url") options.Remove("appZip");
                if (scenario == "profile") options.Add("dataDirectory", Root);
                if (scenario == "handshake") { options.Add("readyFile", Root); options.Add("proceedFile", Root); }
                if (scenario == "one-handshake") options.Add("readyFile", Root);
                if (scenario == "both-packages") options.Add("appZipPath", "local.zip");
                await Throws<ArgumentException>(() => Run(() => Updater.IsLegacyInvocation(options)));
            });
        await Test("Legacy fails closed when this updater has no embedded release pin", () =>
            Throws<InvalidDataException>(() => Run(() => LegacyUpdateMetadata.LoadEmbedded())));
        await Test("Legacy profile inherits validated isolation override or retains normal LocalAppData default", async () =>
        {
            var previous = Environment.GetEnvironmentVariable("DINDEX_DATA_DIR");
            var options = Updater.ParseArgs(LegacyArgs(Path.Combine(Root, "DIndex.exe")));
            try
            {
                Environment.SetEnvironmentVariable("DINDEX_DATA_DIR", null);
                True(Updater.ResolveDataDirectory(options) is null, "Legacy default profile was changed.");
                Environment.SetEnvironmentVariable("DINDEX_DATA_DIR", Root);
                Equal(Path.GetFullPath(Root), Updater.ResolveDataDirectory(options));
                options.Add("dataDirectory", Path.Combine(Root, "explicit"));
                Equal(Path.Combine(Root, "explicit"), Updater.ResolveDataDirectory(options));
                options.Remove("dataDirectory");
                Environment.SetEnvironmentVariable("DINDEX_DATA_DIR", "relative-profile");
                await Throws<ArgumentException>(() => Run(() => Updater.ResolveDataDirectory(options)));
            }
            finally { Environment.SetEnvironmentVariable("DINDEX_DATA_DIR", previous); }
        });
        await Test("Legacy early failure writes its log only to inherited isolated profile", async () =>
        {
            var directory = Fixture("legacy-isolated-log"); var app = Path.Combine(directory, "DIndex.exe"); File.WriteAllBytes(app, Executable(1));
            var profile = Path.Combine(directory, "isolated-profile"); var previousProfile = Environment.GetEnvironmentVariable("DINDEX_DATA_DIR");
            using var handler = new StubHttpHandler(_ => throw new Exception("Missing pin must not reach network.")); using var client = new HttpClient(handler);
            using var error = new StringWriter(); var previousError = Console.Error;
            try
            {
                Environment.SetEnvironmentVariable("DINDEX_DATA_DIR", profile); Console.SetError(error);
                Equal(1, await Updater.RunAsync(LegacyArgs(app).SkipLast(1).ToArray(), client));
            }
            finally { Environment.SetEnvironmentVariable("DINDEX_DATA_DIR", previousProfile); Console.SetError(previousError); }
            True(File.ReadAllText(Path.Combine(profile, "updater.log")).Contains("does not include a verified legacy update package"), "Inherited profile did not receive the failure log.");
            True(Executable(1).SequenceEqual(File.ReadAllBytes(app)), "Early logging failure changed installed bytes.");
            Equal(0, handler.Requests.Count);
        });
        await Test("Legacy valid pinned metadata accepts public 1.0.1 and UTF8 BOM", () => Run(() =>
        {
            var hash = new string('a', 64);
            var metadata = LegacyUpdateMetadata.Parse(new byte[] { 0xef, 0xbb, 0xbf }.Concat(PinnedJson(hash)).ToArray());
            Equal(hash, metadata.GetVerifiedHash(PinnedUrl, "unused", _ => OldVersion));
            metadata.ValidateExecutableVersion("unused", _ => NewVersion);
        }));
        var invalidMetadata = new[] { "", "{", "null", "[]", "{}", "{\"version\":\"1.1.2\"}",
                     "{\"version\":\"1.1.2\",\"version\":\"1.1.2\",\"downloadUrl\":\"" + PinnedUrl + "\",\"downloadSha256\":\"" + new string('a', 64) + "\"}",
                     "{\"version\":null,\"downloadUrl\":\"" + PinnedUrl + "\",\"downloadSha256\":\"" + new string('a', 64) + "\"}", new string(' ', 16 * 1024 + 1) };
        for (var index = 0; index < invalidMetadata.Length; index++)
        {
            var json = invalidMetadata[index];
            await Test("Legacy malformed/missing/duplicate/bounded pin rejects input " + index, () =>
                Throws<InvalidDataException>(() => Run(() => LegacyUpdateMetadata.Parse(Encoding.UTF8.GetBytes(json)))));
        }
        foreach (var hash in new[] { "", "abcd", new string('z', 64), new string('0', 63), new string('0', 65) })
            await Test("Legacy pin rejects invalid SHA256: " + hash.Length, () =>
                Throws<InvalidDataException>(() => Run(() => LegacyUpdateMetadata.Parse(PinnedJson(hash)))));
        foreach (var url in new[] { "http://github.com/184649/DIndex/releases/download/v1.1.2/DIndex.zip",
                     "https://evil.example/DIndex.zip", "https://github.com/other/DIndex/releases/download/v1.1.2/DIndex.zip",
                     "https://raw.githubusercontent.com/Chairman-bits/DIndex/main/DIndex.zip",
                     PinnedUrl + "?changed=1", PinnedUrl.Replace("v1.1.2", "v1.1.3") })
            await Test("Legacy pin rejects untrusted/mutable/wrong-version URL: " + url, () =>
                Throws<InvalidDataException>(() => Run(() => LegacyUpdateMetadata.Parse(PinnedJson(new string('a', 64), url)))));
        await Test("Legacy exact pin rejects mismatched requested URL and downgrade/equal versions", async () =>
        {
            var metadata = LegacyUpdateMetadata.Parse(PinnedJson(new string('a', 64)));
            await Throws<InvalidDataException>(() => Run(() => metadata.GetVerifiedHash(PinnedUrl.Replace("DIndex.zip", "Other.zip"), "unused", _ => OldVersion)));
            await Throws<InvalidDataException>(() => Run(() => metadata.GetVerifiedHash(PinnedUrl.Replace("github.com", "GITHUB.COM"), "unused", _ => OldVersion)));
            await Throws<InvalidDataException>(() => Run(() => metadata.GetVerifiedHash(PinnedUrl, "unused", _ => NewVersion)));
            await Throws<InvalidDataException>(() => Run(() => metadata.GetVerifiedHash(PinnedUrl, "unused", _ => new Version(9, 0, 0, 0))));
            await Throws<InvalidDataException>(() => Run(() => metadata.ValidateExecutableVersion("unused", _ => OldVersion)));
            var noVersion = Path.Combine(Fixture("no-version"), "DIndex.exe"); File.WriteAllBytes(noVersion, Executable());
            await Throws<InvalidDataException>(() => Run(() => metadata.GetVerifiedHash(PinnedUrl, noVersion)));
        });
        foreach (var version in new[] { "", "1.1", "invalid", "1.1.2-preview.1" })
            await Test("Legacy pin rejects nonrelease version: " + version, () =>
                Throws<InvalidDataException>(() => Run(() => LegacyUpdateMetadata.Parse(PinnedJson(new string('a', 64),
                    $"https://github.com/184649/DIndex/releases/download/v{version}/DIndex.zip", version)))));

        foreach (var scenario in new[] { "missing-pin", "malformed-pin", "wrong-hash", "wrong-url", "untrusted-redirect", "redirect-loop", "http-error", "oversize", "invalid-pe", "wrong-exe-version", "wrong-pid", "msi", "locked" })
            await Test("Legacy failure preserves old EXE and profile: " + scenario, async () =>
            {
                var directory = Fixture("legacy-" + scenario); var app = Path.Combine(directory, "DIndex.exe");
                File.WriteAllBytes(app, Executable(1));
                var profile = Path.Combine(directory, "profile"); Directory.CreateDirectory(profile);
                File.WriteAllText(Path.Combine(profile, "settings.json"), "preserved settings");
                var oldProfile = Environment.GetEnvironmentVariable("DINDEX_DATA_DIR");
                Environment.SetEnvironmentVariable("DINDEX_DATA_DIR", profile);
                var zip = Zip(directory, new[] { ("DIndex.exe", scenario == "invalid-pe" ? new byte[256] : Executable(2), 0) });
                var bytes = File.ReadAllBytes(zip); var hash = Convert.ToHexString(SHA256.HashData(bytes));
                var pin = LegacyUpdateMetadata.Parse(PinnedJson(scenario == "wrong-hash" ? new string('0', 64) : hash));
                using var handler = new StubHttpHandler(_ =>
                {
                    var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                    if (scenario is "untrusted-redirect" or "redirect-loop")
                    {
                        response.StatusCode = HttpStatusCode.Redirect;
                        response.Headers.Location = new Uri(scenario == "untrusted-redirect" ? "https://evil.example/app.zip" : "https://release-assets.githubusercontent.com/loop");
                    }
                    if (scenario == "http-error") response.StatusCode = HttpStatusCode.ServiceUnavailable;
                    if (scenario == "oversize") response.Content.Headers.ContentLength = UpdatePackageSafety.MaximumPackageBytes + 1;
                    return response;
                });
                using var client = new HttpClient(handler);
                var args = LegacyArgs(app, scenario == "wrong-url" ? PinnedUrl.Replace("DIndex.zip", "Wrong.zip") : PinnedUrl);
                if (scenario == "wrong-pid") args[1] = Environment.ProcessId.ToString();
                if (scenario == "msi") File.WriteAllText(Path.Combine(directory, InstallationMode.MarkerFileName), "");
                using var locked = scenario == "locked" ? new FileStream(Path.Combine(directory, ".dindex-update.lock"), FileMode.Create, FileAccess.ReadWrite, FileShare.None) : null;
                var launches = 0; using var errors = new StringWriter(); var originalError = Console.Error;
                try
                {
                    Console.SetError(errors);
                    Equal(1, await RunUpdaterAsync(args, client,
                        () => scenario == "missing-pin" ? LegacyUpdateMetadata.LoadEmbedded() : scenario == "malformed-pin" ? LegacyUpdateMetadata.Parse(Encoding.UTF8.GetBytes("{}")) : pin,
                        path => path == app || scenario == "wrong-exe-version" ? OldVersion : NewVersion,
                        _ => { launches++; throw new Exception("No restart is safe without a verified original process."); }));
                }
                finally { Console.SetError(originalError); Environment.SetEnvironmentVariable("DINDEX_DATA_DIR", oldProfile); }
                Equal(0, launches); True(Executable(1).SequenceEqual(File.ReadAllBytes(app)), "Old executable changed.");
                True(!File.Exists(app + ".bak"), "Rejected package reached installation.");
                Equal(0, Directory.GetFiles(directory, "*.staged").Length);
                Equal("preserved settings", File.ReadAllText(Path.Combine(profile, "settings.json")));
                if (scenario is "missing-pin" or "malformed-pin" or "wrong-url" or "wrong-pid" or "msi" or "locked") Equal(0, handler.Requests.Count);
                if (scenario == "untrusted-redirect") Equal(1, handler.Requests.Count);
                if (scenario == "redirect-loop") Equal(6, handler.Requests.Count);
            });

        await Test("Legacy pinned package follows official asset redirect, verifies, installs and restarts", async () =>
        {
            var directory = Fixture("legacy-success"); var app = Path.Combine(directory, "DIndex.exe"); File.WriteAllBytes(app, Executable(1));
            var zip = Zip(directory, new[] { ("DIndex.exe", Executable(2), 0) }); var bytes = File.ReadAllBytes(zip);
            var pin = LegacyUpdateMetadata.Parse(PinnedJson(Convert.ToHexString(SHA256.HashData(bytes))));
            using var handler = new StubHttpHandler(request => request.RequestUri!.Host == "github.com"
                ? new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://release-assets.githubusercontent.com/verified?token=test") } }
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            using var client = new HttpClient(handler); var exitFile = Path.Combine(directory, "exit"); WaitingTestProcess? restarted = null;
            try
            {
                Equal(0, await RunUpdaterAsync(LegacyArgs(app), client, () => pin, path => path == app ? OldVersion : NewVersion, start =>
                {
                    Equal(app, start.FileName); Equal(directory, start.WorkingDirectory); Equal(false, start.UseShellExecute);
                    restarted = StartWaitingTestProcess(exitFile);
                    // The updater owns/disposes its handle; the fixture retains another for guaranteed cleanup.
                    return Process.GetProcessById(restarted.Process.Id);
                }));
                True(Executable(2).SequenceEqual(File.ReadAllBytes(app)), "Verified package not installed.");
                True(Executable(1).SequenceEqual(File.ReadAllBytes(app + ".bak")), "Original backup not retained.");
                Equal(2, handler.Requests.Count);
            }
            finally { restarted?.Dispose(); }
        });

        await Test("Modern local SHA and handshake succeed without consulting legacy metadata or network", async () =>
        {
            var directory = Fixture("modern-local"); var app = Path.Combine(directory, "DIndex.exe"); File.WriteAllBytes(app, Executable(1));
            var zip = Zip(directory, new[] { ("DIndex.exe", Executable(2), 0) });
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip)));
            var ready = Path.Combine(directory, "ready"); var proceed = Path.Combine(directory, "proceed");
            UpdatePackageSafety.WriteSignal(proceed);
            using var handler = new StubHttpHandler(_ => throw new Exception("Local package must not use network.")); using var client = new HttpClient(handler);
            Equal(0, await RunUpdaterAsync(new[] { "--processId", int.MaxValue.ToString(), "--appPath", app, "--appZipPath", zip,
                "--appSha256", hash, "--readyFile", ready, "--proceedFile", proceed }, client,
                () => throw new Exception("Modern updates must not depend on the legacy pin."), _ => throw new Exception("Modern version behavior must stay unchanged.")));
            True(UpdatePackageSafety.HasValidSignal(ready), "Readiness signal missing.");
            True(Executable(2).SequenceEqual(File.ReadAllBytes(app)), "Modern update did not install."); Equal(0, handler.Requests.Count);
        });
        await Test("Legacy optional restart omitted installs without launching either executable", async () =>
        {
            var directory = Fixture("legacy-no-restart"); var app = Path.Combine(directory, "DIndex.exe"); File.WriteAllBytes(app, Executable(1));
            var zip = Zip(directory, new[] { ("DIndex.exe", Executable(2), 0) }); var bytes = File.ReadAllBytes(zip);
            var pin = LegacyUpdateMetadata.Parse(PinnedJson(Convert.ToHexString(SHA256.HashData(bytes))));
            using var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); using var client = new HttpClient(handler);
            Equal(0, await RunUpdaterAsync(LegacyArgs(app).SkipLast(1).ToArray(), client, () => pin,
                path => path == app ? OldVersion : NewVersion, _ => throw new Exception("Restart was not requested.")));
            True(Executable(2).SequenceEqual(File.ReadAllBytes(app)), "Optional restart changed install behavior.");
            True(Executable(1).SequenceEqual(File.ReadAllBytes(app + ".bak")), "Old executable backup missing.");
        });
        await Test("Modern malformed explicit SHA never falls back to the legacy pin", async () =>
        {
            var directory = Fixture("modern-bad-hash"); var app = Path.Combine(directory, "DIndex.exe"); File.WriteAllBytes(app, Executable(1));
            using var handler = new StubHttpHandler(_ => throw new Exception("Malformed hash must not reach network.")); using var client = new HttpClient(handler);
            using var errors = new StringWriter(); var previousError = Console.Error;
            try
            {
                Console.SetError(errors);
                Equal(1, await RunUpdaterAsync(LegacyArgs(app).Concat(new[] { "--appSha256", "invalid" }).ToArray(), client,
                    () => throw new Exception("Explicit hash must not fall back to the legacy pin.")));
            }
            finally { Console.SetError(previousError); }
            Equal(0, handler.Requests.Count); True(Executable(1).SequenceEqual(File.ReadAllBytes(app)), "Malformed hash changed the old executable.");
        });
        await Test("Modern URL SHA path works without legacy metadata", async () =>
        {
            var directory = Fixture("modern-url"); var app = Path.Combine(directory, "DIndex.exe"); File.WriteAllBytes(app, Executable(1));
            var zip = Zip(directory, new[] { ("DIndex.exe", Executable(2), 0) }); var bytes = File.ReadAllBytes(zip);
            using var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }); using var client = new HttpClient(handler);
            Equal(0, await RunUpdaterAsync(new[] { "--processId", int.MaxValue.ToString(), "--appPath", app, "--appZip", PinnedUrl,
                "--appSha256", Convert.ToHexString(SHA256.HashData(bytes)) }, client, () => throw new Exception("Legacy pin consulted.")));
            True(Executable(2).SequenceEqual(File.ReadAllBytes(app)), "Modern URL update did not install."); Equal(1, handler.Requests.Count);
        });
        await Test("Legacy recovery never guesses absent or mismatched PID identity", async () =>
        {
            True(Updater.CaptureOriginalProcess(int.MaxValue, Environment.ProcessPath!) is null, "Absent PID was treated as verified.");
            Equal(false, await Updater.CanRecoverLegacyAsync(null, "unused", TimeSpan.Zero, _ => false));
            await Throws<InvalidOperationException>(() => Run(() => Updater.CaptureOriginalProcess(Environment.ProcessId, Path.Combine(Root, "DIndex.exe"))));
            using var verified = Updater.CaptureOriginalProcess(Environment.ProcessId, Environment.ProcessPath!);
            Equal(false, await Updater.CanRecoverLegacyAsync(verified, Environment.ProcessPath!, TimeSpan.FromMilliseconds(10), _ => false));
        });
        await Test("Legacy ready-process identity is stable across repeated child starts", () => Run(() =>
        {
            for (var iteration = 0; iteration < 20; iteration++)
            {
                var exitFile = Path.Combine(Fixture("legacy-ready-repeat"), "exit");
                using var fixture = StartWaitingTestProcess(exitFile);
                using var verified = Updater.CaptureOriginalProcess(fixture.Process.Id, Environment.ProcessPath!);
                True(verified is not null && !verified.HasExited, $"Ready child capture failed on iteration {iteration}.");
            }
        }));
        await Test("Legacy owned test child is stopped when an assertion fails", async () =>
        {
            Process? observed = null;
            try
            {
                await Throws<InvalidOperationException>(() => Run(() =>
                {
                    var exitFile = Path.Combine(Fixture("legacy-ready-cleanup"), "exit");
                    using var fixture = StartWaitingTestProcess(exitFile);
                    observed = Process.GetProcessById(fixture.Process.Id);
                    _ = observed.SafeHandle;
                    throw new InvalidOperationException("Injected assertion failure after child readiness.");
                }));
                True(observed is not null && observed.HasExited, "An owned test child survived the failed assertion.");
            }
            finally { observed?.Dispose(); }
        });
        await Test("Legacy recovery requires verified exit and no duplicate or uncertain process", async () =>
        {
            var directory = Fixture("legacy-recovery"); var exitFile = Path.Combine(directory, "exit");
            using var fixture = StartWaitingTestProcess(exitFile);
            using var verified = Updater.CaptureOriginalProcess(fixture.Process.Id, Environment.ProcessPath!);
            True(verified is not null, "Live matching process was not captured.");
            File.WriteAllText(exitFile, "exit"); await fixture.Process.WaitForExitAsync();
            Equal(true, await Updater.CanRecoverLegacyAsync(verified, Environment.ProcessPath!, TimeSpan.FromSeconds(1), _ => false));
            Equal(false, await Updater.CanRecoverLegacyAsync(verified, Environment.ProcessPath!, TimeSpan.FromSeconds(1), _ => true));
            Equal(false, await Updater.CanRecoverLegacyAsync(verified, Environment.ProcessPath!, TimeSpan.FromSeconds(1), _ => throw new IOException("Cannot inspect process.")));
            Equal(false, await Updater.CanRecoverLegacyAsync(verified, Environment.ProcessPath!, TimeSpan.FromSeconds(1)));
        });
        await Test("Legacy caller exiting between PID lookup and handle capture is not recovery proof", async () =>
        {
            var exitFile = Path.Combine(Fixture("legacy-capture-race"), "exit");
            using var caller = StartWaitingTestProcess(exitFile);
            // Match production's lazy GetProcessById result without first opening its handle.
            using var lookup = Process.GetProcessById(caller.Process.Id);
            File.WriteAllText(exitFile, "exit"); await caller.Process.WaitForExitAsync();
            using var captured = Updater.CaptureOriginalProcess(lookup, Environment.ProcessPath!);
            True(captured is null, "An exited-before-capture caller became recovery proof.");
            Equal(false, await Updater.CanRecoverLegacyAsync(captured, Environment.ProcessPath!, TimeSpan.Zero, _ => false));
        });
    }
}
