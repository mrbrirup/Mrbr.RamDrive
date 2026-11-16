namespace HighSpeedRamDrive;

/// <summary>Workload defaults for capacity and allocation granularity. Profiles never reserve a drive letter.</summary>
public sealed record RamDriveProfile(string Name, string Description, long CapacityBytes, int ChunkSize) {
    public static IReadOnlyList<RamDriveProfile> Presets { get; } = Array.AsReadOnly(new[] {
        new RamDriveProfile("video", "AI video and large sequential scratch files", GiB(32), 1024 * 1024),
        new RamDriveProfile("images", "Images, thumbnails and temporary renders", GiB(16), 256 * 1024),
        new RamDriveProfile("text", "Small temporary text, logs and JSON files", GiB(2), 4 * 1024),
        new RamDriveProfile("misc", "General purpose temporary files", GiB(8), 64 * 1024),
        new RamDriveProfile("database", "Database files and random page-sized I/O", GiB(16), 4 * 1024),
        new RamDriveProfile("build", "Compiler intermediates, package caches and CI scratch data", GiB(16), 64 * 1024),
        new RamDriveProfile("cache", "Reusable application and model caches", GiB(16), 256 * 1024),
        new RamDriveProfile("small-files", "Many small temporary files", GiB(2), 4 * 1024),
        new RamDriveProfile("medium-files", "Mixed file sizes and general processing", GiB(8), 64 * 1024),
        new RamDriveProfile("large-files", "Large sequential temporary files", GiB(32), 1024 * 1024)
    });

    public static long GiB(long value) => checked(value * 1024 * 1024 * 1024);

    public static RamDriveProfile GetPreset(string name) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return Presets.FirstOrDefault(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No RAM drive workload preset is named '{name}'.");
    }

    public RamDriveProfile Validate() {
        if (string.IsNullOrWhiteSpace(Name))
            throw new ArgumentException("A drive profile name is required.", nameof(Name));
        if (string.IsNullOrWhiteSpace(Description))
            throw new ArgumentException("A drive profile description is required.", nameof(Description));
        if (CapacityBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(CapacityBytes));
        _ = new ChunkPool(CapacityBytes, ChunkSize);
        return this;
    }
}
