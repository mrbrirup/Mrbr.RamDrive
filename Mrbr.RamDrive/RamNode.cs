using System.Collections.Concurrent;
using FileInfo = Fsp.Interop.FileInfo;

namespace HighSpeedRamDrive;

// 2. FILE SYSTEM NODE (Represents either a File or a Directory)
public class RamNode {
    public string Name { get; set; }
    public bool IsDirectory { get; }
    public uint Attributes { get; set; }
    public ulong CreationTime { get; set; }
    public ulong LastAccessTime { get; set; }
    public ulong LastWriteTime { get; set; }
    public ulong ChangeTime { get; set; }

    // File-specific storage
    public List<byte[]> Chunks { get; } = new();
    public long FileSize { get; set; }
    internal ChunkPool ChunkPool { get; }
    internal RamDriveTempQuota? TempQuota { get; set; }
    internal string? DeletePath { get; set; }
    internal bool TempQuotaEntryReleased { get; set; }

    // Directory-specific children
    public ConcurrentDictionary<string, RamNode> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Lifecycle tracking for DeleteOnClose
    public int OpenHandles;
    public bool DeletePending;
    public readonly object NodeLock = new();

    public RamNode(string name, bool isDirectory, uint attributes = 0)
        : this(name, isDirectory, new ChunkPool(long.MaxValue), attributes) { }

    internal RamNode(string name, bool isDirectory, ChunkPool chunkPool, uint attributes = 0) {
        Name = name;
        IsDirectory = isDirectory;
        ChunkPool = chunkPool;
        uint defaultAttr = isDirectory ? (uint)FileAttributes.Directory : (uint)FileAttributes.Archive;
        Attributes = attributes == 0 ? defaultAttr : attributes;

        ulong now = (ulong)DateTime.UtcNow.ToFileTimeUtc();
        CreationTime = LastAccessTime = LastWriteTime = ChangeTime = now;
    }

    public bool TryResize(long newSize) {
        if (newSize < 0)
            return false;
        if (IsDirectory)
            return true;
        if (newSize > int.MaxValue * (long)ChunkPool.ChunkSize)
            return false;

        int chunkSize = ChunkPool.ChunkSize;
        int chunkShift = ChunkPool.ChunkShift;
        int chunkMask = chunkSize - 1;
        long requiredChunkCount = newSize == 0 ? 0 : ((newSize - 1) >> chunkShift) + 1;
        if (requiredChunkCount > int.MaxValue)
            return false;
        int requiredChunks = (int)requiredChunkCount;

        int oldChunkCount = Chunks.Count;
        long sizeDelta = newSize - FileSize;
        bool quotaRejected = sizeDelta > 0 && TempQuota?.TryAddBytes(sizeDelta) == false;
        if (quotaRejected)
            return false;

        if (oldChunkCount < requiredChunks && !ChunkPool.TryRentMany(requiredChunks - oldChunkCount, Chunks)) {
            if (sizeDelta > 0)
                TempQuota?.ReleaseBytes(sizeDelta);
            return false;
        }

        if (newSize > FileSize && oldChunkCount > 0) {
            long clearEnd = Math.Min(newSize, (long)oldChunkCount * chunkSize);
            long clearOffset = FileSize;
            while (clearOffset < clearEnd) {
                int chunkIndex = (int)(clearOffset >> chunkShift);
                int chunkOffset = (int)(clearOffset & chunkMask);
                int clearLength = (int)Math.Min(clearEnd - clearOffset, chunkSize - chunkOffset);
                Chunks[chunkIndex].AsSpan(chunkOffset, clearLength).Clear();
                clearOffset += clearLength;
            }
        }

        while (Chunks.Count > requiredChunks) {
            int lastIdx = Chunks.Count - 1;
            ChunkPool.Return(Chunks[lastIdx]);
            Chunks.RemoveAt(lastIdx);
        }

        if (sizeDelta < 0)
            TempQuota?.ReleaseBytes(-sizeDelta);
        FileSize = newSize;
        return true;
    }

    public void ReleaseAllChunks() {
        TempQuota?.ReleaseBytes(FileSize);
        foreach (var chunk in Chunks) {
            ChunkPool.Return(chunk);
        }
        Chunks.Clear();
        FileSize = 0;
    }

    public FileInfo ToFileInfo() {
        return new FileInfo {
            FileAttributes = Attributes,
            ReparseTag = 0,
            FileSize = (ulong)FileSize,
            AllocationSize = (ulong)Chunks.Count * (uint)ChunkPool.ChunkSize,
            CreationTime = CreationTime,
            LastAccessTime = LastAccessTime,
            LastWriteTime = LastWriteTime,
            ChangeTime = ChangeTime,
            IndexNumber = (ulong)GetHashCode()
        };
    }
}

