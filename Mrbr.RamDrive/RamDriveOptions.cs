namespace HighSpeedRamDrive;

/// <summary>Configuration for mounting a managed RAM drive.</summary>
public sealed record RamDriveOptions {
    public string Name { get; init; } = "application-temp";
    public string? MountPoint { get; init; }
    public long CapacityBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public int ChunkSize { get; init; } = 64 * 1024;
    public string VolumeLabel { get; init; } = "RamDrive";
    public string? SaveSnapshotOnUnmountPath { get; init; }
    public RamDriveSnapshotOptions SnapshotOptions { get; init; } = new();
    public IRamDriveSaveFailureHandler? SaveFailureHandler { get; init; }

    /// <summary>Returns a volatile 2 GiB drive intended for one application's temporary workspace.</summary>
    public static RamDriveOptions ApplicationTemp() => new();

    public static RamDriveOptions FromProfile(RamDriveProfile profile) {
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        return new RamDriveOptions {
            Name = profile.Name,
            CapacityBytes = profile.CapacityBytes,
            ChunkSize = profile.ChunkSize,
            VolumeLabel = profile.Name
        };
    }

    public RamDriveOptions Validate() {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("A drive name is required.", nameof(Name));
        if (CapacityBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(CapacityBytes));
        if (string.IsNullOrWhiteSpace(VolumeLabel) || VolumeLabel.Length > 32)
            throw new ArgumentException("A volume label from 1 to 32 characters is required.", nameof(VolumeLabel));
        if (MountPoint is not null && (MountPoint.Length != 2 || !char.IsAsciiLetter(MountPoint[0]) || MountPoint[1] != ':'))
            throw new ArgumentException("Mount point must be a drive letter such as R:.", nameof(MountPoint));
        if (SaveSnapshotOnUnmountPath is not null && string.IsNullOrWhiteSpace(SaveSnapshotOnUnmountPath))
            throw new ArgumentException("Snapshot path cannot be empty.", nameof(SaveSnapshotOnUnmountPath));
        SnapshotOptions.Validate();
        _ = new ChunkPool(CapacityBytes, ChunkSize);
        return this;
    }
}

public sealed record RamDriveStatus(
    string Name,
    string MountPoint,
    bool IsMounted,
    long CapacityBytes,
    long InUseBytes,
    long FreeBytes,
    long CachedBytes,
    int ChunkSize);
