using System.Diagnostics;
using System.Text.Json;
using System.Threading;

internal static class RuntimeSmoke
{
    static ProcessStartInfo StartInfo(string executable, string root, string profile)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = root };
        start.Environment["DINDEX_DATA_DIR"] = profile;
        start.Environment["DOTNET_ROOT"] = Path.Combine(root, "absent-runtime");
        start.Environment["DOTNET_ROOT_X64"] = Path.Combine(root, "absent-runtime");
        start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
        start.Environment["DOTNET_ROLL_FORWARD"] = "Disable";
        start.Environment["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = Path.Combine(root, "bundle-extraction");
        start.Environment["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return start;
    }
    internal static void Run(string app, string updater, string version, string root)
    {
        Program.Require(OperatingSystem.IsWindows(), "The new-executable runtime smoke test requires Windows.");
        Program.Require(!Directory.Exists(root) && !File.Exists(root), "Runtime smoke output must be a new directory.");
        Program.InspectHost(File.ReadAllBytes(app), version);
        Program.InspectHost(File.ReadAllBytes(updater), version);
        Directory.CreateDirectory(root);
        var profile = Path.Combine(root, "synthetic-profile");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "settings.json"), "{\"SearchRoots\":[],\"ResultLimit\":500}");
        var appCopy = Path.Combine(root, "DIndex.exe");
        var updaterCopy = Path.Combine(root, "DIndexUpdater.exe");
        File.Copy(app, appCopy, false); File.Copy(updater, updaterCopy, false);
        // A synthetic marker disables the app's automatic network update check;
        // it exists only beside this disposable COPY, never in the release ZIP.
        File.WriteAllText(Path.Combine(root, "installed-by-msi.json"), "{\"fixture\":true}");
        using var application = new Process { StartInfo = StartInfo(appCopy, root, profile) };
        var applicationStarted = false;
        try
        {
            applicationStarted = application.Start();
            Program.Require(applicationStarted, "Could not start the verified new application.");
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(3))
            {
                Program.Require(!application.HasExited, "New application exited during isolated runtime startup.");
                Thread.Sleep(100);
            }
            Program.Require(File.Exists(Path.Combine(profile, ".instance.lock")), "New app did not reach its managed profile initialization.");
            Program.Require(!File.Exists(Path.Combine(profile, "error.log")), "New app reported an isolated startup error.");
            foreach (ProcessModule module in application.Modules)
            {
                var path = Path.GetFullPath(module.FileName);
                foreach (var basePath in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                    if (!string.IsNullOrEmpty(basePath)) Program.Require(!path.StartsWith(Path.Combine(basePath, "dotnet") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "New app loaded a machine-wide .NET native module: " + path);
            }
        }
        finally
        {
            if (applicationStarted && !application.HasExited) { application.Kill(true); Program.Require(application.WaitForExit(5000), "Could not stop the disposable app smoke process."); }
        }
        // Exercise managed updater argument validation only. No package is read,
        // downloaded or installed, and its expected log is confined to the fixture.
        var start = StartInfo(updaterCopy, root, profile);
        start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        foreach (var argument in new[] { "--processId", Environment.ProcessId.ToString(), "--appPath", Path.Combine(root, "missing", "DIndex.exe"), "--appZipPath", Path.Combine(root, "missing.zip"), "--appSha256", new string('A', 64), "--dataDirectory", profile }) start.ArgumentList.Add(argument);
        using var update = new Process { StartInfo = start };
        Program.Require(update.Start(), "Could not start the verified new updater.");
        var stdout = update.StandardOutput.ReadToEndAsync(); var stderr = update.StandardError.ReadToEndAsync();
        if (!update.WaitForExit(10000)) { update.Kill(true); update.WaitForExit(5000); throw new InvalidDataException("New updater did not finish the isolated preflight."); }
        var error = stderr.GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(root, "updater-stdout.txt"), stdout.GetAwaiter().GetResult());
        File.WriteAllText(Path.Combine(root, "updater-stderr.txt"), error);
        Program.Require(update.ExitCode == 1 && error.Contains("The existing DIndex.exe was not found.", StringComparison.Ordinal), "New updater did not reach the expected safe managed preflight failure.");
        File.WriteAllText(Path.Combine(root, "smoke-proof.json"), JsonSerializer.Serialize(new
        {
            version, isolatedProfile = true, appManagedStartup = true, updaterManagedPreflight = true,
            perProcessRuntimeRootsInvalidated = true, globalSettingsChanged = false,
            limitation = "The CI host still has SDKs installed. This is startup corroboration; PE runtime-pack identity and bundled framework checks are the independent self-contained proof."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
