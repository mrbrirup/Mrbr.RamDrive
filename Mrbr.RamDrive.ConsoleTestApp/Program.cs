using HighSpeedRamDrive;
using Mrbr.RamDrive.ConsoleTestApp;
using System.ComponentModel;

return await ProgramEntry.RunAsync(args);

internal static class ProgramEntry {
    internal static async Task<int> RunAsync(string[] args) {
        Console.WriteLine("Mrbr.RamDrive Console Test App");
        Console.WriteLine("WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos");
        Console.WriteLine("https://github.com/winfsp/winfsp");
        Console.WriteLine();

        if (!TryParseArguments(args, out var profiles, out bool showHelp, out string? error)) {
            Console.Error.WriteLine(error);
            PrintUsage(Console.Error);
            return 2;
        }
        if (showHelp) {
            PrintUsage(Console.Out);
            return 0;
        }
        if (profiles == null) {
            foreach (var profile in RamDriveProfile.Presets)
                Console.WriteLine($"{profile.Name,-14} {profile.Description} ({profile.CapacityBytes / (1024 * 1024 * 1024)} GiB limit, {profile.ChunkSize / 1024} KiB chunks)");
            return 0;
        }

        if (!OperatingSystem.IsWindows()) {
            Console.Error.WriteLine("Mounting RAM drives requires Windows and the WinFsp runtime.");
            return 1;
        }

        try {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mrbr.RamDrive/1.0");
            var installer = new WinFspRuntimeInstaller(httpClient);
            var runtimeSetup = new WinFspRuntimeSetup(WinFspRuntime.Check,
                () => installer.InstallAsync(Console.Out), Console.In, Console.Out);
            int setupStatus = await runtimeSetup.EnsureInstalledAsync();
            if (setupStatus != 0)
                return setupStatus;

            var drives = new List<RamDriveSession>();
            try {
                foreach (var profile in profiles) {
                    RamDriveSession drive = RamDriveFactory.CreateDrive(profile);
                    drives.Add(drive);
                    Console.WriteLine($"Mounted {profile.Name} at {drive.MountPoint}: {profile.Description}; {drive.Options.CapacityBytes / (1024 * 1024 * 1024)} GiB limit, {drive.Options.ChunkSize / 1024} KiB chunks.");
                }

                Console.WriteLine("Press Enter to unmount all RAM drives. Their files are temporary.");
                Console.ReadLine();
                return 0;
            } finally {
                for (int i = drives.Count - 1; i >= 0; i--) {
                    RamDriveSession drive = drives[i];
                    await drive.DisposeAsync();
                    Console.WriteLine($"Unmounted {drive.MountPoint}.");
                }
            }
        } catch (Exception ex) when (ex is TypeInitializationException or DllNotFoundException or
            BadImageFormatException or EntryPointNotFoundException) {
            Console.Error.WriteLine($"Unable to load WinFsp: {ex.GetBaseException().Message}");
            Console.Error.WriteLine("Check the WinFsp installation and restart this app. An incompatible DLL beside the app can override the installed runtime.");
            return 1;
        } catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) {
            Console.Error.WriteLine("WinFsp installation was cancelled at the Windows administrator prompt.");
            return 1;
        } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or
            UnauthorizedAccessException or Win32Exception or InvalidOperationException or System.Security.SecurityException) {
            Console.Error.WriteLine($"WinFsp setup failed: {ex.GetBaseException().Message}");
            Console.Error.WriteLine($"Check your connection and permissions, then try again. Matching manual installer: {WinFspRuntimeInstaller.ReleaseUrl}");
            return 1;
        }
    }

    private static bool TryParseArguments(string[] args, out IReadOnlyList<RamDriveProfile>? profiles,
        out bool showHelp, out string? error) {
        profiles = null;
        showHelp = false;
        error = null;
        if (args.Length == 0) {
            profiles = [RamDriveProfile.Presets[0]];
            return true;
        }

        if (args.Length == 1 && args[0] is "--help" or "-h" or "/?") {
            showHelp = true;
            return true;
        }
        if (args.Length == 1 && args[0] == "--list")
            return true;
        if (args.Length != 2 || args[0] != "--preset") {
            error = "Specify --preset followed by one or more comma-separated workload names.";
            return false;
        }

        string[] names = args[1].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (names.Length == 0) {
            error = "At least one workload name is required.";
            return false;
        }
        if (names.Length == 1 && names[0].Equals("all", StringComparison.OrdinalIgnoreCase)) {
            profiles = RamDriveProfile.Presets;
            return true;
        }

        var selected = new List<RamDriveProfile>(names.Length);
        foreach (string name in names) {
            var profile = RamDriveProfile.Presets.FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (profile == null) {
                error = $"Unknown workload '{name}'. Use --list to see available workload defaults.";
                return false;
            }
            if (selected.Any(item => item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))) {
                error = $"Workload '{profile.Name}' was specified more than once.";
                return false;
            }
            selected.Add(profile);
        }
        profiles = selected;
        return true;
    }

    private static void PrintUsage(TextWriter output) {
        output.WriteLine("Usage:");
        output.WriteLine("  Mrbr.RamDrive.ConsoleTestApp                 Mount the default video workload on the last available letter");
        output.WriteLine("  Mrbr.RamDrive.ConsoleTestApp --list          List workload defaults (no fixed letters)");
        output.WriteLine("  Mrbr.RamDrive.ConsoleTestApp --preset video,images,database");
        output.WriteLine("  Mrbr.RamDrive.ConsoleTestApp --preset all    Mount all available presets");
        output.WriteLine("Press Enter to unmount every drive mounted by this process.");
    }
}
