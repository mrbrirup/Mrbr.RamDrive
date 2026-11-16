using System.Diagnostics;
using System.Security.Cryptography;

namespace Mrbr.RamDrive.ConsoleTestApp;

internal sealed class WinFspRuntimeInstaller {
    // Keep this manifest in step with the winfsp.net PackageReference.
    // Upstream release and published SHA-256:
    // https://github.com/winfsp/winfsp/releases/tag/v2.2B5
    internal static readonly Version PackageVersion = new(2, 2, 26286);
    internal const string ReleaseUrl = "https://github.com/winfsp/winfsp/releases/tag/v2.2B5";
    internal static readonly Uri DownloadUri = new(
        "https://github.com/winfsp/winfsp/releases/download/v2.2B5/winfsp-2.2.26286.msi");
    internal const string Sha256 = "293FFB9BCC0B625C85079FE85FA77F8FE98170ABA51A02AC9C83D58F6C3B0E2F";
    private const string InstallerFileName = "winfsp-2.2.26286.msi";

    private readonly HttpClient _httpClient;
    private readonly Func<ProcessStartInfo, Task<int>> _runInstaller;

    internal WinFspRuntimeInstaller(HttpClient httpClient, Func<ProcessStartInfo, Task<int>>? runInstaller = null) {
        _httpClient = httpClient;
        _runInstaller = runInstaller ?? RunInstallerAsync;
    }

    internal async Task<int> InstallAsync(TextWriter output, CancellationToken cancellationToken = default) {
        if (!WinFspRuntime.IsCompatible(PackageVersion, WinFspRuntime.RequiredVersion))
            throw new InvalidOperationException("The pinned WinFsp installer must be updated to match the .NET bindings.");

        var directory = Directory.CreateTempSubdirectory("Mrbr.RamDrive-WinFsp-");
        string installerPath = Path.Combine(directory.FullName, InstallerFileName);
        string logPath = Path.Combine(directory.FullName, "install.log");

        try {
            output.WriteLine($"Downloading WinFsp {PackageVersion} (2026 Beta5) from the official release...");
            await using (var downloadFile = new FileStream(installerPath, FileMode.CreateNew,
                FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)) {
                downloadTimeout.CancelAfter(TimeSpan.FromMinutes(3));
                await DownloadAndVerifyAsync(_httpClient, DownloadUri, Sha256, downloadFile, downloadTimeout.Token);
            }

            // Reopen read-only so MSI's reader can share the file. Recheck it under
            // this lock and retain the handle until installation finishes.
            await using var verifiedFile = new FileStream(installerPath, FileMode.Open,
                FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            await VerifyChecksumAsync(verifiedFile, Sha256, cancellationToken);
            output.WriteLine("Download verified. Starting Windows Installer; approve its administrator prompt to continue.");
            output.WriteLine($"Installer log: {logPath}");

            cancellationToken.ThrowIfCancellationRequested();
            return await _runInstaller(CreateInstallerStartInfo(installerPath, logPath));
        } finally {
            // Preserve the MSI log for diagnosing failures, but remove the download.
            try {
                File.Delete(installerPath);
                if (!File.Exists(logPath))
                    directory.Delete();
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                output.WriteLine($"Temporary installer could not be removed: {installerPath}");
            }
        }
    }

    internal static async Task DownloadAndVerifyAsync(HttpClient client, Uri uri, string expectedSha256,
        Stream destination, CancellationToken cancellationToken = default) {
        using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await source.CopyToAsync(destination, cancellationToken);
        await destination.FlushAsync(cancellationToken);

        await VerifyChecksumAsync(destination, expectedSha256, cancellationToken);
    }

    private static async Task VerifyChecksumAsync(Stream installer, string expectedSha256, CancellationToken cancellationToken) {
        installer.Position = 0;
        byte[] actualHash = await SHA256.HashDataAsync(installer, cancellationToken);
        if (!CryptographicOperations.FixedTimeEquals(actualHash, Convert.FromHexString(expectedSha256)))
            throw new InvalidDataException("WinFsp installer checksum verification failed. The installer was not started.");
        installer.Position = 0;
    }

    internal static ProcessStartInfo CreateInstallerStartInfo(string installerPath, string logPath) {
        var startInfo = new ProcessStartInfo {
            FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"),
            UseShellExecute = true,
            Verb = "runas"
        };
        foreach (string argument in new[] { "/i", installerPath, "/qb", "/norestart", "/L*v", logPath })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static async Task<int> RunInstallerAsync(ProcessStartInfo startInfo) {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Windows Installer could not be started.");
        // Once MSI has started, let it complete or roll back rather than terminating it.
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
