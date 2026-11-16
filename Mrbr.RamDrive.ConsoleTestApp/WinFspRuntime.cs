using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Mrbr.RamDrive.ConsoleTestApp;

internal readonly record struct RuntimeStatus(bool IsCompatible, string Message);

internal static class WinFspRuntime {
    // Read metadata without initializing WinFsp's native API. A failed API initializer
    // cannot be retried in the same process after installing the missing runtime.
    internal static Version RequiredVersion {
        get {
            var version = FileVersionInfo.GetVersionInfo(typeof(Fsp.FileSystemHost).Assembly.Location);
            return new Version(version.FileMajorPart, version.FileMinorPart);
        }
    }

    internal static bool IsCompatible(Version actual, Version required) =>
        actual.Major == required.Major && actual.Minor >= required.Minor;

    internal static string LibraryName(Architecture architecture) => architecture switch {
        Architecture.X64 => "winfsp-x64.dll",
        Architecture.X86 => "winfsp-x86.dll",
        Architecture.Arm64 => "winfsp-a64.dll",
        _ => throw new PlatformNotSupportedException($"WinFsp does not support {architecture} processes.")
    };

    [SupportedOSPlatform("windows")]
    internal static RuntimeStatus Check() {
        string libraryName = LibraryName(RuntimeInformation.ProcessArchitecture);
        Version required = RequiredVersion;

        // Match WinFsp's loader: try the DLL search path, then its 32-bit registry key.
        if (!NativeLibrary.TryLoad(libraryName, out var library)) {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = machine.OpenSubKey(@"SOFTWARE\WinFsp");
            string? installDirectory = key?.GetValue("InstallDir") as string;
            if (string.IsNullOrWhiteSpace(installDirectory) ||
                !NativeLibrary.TryLoad(Path.Combine(installDirectory, "bin", libraryName), out library)) {
                return new(false, $"WinFsp {required} or a compatible newer minor version is required; {libraryName} cannot be loaded.");
            }
        }

        try {
            if (!NativeLibrary.TryGetExport(library, "FspVersion", out var export))
                return new(false, $"The loaded {libraryName} does not expose FspVersion.");

            var getVersion = Marshal.GetDelegateForFunctionPointer<GetVersion>(export);
            if (getVersion(out uint packedVersion) < 0)
                return new(false, $"Unable to query the version of {libraryName}.");

            var actual = new Version((int)(packedVersion >> 16), (int)(packedVersion & 0xFFFF));
            return IsCompatible(actual, required)
                ? new(true, $"WinFsp {actual} runtime is available.")
                : new(false, $"WinFsp {actual} is installed, but the .NET bindings require {required} with the same major version.");
        } finally {
            // Do not keep an old DLL locked while Windows Installer upgrades it.
            NativeLibrary.Free(library);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetVersion(out uint version);
}
