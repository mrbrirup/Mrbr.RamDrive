using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HighSpeedRamDrive;

/// <summary>Creates workload-configured RAM drives with caller-selected sizing and drive letters.</summary>
public static class RamDriveFactory {
    private const long BytesPerGiB = 1024L * 1024 * 1024;

    public static RamDriveSession CreateVideoDrive(RamDriveSettings? settings = null) => CreateDrive(RamDriveProfile.GetPreset("video"), settings);
    public static RamDriveSession CreateImageDrive(RamDriveSettings? settings = null) => CreateDrive(RamDriveProfile.GetPreset("images"), settings);
    public static RamDriveSession CreateDatabaseDrive(RamDriveSettings? settings = null) => CreateDrive(RamDriveProfile.GetPreset("database"), settings);
    public static RamDriveSession CreateSmallFileDrive(RamDriveSettings? settings = null) => CreateDrive(RamDriveProfile.GetPreset("small-files"), settings);
    public static RamDriveSession CreateMediumFileDrive(RamDriveSettings? settings = null) => CreateDrive(RamDriveProfile.GetPreset("medium-files"), settings);
    public static RamDriveSession CreateLargeFileDrive(RamDriveSettings? settings = null) => CreateDrive(RamDriveProfile.GetPreset("large-files"), settings);
    public static RamDriveSession CreateApplicationTempDrive(RamDriveSettings? settings = null) => CreateDrive(
        new RamDriveProfile("application-temp", "Temporary workspace for one application", RamDriveProfile.GiB(2), 64 * 1024), settings);
    public static RamDriveSession CreateBuildDrive(RamDriveSettings? settings = null) => CreateDrive(RamDriveProfile.GetPreset("build"), settings);
    public static RamDriveSession CreateCacheDrive(RamDriveSettings? settings = null) => CreateDrive(RamDriveProfile.GetPreset("cache"), settings);
    public static RamDriveSession CreateMiscellaneousDrive(RamDriveSettings? settings = null) => CreateDrive(RamDriveProfile.GetPreset("misc"), settings);

    /// <summary>Creates a drive using a workload profile and optional caller overrides.</summary>
    public static RamDriveSession CreateDrive(RamDriveProfile profile, RamDriveSettings? settings = null) {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        settings = (settings ?? new RamDriveSettings()).Validate();

        DriveSizing sizing = settings.DriveSizing ?? DriveSizing.DefinedSize;
        double amount = settings.Size ?? (double)profile.CapacityBytes / BytesPerGiB;
        if ((sizing is DriveSizing.PercentOfFreeRam or DriveSizing.PercentOfTotalRam) && settings.Size is null)
            throw new ArgumentException("Size must be specified as a percentage when percentage-based sizing is selected.", nameof(settings));

        long capacityBytes = CalculateCapacityBytes(sizing, amount);
        string? mountPoint = settings.FindFreeDriveLetter == DriveLetterSelection.Provided
            ? RamDriveSettings.NormalizeDriveLetter(settings.DriveLetter!)
            : null;
        string name = settings.Name ?? profile.Name;
        var options = new RamDriveOptions {
            Name = name,
            MountPoint = mountPoint,
            CapacityBytes = capacityBytes,
            ChunkSize = settings.ChunkSize ?? profile.ChunkSize,
            VolumeLabel = settings.VolumeLabel ?? name,
            SaveSnapshotOnUnmountPath = settings.SaveSnapshotOnUnmountPath,
            SnapshotOptions = settings.SnapshotOptions,
            SaveFailureHandler = settings.SaveFailureHandler
        };
        return RamDriveSession.Mount(options, settings.FindFreeDriveLetter);
    }

    private static long CalculateCapacityBytes(DriveSizing sizing, double amount) {
        if (!double.IsFinite(amount) || amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "Size must be a finite positive number.");

        if (sizing == DriveSizing.DefinedSize) {
            double wholeGiB = Math.Floor(amount);
            if (wholeGiB < 1 || wholeGiB > long.MaxValue / BytesPerGiB)
                throw new ArgumentOutOfRangeException(nameof(amount), "A defined drive size must be at least 1 GiB and within the supported range.");
            return checked((long)wholeGiB * BytesPerGiB);
        }

        if (amount > 100)
            throw new ArgumentOutOfRangeException(nameof(amount), "RAM percentages must be greater than 0 and no more than 100.");
        if (sizing is not (DriveSizing.PercentOfFreeRam or DriveSizing.PercentOfTotalRam))
            throw new ArgumentOutOfRangeException(nameof(sizing));

        MemoryStatusEx memory = GetMemoryStatus();
        ulong baseBytes = sizing == DriveSizing.PercentOfFreeRam ? memory.AvailablePhysicalBytes : memory.TotalPhysicalBytes;
        double requested = Math.Floor(baseBytes * (amount / 100d));
        if (!double.IsFinite(requested) || requested < 1 || requested > long.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(amount), "The selected percentage does not produce a supported drive capacity.");
        return (long)requested;
    }

    private static MemoryStatusEx GetMemoryStatus() {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("RAM-percentage drive sizing requires Windows physical memory information.");

        var memory = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref memory))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read available physical memory.");
        return memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhysicalBytes;
        public ulong AvailablePhysicalBytes;
        public ulong TotalPageFileBytes;
        public ulong AvailablePageFileBytes;
        public ulong TotalVirtualBytes;
        public ulong AvailableVirtualBytes;
        public ulong AvailableExtendedVirtualBytes;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
