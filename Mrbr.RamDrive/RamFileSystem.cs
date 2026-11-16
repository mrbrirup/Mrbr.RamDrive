using Fsp;
using System.Collections.Concurrent;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace HighSpeedRamDrive;

// 3. WINFSP FILE SYSTEM IMPLEMENTATION
public class RamFileSystem : FileSystemBase {
    private const uint InvalidFileAttributes = uint.MaxValue;
    private readonly long _maxCapacityBytes;
    private readonly ChunkPool _chunkPool;
    private readonly RamNode _root;
    private readonly ConcurrentDictionary<string, RamDriveTempQuota> _tempScopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _volumeLabel;

    public RamFileSystem(long maxCapacityBytes, int chunkSize = ChunkPool.DefaultChunkSize, string volumeLabel = "RamDrive") {
        ArgumentException.ThrowIfNullOrWhiteSpace(volumeLabel);
        _chunkPool = new ChunkPool(maxCapacityBytes, chunkSize);
        _root = new RamNode("", isDirectory: true, _chunkPool);
        _maxCapacityBytes = maxCapacityBytes;
        _volumeLabel = volumeLabel;
    }

    internal long ChunkPoolInUseBytes => _chunkPool.InUseBytes;

    // Helper: Resolves a Windows path like "\temp\frames\001.jpg" to its node and parent
    private bool TryResolvePath(string? fileName, out RamNode? node, out RamNode? parent, out string leafName) {
        node = null;
        parent = null;
        leafName = string.Empty;

        if (string.IsNullOrEmpty(fileName) || fileName == "\\") {
            node = _root;
            return true;
        }

        var parts = fileName.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        RamNode current = _root;

        for (int i = 0; i < parts.Length - 1; i++) {
            if (!current.IsDirectory || !current.Children.TryGetValue(parts[i], out var next)) {
                return false;
            }
            current = next;
        }

        parent = current;
        leafName = parts[^1];
        parent.Children.TryGetValue(leafName, out node);
        return true;
    }

    public override int Init(object host) {
        var fsHost = (FileSystemHost)host;
        fsHost.SectorSize = 4096;
        fsHost.SectorsPerAllocationUnit = 1;
        fsHost.MaxComponentLength = 255;
        fsHost.FileInfoTimeout = 1000;
        fsHost.CaseSensitiveSearch = false;
        fsHost.CasePreservedNames = true;
        fsHost.UnicodeOnDisk = true;
        fsHost.PersistentAcls = false;
        fsHost.PostCleanupWhenModifiedOnly = true;
        fsHost.PassQueryDirectoryPattern = true;

        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo) {
        volumeInfo = new VolumeInfo {
            TotalSize = (ulong)_maxCapacityBytes,
            FreeSize = (ulong)Math.Max(0, _maxCapacityBytes - _chunkPool.InUseBytes)
        };
        volumeInfo.SetVolumeLabel(_volumeLabel);
        return STATUS_SUCCESS;
    }

    public override int GetSecurityByName(string fileName, out uint fileAttributes, ref byte[] securityDescriptor) {
        fileAttributes = 0;
        if (!TryResolvePath(fileName, out var node, out _, out _) || node == null)
            return STATUS_OBJECT_NAME_NOT_FOUND;

        fileAttributes = node.Attributes;
        return STATUS_SUCCESS;
    }

    public override int Create(string fileName, uint createOptions, uint grantedAccess, uint fileAttributes,
        byte[] securityDescriptor, ulong allocationSize, out object? fileNode, out object? fileDesc,
        out FileInfo fileInfo, out string normalizedName) {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = fileName;

        if (!TryResolvePath(fileName, out var existingNode, out var parent, out var leafName) || parent == null)
            return STATUS_OBJECT_PATH_NOT_FOUND;

        if (existingNode != null)
            return STATUS_OBJECT_NAME_COLLISION;

        bool isDir = (createOptions & FILE_DIRECTORY_FILE) != 0;
        var newNode = new RamNode(leafName, isDir, _chunkPool, fileAttributes);
        newNode.TempQuota = FindTempQuota(fileName);
        if (newNode.TempQuota is { } tempQuota && !tempQuota.TryAddEntry())
            return STATUS_DISK_FULL;

        if (!isDir && allocationSize > 0 && !newNode.TryResize((long)allocationSize)) {
            newNode.TempQuota?.ReleaseEntry();
            return STATUS_DISK_FULL;
        }

        newNode.OpenHandles = 1;
        newNode.DeletePending = (createOptions & FILE_DELETE_ON_CLOSE) != 0;
        if (newNode.DeletePending)
            newNode.DeletePath = fileName;
        if (!parent.Children.TryAdd(leafName, newNode)) {
            newNode.OpenHandles = 0;
            newNode.ReleaseAllChunks();
            newNode.TempQuota?.ReleaseEntry();
            return STATUS_OBJECT_NAME_COLLISION;
        }

        fileNode = newNode;
        fileInfo = newNode.ToFileInfo();
        return STATUS_SUCCESS;
    }

