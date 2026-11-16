namespace HighSpeedRamDrive;

/// <summary>Controls drive-letter selection and memory-based sizing for workload helpers.</summary>
public sealed record RamDriveSettings {
    /// <summary>Search from Z to A by default; choose FirstAvailable to search from A to Z.</summary>
    public DriveLetterSelection FindFreeDriveLetter { get; init; } = DriveLetterSelection.LastAvailable;

    /// <summary>Required when FindFreeDriveLetter is Provided. Accepts a letter such as R or R:.</summary>
    public string? DriveLetter { get; init; }

    /// <summary>Null uses the workload helper's fixed-size default.</summary>
    public DriveSizing? DriveSizing { get; init; }

    /// <summary>GiB for DefinedSize; percentage (0, 100] for either RAM percentage mode.</summary>
    public double? Size { get; init; }

    /// <summary>Optional overrides for the workload name, label, and chunk allocation size.</summary>
    public string? Name { get; init; }
    public string? VolumeLabel { get; init; }
    public int? ChunkSize { get; init; }

    /// <summary>Optional snapshot to save during unmount.</summary>
    public string? SaveSnapshotOnUnmountPath { get; init; }
    public RamDriveSnapshotOptions SnapshotOptions { get; init; } = new();
    public IRamDriveSaveFailureHandler? SaveFailureHandler { get; init; }

    public RamDriveSettings Validate() {
        if (!Enum.IsDefined(FindFreeDriveLetter))
            throw new ArgumentOutOfRangeException(nameof(FindFreeDriveLetter));
        if (FindFreeDriveLetter == DriveLetterSelection.Provided) {
            if (string.IsNullOrWhiteSpace(DriveLetter))
                throw new ArgumentException("A drive letter is required when FindFreeDriveLetter is Provided.", nameof(DriveLetter));
            _ = NormalizeDriveLetter(DriveLetter);
        } else if (DriveLetter is not null) {
            throw new ArgumentException("DriveLetter can only be set when FindFreeDriveLetter is Provided.", nameof(DriveLetter));
        }

        if (DriveSizing is { } sizing && !Enum.IsDefined(sizing))
            throw new ArgumentOutOfRangeException(nameof(DriveSizing));
        if (Size is { } size && (!double.IsFinite(size) || size <= 0))
            throw new ArgumentOutOfRangeException(nameof(Size), "Size must be a finite positive number.");
        if ((DriveSizing is global::HighSpeedRamDrive.DriveSizing.PercentOfFreeRam or
            global::HighSpeedRamDrive.DriveSizing.PercentOfTotalRam) && Size is null)
            throw new ArgumentException("Size must be specified as a percentage when percentage-based sizing is selected.", nameof(Size));
        if (Name is not null && string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("Name cannot be empty.", nameof(Name));
        if (VolumeLabel is not null && (string.IsNullOrWhiteSpace(VolumeLabel) || VolumeLabel.Length > 32))
            throw new ArgumentException("A volume label from 1 to 32 characters is required.", nameof(VolumeLabel));
        if (ChunkSize is { } chunkSize && (chunkSize < 4096 || !System.Numerics.BitOperations.IsPow2((uint)chunkSize)))
            throw new ArgumentOutOfRangeException(nameof(ChunkSize), "Chunk size must be a power of two of at least 4 KiB.");
        if (SaveSnapshotOnUnmountPath is not null && string.IsNullOrWhiteSpace(SaveSnapshotOnUnmountPath))
            throw new ArgumentException("Snapshot path cannot be empty.", nameof(SaveSnapshotOnUnmountPath));
        ArgumentNullException.ThrowIfNull(SnapshotOptions);
        SnapshotOptions.Validate();
        return this;
    }

    internal static string NormalizeDriveLetter(string value) {
        string trimmed = value.Trim();
        if (trimmed.Length == 1 && char.IsAsciiLetter(trimmed[0]))
            return char.ToUpperInvariant(trimmed[0]) + ":";
        if (trimmed.Length == 2 && char.IsAsciiLetter(trimmed[0]) && trimmed[1] == ':')
            return char.ToUpperInvariant(trimmed[0]) + ":";
        throw new ArgumentException("DriveLetter must be a letter such as R or R:.", nameof(DriveLetter));
    }
}

/// <summary>How a workload helper chooses a Windows drive letter.</summary>
public enum DriveLetterSelection {
    LastAvailable,
    FirstAvailable,
    Provided
}

/// <summary>How a workload helper converts Size into a capacity limit.</summary>
public enum DriveSizing {
    DefinedSize,
    PercentOfFreeRam,
    PercentOfTotalRam
}
