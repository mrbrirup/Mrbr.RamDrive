using Fsp;
using HighSpeedRamDrive;
using System.Runtime.InteropServices;
using FileInfo = Fsp.Interop.FileInfo;
using Xunit;

namespace Mrbr.RamDrive.ConsoleTestApp.Tests;

public class RamFileSystemStorageTests {
    [Fact]
    public void DistinctDrivesEnforceTheirOwnCapacityAndShareReusableChunks() {
        var firstDrive = new RamFileSystem(8 * 4096, 4096);
        var secondDrive = new RamFileSystem(2 * 4096, 4096);
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, firstDrive.Create("\\one", 0, 0,
            (uint)FileAttributes.Archive, Array.Empty<byte>(), 0, out var firstFile, out _, out _, out _));
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, secondDrive.Create("\\two", 0, 0,
            (uint)FileAttributes.Archive, Array.Empty<byte>(), 0, out var secondFile, out _, out _, out _));

        byte[] page = Enumerable.Range(0, 4096).Select(value => (byte)value).ToArray();
        IntPtr buffer = Marshal.AllocHGlobal(page.Length);
        try {
            Marshal.Copy(page, 0, buffer, page.Length);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, firstDrive.Write(firstFile!, null!, buffer, 0,
                (uint)page.Length, false, false, out var firstBytes, out _));
            Assert.Equal((uint)page.Length, firstBytes);

            Assert.Equal(FileSystemBase.STATUS_DISK_FULL, secondDrive.Write(secondFile!, null!, buffer, 0,
                (uint)(page.Length * 2 + 1), false, false, out var rejectedBytes, out var rejectedInfo));
            Assert.Equal(0u, rejectedBytes);
            Assert.Equal(0ul, rejectedInfo.FileSize);

            Assert.Equal(FileSystemBase.STATUS_SUCCESS, secondDrive.Write(secondFile!, null!, buffer, 0,
                (uint)page.Length, false, false, out var secondBytes, out _));
            Assert.Equal((uint)page.Length, secondBytes);

            Assert.Equal(FileSystemBase.STATUS_SUCCESS, firstDrive.GetVolumeInfo(out var firstVolume));
            Assert.Equal(7ul * 4096, firstVolume.FreeSize);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, secondDrive.GetVolumeInfo(out var secondVolume));
            Assert.Equal(4096ul, secondVolume.FreeSize);

            firstDrive.Cleanup(firstFile!, null!, "\\one", FileSystemBase.CleanupDelete);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, secondDrive.Create("\\three", 0, 0,
                (uint)FileAttributes.Archive, Array.Empty<byte>(), 0, out var thirdFile, out _, out _, out _));
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, secondDrive.Write(thirdFile!, null!, buffer, 0,
                (uint)page.Length, false, false, out _, out _));
        } finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(65536)]
    [InlineData(262144)]
    [InlineData(1048576)]
    public void NativeBufferRoundTripsAcrossChunkBoundaries(int chunkSize) {
        var fileSystem = new RamFileSystem(chunkSize * 4L, chunkSize);
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Create("\\roundtrip", 0, 0,
            (uint)FileAttributes.Archive, Array.Empty<byte>(), 0, out var node, out _, out _, out _));
        int length = chunkSize * 3 + 123;
        byte[] expected = new byte[length];
        new Random(5150).NextBytes(expected);
        IntPtr input = Marshal.AllocHGlobal(length);
        IntPtr output = Marshal.AllocHGlobal(length);
        try {
            Marshal.Copy(expected, 0, input, length);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Write(node!, null!, input, 0,
                (uint)length, false, false, out var written, out _));
            Assert.Equal((uint)length, written);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Read(node!, null!, output, 0,
                (uint)length, out var read));
            Assert.Equal((uint)length, read);
            byte[] actual = new byte[length];
            Marshal.Copy(output, actual, 0, length);
            Assert.Equal(expected, actual);
        } finally {
            Marshal.FreeHGlobal(input);
            Marshal.FreeHGlobal(output);
        }
    }

    [Fact]
    public void FileAllocationIsZeroFilledAndFailedGrowthPreservesExistingContents() {
        var fileSystem = new RamFileSystem(4096, 4096);
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Create("\\zero", 0, 0,
            (uint)FileAttributes.Archive, Array.Empty<byte>(), 4096, out var node, out _, out _, out _));
        IntPtr buffer = Marshal.AllocHGlobal(4096);
        try {
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Read(node!, null!, buffer, 0, 4096, out var initiallyRead));
            Assert.Equal(4096u, initiallyRead);
            byte[] zeroes = new byte[4096];
            byte[] actual = new byte[4096];
            Marshal.Copy(buffer, actual, 0, actual.Length);
            Assert.Equal(zeroes, actual);

            byte[] content = Enumerable.Repeat((byte)0x5a, 4096).ToArray();
            Marshal.Copy(content, 0, buffer, content.Length);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Write(node!, null!, buffer, 0,
                4096, false, false, out _, out _));
            Assert.Equal(FileSystemBase.STATUS_DISK_FULL, fileSystem.SetFileSize(node!, null!, 8192, false, out _));
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Read(node!, null!, buffer, 0, 4096, out _));
            Marshal.Copy(buffer, actual, 0, actual.Length);
            Assert.Equal(content, actual);
        } finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void ShrinkThenExtendZeroesPreviouslyWrittenFileGap() {
        var fileSystem = new RamFileSystem(8192, 4096);
        Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Create("\\gap", 0, 0,
            (uint)FileAttributes.Archive, Array.Empty<byte>(), 4096, out var node, out _, out _, out _));
        IntPtr buffer = Marshal.AllocHGlobal(4096);
        try {
            Marshal.Copy(Enumerable.Repeat((byte)0x73, 4096).ToArray(), 0, buffer, 4096);
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Write(node!, null!, buffer, 0,
                4096, false, false, out _, out _));
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.SetFileSize(node!, null!, 1, false, out _));
            Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.SetFileSize(node!, null!, 4096, false, out _));

            Assert.Equal(FileSystemBase.STATUS_SUCCESS, fileSystem.Read(node!, null!, buffer, 0, 4096, out _));
            byte[] actual = new byte[4096];
            Marshal.Copy(buffer, actual, 0, actual.Length);
            Assert.Equal((byte)0x73, actual[0]);
            Assert.All(actual.AsSpan(1).ToArray(), value => Assert.Equal((byte)0, value));
        } finally {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