    public override int Open(string fileName, uint createOptions, uint grantedAccess,
        out object? fileNode, out object? fileDesc, out FileInfo fileInfo, out string normalizedName) {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = fileName;

        if (!TryResolvePath(fileName, out var node, out _, out _) || node == null)
            return STATUS_OBJECT_NAME_NOT_FOUND;

        if ((createOptions & FILE_DIRECTORY_FILE) != 0 && !node.IsDirectory)
            return STATUS_NOT_A_DIRECTORY;

        if ((createOptions & FILE_NON_DIRECTORY_FILE) != 0 && node.IsDirectory)
            return STATUS_FILE_IS_A_DIRECTORY;

        lock (node.NodeLock) {
            if (node.DeletePending)
                return STATUS_DELETE_PENDING;

            node.OpenHandles++;
            if ((createOptions & FILE_DELETE_ON_CLOSE) != 0) {
                node.DeletePending = true;
                node.DeletePath = fileName;
            }
        }

        fileNode = node;
        fileInfo = node.ToFileInfo();
        return STATUS_SUCCESS;
    }

    public override int Overwrite(object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes,
        ulong allocationSize, out FileInfo fileInfo) {
        var node = (RamNode)fileNode;
        lock (node.NodeLock) {
            long newSize = allocationSize > 0 ? (long)allocationSize : 0;
            if (!node.TryResize(newSize)) {
                fileInfo = node.ToFileInfo();
                return STATUS_DISK_FULL;
            }

            node.Attributes = replaceFileAttributes ? fileAttributes : (node.Attributes | fileAttributes);
            node.LastWriteTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            fileInfo = node.ToFileInfo();
        }
        return STATUS_SUCCESS;
    }
    // 4. PROPER CLEANUP & MEMORY RECYCLING
    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags) {
        var node = (RamNode)fileNode;
        lock (node.NodeLock) {
            if ((flags & CleanupDelete) != 0) {
                node.DeletePending = true;
                node.DeletePath = fileName;
            }
        }
    }
    public override void Close(object fileNode, object fileDesc) {
        var node = (RamNode)fileNode;
        string? deletePath = null;
        bool releaseDeletedNode = false;
        lock (node.NodeLock) {
            if (node.OpenHandles > 0)
                node.OpenHandles--;
            if (node.OpenHandles == 0 && node.DeletePending && node != _root) {
                deletePath = node.DeletePath;
                node.DeletePending = false;
                releaseDeletedNode = true;
            }
        }
        if (!releaseDeletedNode)
            return;

        if (deletePath is not null && TryResolvePath(deletePath, out _, out var parent, out var leafName) && parent is not null)
            ((ICollection<KeyValuePair<string, RamNode>>)parent.Children)
                .Remove(new KeyValuePair<string, RamNode>(leafName, node));

        lock (node.NodeLock) {
            node.ReleaseAllChunks();
            if (!node.TempQuotaEntryReleased) {
                node.TempQuota?.ReleaseEntry();
                node.TempQuotaEntryReleased = true;
            }
        }
    }
    public override unsafe int Read(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length, out uint bytesTransferred) {
        var node = (RamNode)fileNode;
        bytesTransferred = 0;
        if (offset > long.MaxValue)
            return STATUS_INVALID_PARAMETER;
        lock (node.NodeLock) {
            if ((long)offset >= node.FileSize)
                return STATUS_END_OF_FILE;
            long available = node.FileSize - (long)offset;
            int toRead = (int)Math.Min(length, available);
            int copied = 0;
            long currentOffset = (long)offset;
            int chunkSize = node.ChunkPool.ChunkSize;
            int chunkShift = node.ChunkPool.ChunkShift;
            int chunkMask = chunkSize - 1;
            List<byte[]> chunks = node.Chunks;
            Span<byte> destination = new((void*)buffer, toRead);
            while (copied < toRead) {
                int chunkIndex = (int)(currentOffset >> chunkShift);
                int chunkOffset = (int)(currentOffset & chunkMask);
                int sliceLength = Math.Min(toRead - copied, chunkSize - chunkOffset);
                chunks[chunkIndex].AsSpan(chunkOffset, sliceLength).CopyTo(destination[copied..]);
                copied += sliceLength;
                currentOffset += sliceLength;
            }
            bytesTransferred = (uint)copied;
        }
        return STATUS_SUCCESS;
    }
    public override unsafe int Write(object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
    bool writeToEndOfFile, bool constrainedIo, out uint bytesTransferred, out FileInfo fileInfo) {
        var node = (RamNode)fileNode;
        bytesTransferred = 0;
        lock (node.NodeLock) {
            long writeOffset = writeToEndOfFile ? node.FileSize : (long)offset;
            if (writeOffset < 0 || writeOffset > long.MaxValue - length) {
                fileInfo = node.ToFileInfo();
                return STATUS_INVALID_PARAMETER;
            }
            long endOffset = writeOffset + length;
            if (constrainedIo) {
                if (writeOffset >= node.FileSize) {
                    fileInfo = node.ToFileInfo();
                    return STATUS_SUCCESS;
                }
                endOffset = Math.Min(endOffset, node.FileSize);
            }
            if (endOffset > node.FileSize) {
                if (!node.TryResize(endOffset)) {
                    fileInfo = node.ToFileInfo();
                    return STATUS_DISK_FULL;
                }
            }
            int toWrite = (int)(endOffset - writeOffset);
            int copied = 0;
            long currentOffset = writeOffset;
            int chunkSize = node.ChunkPool.ChunkSize;
            int chunkShift = node.ChunkPool.ChunkShift;
            int chunkMask = chunkSize - 1;
            List<byte[]> chunks = node.Chunks;
            ReadOnlySpan<byte> source = new((void*)buffer, toWrite);
            while (copied < toWrite) {
                int chunkIndex = (int)(currentOffset >> chunkShift);
                int chunkOffset = (int)(currentOffset & chunkMask);
                int sliceLength = Math.Min(toWrite - copied, chunkSize - chunkOffset);
                source.Slice(copied, sliceLength).CopyTo(chunks[chunkIndex].AsSpan(chunkOffset, sliceLength));
                copied += sliceLength;
                currentOffset += sliceLength;
            }
            node.LastWriteTime = (ulong)DateTime.UtcNow.ToFileTimeUtc();
            bytesTransferred = (uint)copied;
            fileInfo = node.ToFileInfo();
        }
        return STATUS_SUCCESS;
    }
    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo) {
        var node = (RamNode)fileNode;
        lock (node.NodeLock) {
            fileInfo = node.ToFileInfo();
        }
        return STATUS_SUCCESS;
    }
    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo) {
        var node = (RamNode)fileNode;
        lock (node.NodeLock) {
            fileInfo = node.ToFileInfo();
        }
        return STATUS_SUCCESS;
    }
    public override int SetBasicInfo(object fileNode, object fileDesc, uint fileAttributes,
    ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime, out FileInfo fileInfo) {
        var node = (RamNode)fileNode;
        lock (node.NodeLock) {
            if (fileAttributes != InvalidFileAttributes) node.Attributes = fileAttributes;
            if (creationTime != 0) node.CreationTime = creationTime;
            if (lastAccessTime != 0) node.LastAccessTime = lastAccessTime;
            if (lastWriteTime != 0) node.LastWriteTime = lastWriteTime;
            if (changeTime != 0) node.ChangeTime = changeTime;
            fileInfo = node.ToFileInfo();
        }
        return STATUS_SUCCESS;
    }
    public override int SetFileSize(object fileNode, object fileDesc, ulong newSize, bool setAllocationSize, out FileInfo fileInfo) {
        var node = (RamNode)fileNode;
        lock (node.NodeLock) {
            if (!setAllocationSize || (long)newSize < node.FileSize) {
                if (!node.TryResize((long)newSize)) {
                    fileInfo = node.ToFileInfo();
                    return STATUS_DISK_FULL;
                }
            }
            fileInfo = node.ToFileInfo();
        }
        return STATUS_SUCCESS;
    }
    public override int CanDelete(object fileNode, object fileDesc, string fileName) {
        var node = (RamNode)fileNode;
        if (node == _root) return STATUS_ACCESS_DENIED;
        if (node.IsDirectory && !node.Children.IsEmpty) return STATUS_DIRECTORY_NOT_EMPTY;
        return STATUS_SUCCESS;
    }
    public override int Rename(object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists) {
        if (!TryResolvePath(fileName, out var node, out var oldParent, out var oldName) || node == null)
            return STATUS_OBJECT_NAME_NOT_FOUND;
        if (oldParent == null)
            return STATUS_ACCESS_DENIED;
        if (!TryResolvePath(newFileName, out var targetNode, out var newParent, out var newName) || newParent == null)
            return STATUS_OBJECT_PATH_NOT_FOUND;
        if (_tempScopes.ContainsKey(NormalizeVirtualPath(fileName)) ||
            _tempScopes.ContainsKey(NormalizeVirtualPath(newFileName)))
            return STATUS_ACCESS_DENIED;
        var targetQuota = FindTempQuota(newFileName);
        if (!ReferenceEquals(node.TempQuota, targetQuota) && (node.TempQuota != null || targetQuota != null))
            return STATUS_ACCESS_DENIED;
        if (targetNode != null) {
            if (!replaceIfExists || targetNode.IsDirectory)
                return STATUS_OBJECT_NAME_COLLISION;
            if (newParent.Children.TryRemove(newName, out var removed)) {
                lock (removed.NodeLock) {
                    removed.DeletePending = true;
                    removed.DeletePath = null;
                    if (!removed.TempQuotaEntryReleased) {
                        removed.TempQuota?.ReleaseEntry();
                        removed.TempQuotaEntryReleased = true;
                    }
                    if (removed.OpenHandles == 0) {
                        removed.ReleaseAllChunks();
                        removed.DeletePending = false;
                    }
                }
            }
        }
        if (oldParent.Children.TryRemove(oldName, out _)) {
            node.Name = newName;
            newParent.Children[newName] = node;
        }
        return STATUS_SUCCESS;
    }
    public override int ReadDirectory(object fileNode, object fileDesc, string pattern, string marker,
    IntPtr buffer, uint length, out uint bytesTransferred) {
        var dirNode = (RamNode)fileNode;
        bytesTransferred = 0;
        if (!dirNode.IsDirectory) return STATUS_NOT_A_DIRECTORY;

        // WinFsp packs entries into the buffer and adds the end-of-directory marker.
        return base.ReadDirectory(fileNode, fileDesc, pattern, marker, buffer, length, out bytesTransferred);
    }

    public override bool ReadDirectoryEntry(object fileNode, object fileDesc, string pattern, string marker,
    ref object context, out string fileName, out FileInfo fileInfo) {
        fileName = null!;
        fileInfo = default;

        if (context == null) {
            var dirNode = (RamNode)fileNode;
            var entries = new List<(string Name, FileInfo Info)> {
                (".", dirNode.ToFileInfo()),
                ("..", dirNode.ToFileInfo())
            };
            foreach (var kvp in dirNode.Children.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)) {
                if (!kvp.Value.DeletePending) {
                    entries.Add((kvp.Key, kvp.Value.ToFileInfo()));
                }
            }

            if (!string.IsNullOrEmpty(marker)) {
                int markerIndex = entries.FindIndex(entry =>
                    string.Equals(entry.Name, marker, StringComparison.OrdinalIgnoreCase));
                if (markerIndex >= 0) {
                    entries.RemoveRange(0, markerIndex + 1);
                }
                else {
                    // The marker may have been deleted since the previous request.
                    entries.RemoveAll(entry => entry.Name is "." or ".." ||
                        StringComparer.OrdinalIgnoreCase.Compare(entry.Name, marker) <= 0);
                }
            }

            context = ((IEnumerable<(string Name, FileInfo Info)>)entries).GetEnumerator();
        }

        var enumerator = (IEnumerator<(string Name, FileInfo Info)>)context;
        if (enumerator.MoveNext()) {
            var entry = enumerator.Current;
            fileName = entry.Name;
            fileInfo = entry.Info;
            return true;
        }

        return false;
    }

    internal RamDriveTempQuota RegisterTempScope(string virtualPath, long maxBytes, int maxEntries) {
        string normalized = NormalizeVirtualPath(virtualPath);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (maxEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxEntries));
        var quota = new RamDriveTempQuota(maxBytes, maxEntries);
        if (!_tempScopes.TryAdd(normalized, quota))
            throw new InvalidOperationException($"A temporary scope is already registered at '{virtualPath}'.");
        return quota;
    }

    internal void UnregisterTempScope(string virtualPath) => _tempScopes.TryRemove(NormalizeVirtualPath(virtualPath), out _);

    private RamDriveTempQuota? FindTempQuota(string? virtualPath) {
        if (string.IsNullOrWhiteSpace(virtualPath))
            return null;

        string normalized = NormalizeVirtualPath(virtualPath);
        RamDriveTempQuota? bestMatch = null;
        int bestLength = -1;
        foreach (var scope in _tempScopes) {
            if (normalized.Length > scope.Key.Length &&
                normalized.StartsWith(scope.Key, StringComparison.OrdinalIgnoreCase) &&
                normalized[scope.Key.Length] == '\\' && scope.Key.Length > bestLength) {
                bestMatch = scope.Value;
                bestLength = scope.Key.Length;
            }
        }
        return bestMatch;
    }

    private static string NormalizeVirtualPath(string path) {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string normalized = path.Replace('/', '\\').TrimEnd('\\');
        return normalized.StartsWith('\\') ? normalized : "\\" + normalized;
    }
}

