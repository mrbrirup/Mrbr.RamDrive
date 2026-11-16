namespace Mrbr.RamDrive.ConsoleTestApp;

internal sealed class WinFspRuntimeSetup(
    Func<RuntimeStatus> checkRuntime,
    Func<Task<int>> installRuntime,
    TextReader input,
    TextWriter output) {

    internal async Task<int> EnsureInstalledAsync() {
        RuntimeStatus runtime = checkRuntime();
        output.WriteLine(runtime.Message);
        if (runtime.IsCompatible)
            return 0;

        if (!WinFspRuntime.IsCompatible(WinFspRuntimeInstaller.PackageVersion, WinFspRuntime.RequiredVersion)) {
            output.WriteLine("Update the pinned installer manifest to match the winfsp.net package before installing.");
            return 1;
        }

        output.WriteLine($"The matching installer is WinFsp {WinFspRuntimeInstaller.PackageVersion} (2026 Beta5).");
        output.Write("Download and install it now? [y/N] ");
        string? answer = input.ReadLine()?.Trim();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)) {
            output.WriteLine($"Installation skipped. Run the app again to install WinFsp. Matching manual installer: {WinFspRuntimeInstaller.ReleaseUrl}");
            return 1;
        }

        int exitCode = await installRuntime();
        switch (exitCode) {
            case 0:
                runtime = checkRuntime();
                output.WriteLine(runtime.Message);
                if (!runtime.IsCompatible) {
                    output.WriteLine("Installation finished, but the runtime is still unavailable. Check the installer log and restart Windows if required.");
                    output.WriteLine("An app-local WinFsp DLL may be overriding the installed runtime.");
                    return 1;
                }
                output.WriteLine("WinFsp installation complete. Continuing to mount the RAM drive...");
                return 0;
            case 3010:
            case 1641:
                output.WriteLine("WinFsp installation succeeded. Restart Windows, then run this app again to mount the drive.");
                return 3010;
            case 1602:
                output.WriteLine("WinFsp installation was cancelled.");
                return exitCode;
            case 1618:
                output.WriteLine("Another Windows installation is in progress. Wait for it to finish, then try again.");
                return exitCode;
            default:
                output.WriteLine($"WinFsp installation failed (Windows Installer exit code {exitCode}). Check the installer log shown above.");
                return exitCode;
        }
    }
}
