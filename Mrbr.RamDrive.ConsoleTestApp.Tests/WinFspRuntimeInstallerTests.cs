using System.Net;
using System.Security.Cryptography;
using System.Text;
using Mrbr.RamDrive.ConsoleTestApp;
using Xunit;

namespace Mrbr.RamDrive.ConsoleTestApp.Tests;

public class WinFspRuntimeInstallerTests {
    [Fact]
    public async Task MatchingChecksumAcceptsDownloadedBytes() {
        byte[] content = Encoding.UTF8.GetBytes("Test installer content");
        using var handler = new ResponseHandler(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
        using var client = new HttpClient(handler);
        using var destination = new MemoryStream();

        await WinFspRuntimeInstaller.DownloadAndVerifyAsync(client, WinFspRuntimeInstaller.DownloadUri,
            Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), destination);

        Assert.Equal(content, destination.ToArray());
        Assert.Equal(0, destination.Position);
        Assert.Equal(WinFspRuntimeInstaller.DownloadUri, handler.RequestUri);
    }

    [Fact]
    public async Task CorruptedDownloadIsRejectedBeforeInstallerRuns() {
        using var handler = new ResponseHandler(_ => new(HttpStatusCode.OK) {
            Content = new StringContent("This is not the official MSI.")
        });
        using var client = new HttpClient(handler);
        bool installerStarted = false;
        var installer = new WinFspRuntimeInstaller(client, _ => {
            installerStarted = true;
            return Task.FromResult(0);
        });

        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync(new StringWriter()));

        Assert.False(installerStarted);
    }

    [Fact]
    public async Task FailedHttpResponseDoesNotStartInstaller() {
        using var handler = new ResponseHandler(_ => new(HttpStatusCode.NotFound));
        using var client = new HttpClient(handler);
        var installer = new WinFspRuntimeInstaller(client, _ => throw new Exception("Installer must not run."));

        await Assert.ThrowsAsync<HttpRequestException>(() => installer.InstallAsync(new StringWriter()));
    }

    [Fact]
    public async Task InterruptedDownloadDoesNotStartInstaller() {
        using var handler = new ResponseHandler(_ => throw new HttpRequestException("Connection interrupted."));
        using var client = new HttpClient(handler);
        var installer = new WinFspRuntimeInstaller(client, _ => throw new Exception("Installer must not run."));

        await Assert.ThrowsAsync<HttpRequestException>(() => installer.InstallAsync(new StringWriter()));
    }

    [Fact]
    public async Task CancelledDownloadDoesNotStartInstaller() {
        using var handler = new ResponseHandler(_ => new(HttpStatusCode.OK));
        using var client = new HttpClient(handler);
        var installer = new WinFspRuntimeInstaller(client, _ => throw new Exception("Installer must not run."));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.InstallAsync(new StringWriter(), cancellation.Token));
    }

    [Fact]
    public void InstallerUsesElevationAndNeverRestartsWindowsAutomatically() {
        const string installerPath = @"C:\Temp\Path with spaces\winfsp.msi";
        const string logPath = @"C:\Temp\Path with spaces\install.log";
        var startInfo = WinFspRuntimeInstaller.CreateInstallerStartInfo(installerPath, logPath);

        Assert.Equal("msiexec.exe", Path.GetFileName(startInfo.FileName));
        Assert.True(startInfo.UseShellExecute);
        Assert.Equal("runas", startInfo.Verb);
        Assert.Equal(new[] { "/i", installerPath, "/qb", "/norestart", "/L*v", logPath }, startInfo.ArgumentList);
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        internal Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            cancellationToken.ThrowIfCancellationRequested();
            RequestUri = request.RequestUri;
            return Task.FromResult(respond(request));
        }
    }
}
