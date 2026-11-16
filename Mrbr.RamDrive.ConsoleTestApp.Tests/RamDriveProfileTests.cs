using HighSpeedRamDrive;
using Xunit;

namespace Mrbr.RamDrive.ConsoleTestApp.Tests;

public class RamDriveProfileTests {
    [Fact]
    public void WorkloadDefaultsHaveUniqueNamesAndValidStorageSettings() {
        var profiles = RamDriveProfile.Presets;
        Assert.Equal(profiles.Count, profiles.Select(profile => profile.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(profiles, profile => profile.Name == "video");
        Assert.Contains(profiles, profile => profile.Name == "images");
        Assert.Contains(profiles, profile => profile.Name == "text");
        Assert.Contains(profiles, profile => profile.Name == "misc");
        Assert.Contains(profiles, profile => profile.Name == "database");
        Assert.Contains(profiles, profile => profile.Name == "build");
        Assert.Contains(profiles, profile => profile.Name == "cache");
        foreach (var profile in profiles)
            Assert.Same(profile, profile.Validate());
    }

    [Fact]
    public void WorkloadDefaultsUseDifferentAllocationGranularity() {
        var textDrive = RamDriveProfile.Presets.Single(profile => profile.Name == "text");
        var videoDrive = RamDriveProfile.Presets.Single(profile => profile.Name == "video");

        Assert.Equal(4096, textDrive.ChunkSize);
        Assert.Equal(1024 * 1024, videoDrive.ChunkSize);
        Assert.True(videoDrive.ChunkSize / textDrive.ChunkSize >= 250);
    }

    [Fact]
    public void InvalidChunkSizesAreRejected() {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkPool(1024 * 1024, 3000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkPool(1024 * 1024, 6000));
    }
}
