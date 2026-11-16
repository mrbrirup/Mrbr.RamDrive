namespace HighSpeedRamDrive;

public enum RamDriveSnapshotPurpose {
    DisasterRecovery,
    CacheSeed
}

[Flags]
public enum RamDriveSnapshotMetadata {
    None = 0,
    FileAttributes = 1,
    Timestamps = 2,
    AccessControl = 4,
    ReparsePointData = 8,
    Default = FileAttributes | Timestamps
}

public enum RamDriveSnapshotCompression {
    None,
    Fastest,
    Optimal
}

/// <summary>Controls which currently supported file metadata and payload policy are written to a snapshot.</summary>
public sealed record RamDriveSnapshotOptions {
    public RamDriveSnapshotPurpose Purpose { get; init; } = RamDriveSnapshotPurpose.DisasterRecovery;
    public RamDriveSnapshotMetadata Metadata { get; init; } = RamDriveSnapshotMetadata.Default;
    public RamDriveSnapshotCompression Compression { get; init; } = RamDriveSnapshotCompression.Fastest;
    public bool IncludeTempWorkspaces { get; init; }
    public int MaxEntries { get; init; } = 1_000_000;
    public long? MaxPayloadBytes { get; init; }
    public Func<CancellationToken, ValueTask<IAsyncDisposable>>? AcquireQuiescenceAsync { get; init; }

    internal RamDriveSnapshotOptions Validate() {
        if (!Enum.IsDefined(Purpose))
            throw new ArgumentOutOfRangeException(nameof(Purpose));
        if (!Enum.IsDefined(Compression))
            throw new ArgumentOutOfRangeException(nameof(Compression));
        const RamDriveSnapshotMetadata supported = RamDriveSnapshotMetadata.FileAttributes | RamDriveSnapshotMetadata.Timestamps;
        if ((Metadata & ~supported) != 0)
            throw new NotSupportedException("ACL and reparse-point persistence are not yet represented by the current file system.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEntries);
        if (MaxPayloadBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadBytes));
        return this;
    }
}

public enum RamDriveSaveFailureAction {
    Retry,
    UnmountWithoutSaving,
    KeepMounted
}

public readonly record struct RamDriveSaveFailureContext(string SnapshotPath, int Attempt, Exception Exception);

/// <summary>Application-provided UI or policy for a failed save-before-unmount operation.</summary>
public interface IRamDriveSaveFailureHandler {
    ValueTask<RamDriveSaveFailureAction> OnSaveFailedAsync(RamDriveSaveFailureContext context, CancellationToken cancellationToken);
}

public readonly record struct RamDriveUnmountResult(
    bool IsUnmounted,
    bool SnapshotSaved,
    bool UserKeptMounted,
    int SaveAttempts,
    Exception? LastSaveError);

public sealed class RamDriveUnmountException(RamDriveUnmountResult result)
    : InvalidOperationException("The RAM drive remains mounted because the save failure was not resolved.", result.LastSaveError) {
    public RamDriveUnmountResult Result { get; } = result;
}
