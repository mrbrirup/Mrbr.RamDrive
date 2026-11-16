using System.Runtime.InteropServices;
using Mrbr.RamDrive.ConsoleTestApp;
using Xunit;

namespace Mrbr.RamDrive.ConsoleTestApp.Tests;

public class WinFspRuntimeTests {
    [Theory]
    [InlineData(2, 1, false)]
    [InlineData(2, 2, true)]
    [InlineData(2, 3, true)]
    [InlineData(1, 9, false)]
    [InlineData(3, 0, false)]
    public void CompatibilityRequiresSameMajorAndSufficientMinor(int major, int minor, bool expected) {
        Assert.Equal(expected, WinFspRuntime.IsCompatible(new Version(major, minor), new Version(2, 2)));
    }

    [Fact]
    public void PinnedInstallerMatchesReferencedBindings() {
        Assert.True(WinFspRuntime.IsCompatible(WinFspRuntimeInstaller.PackageVersion, WinFspRuntime.RequiredVersion));
    }

    [Theory]
    [InlineData(Architecture.X86, "winfsp-x86.dll")]
    [InlineData(Architecture.X64, "winfsp-x64.dll")]
    [InlineData(Architecture.Arm64, "winfsp-a64.dll")]
    public void NativeLibraryMatchesProcessArchitecture(Architecture architecture, string expected) {
        Assert.Equal(expected, WinFspRuntime.LibraryName(architecture));
    }

    [Fact]
    public void UnsupportedArchitectureIsRejected() {
        Assert.Throws<PlatformNotSupportedException>(() => WinFspRuntime.LibraryName(Architecture.Arm));
    }

    [Fact]
    public async Task CompatibleRuntimeDoesNotPromptOrInstall() {
        var output = new StringWriter();
        var setup = new WinFspRuntimeSetup(() => new(true, "Runtime ready."),
            () => throw new Exception("Installer must not run."), new StringReader(""), output);

        Assert.Equal(0, await setup.EnsureInstalledAsync());
        Assert.DoesNotContain("Download and install", output.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("n\n")]
    [InlineData("no\n")]
    public async Task DecliningOrEndOfInputDoesNotInstall(string answer) {
        var setup = new WinFspRuntimeSetup(() => new(false, "Missing runtime."),
            () => throw new Exception("Installer must not run."), new StringReader(answer), new StringWriter());

        Assert.Equal(1, await setup.EnsureInstalledAsync());
    }

    [Theory]
    [InlineData("y\n")]
    [InlineData(" YES \n")]
    public async Task SuccessfulInstallRechecksRuntimeAndContinues(string answer) {
        bool installed = false;
        int checks = 0;
        var output = new StringWriter();
        var setup = new WinFspRuntimeSetup(() => {
            checks++;
            return new(installed, installed ? "Runtime ready." : "Missing runtime.");
        }, () => {
            installed = true;
            return Task.FromResult(0);
        }, new StringReader(answer), output);

        Assert.Equal(0, await setup.EnsureInstalledAsync());
        Assert.Equal(2, checks);
        Assert.Contains("Continuing to mount", output.ToString());
    }

    [Fact]
    public async Task SuccessfulMsiExitWithoutWorkingRuntimeDoesNotContinue() {
        var output = new StringWriter();
        var setup = new WinFspRuntimeSetup(() => new(false, "Runtime cannot load."),
            () => Task.FromResult(0), new StringReader("y\n"), output);

        Assert.Equal(1, await setup.EnsureInstalledAsync());
        Assert.DoesNotContain("Continuing to mount", output.ToString());
    }

    [Theory]
    [InlineData(3010, 3010, "Restart Windows")]
    [InlineData(1641, 3010, "Restart Windows")]
    [InlineData(1602, 1602, "cancelled")]
    [InlineData(1618, 1618, "Another Windows installation")]
    [InlineData(1603, 1603, "installation failed")]
    public async Task InstallerOutcomeDoesNotProceedToMount(int installerCode, int expectedCode, string message) {
        int checks = 0;
        var output = new StringWriter();
        var setup = new WinFspRuntimeSetup(() => {
            checks++;
            return new(false, "Missing runtime.");
        }, () => Task.FromResult(installerCode), new StringReader("y\n"), output);

        Assert.Equal(expectedCode, await setup.EnsureInstalledAsync());
        Assert.Equal(1, checks);
        Assert.Contains(message, output.ToString());
        Assert.DoesNotContain("Continuing to mount", output.ToString());
    }
}
