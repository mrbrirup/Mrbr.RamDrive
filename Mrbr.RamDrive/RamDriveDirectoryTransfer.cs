namespace HighSpeedRamDrive;

public enum RamDriveConflictPolicy {
    Fail,
    Skip,
    Overwrite
}

public enum RamDriveCopyMode {
    Auto,
    FileStream,
    DoubleBufferedFileStream,
    RandomAccess
}

public sealed record RamDriveTransferOptions {
    public string DestinationDirectory { get; init; } = "";
    public RamDriveConflictPolicy ConflictPolicy { get; init; } = RamDriveConflictPolicy.Fail;
    public bool PreserveFileAttributes { get; init; } = true;
    public bool PreserveTimestamps { get; init; } = true;
    public bool SkipReparsePoints { get; init; } = true;
    public long? MaxBytes { get; init; }
    public RamDriveCopyMode CopyMode { get; init; } = RamDriveCopyMode.Auto;
    public int BufferSize { get; init; } = 128 * 1024;
    public long DoubleBufferThresholdBytes { get; init; } = 8L * 1024 * 1024;
    public int MaxConcurrentFiles { get; init; } = Math.Clamp(Environment.ProcessorCount, 1, 8);
    public bool ClearBuffersOnReturn { get; init; }
    public IProgress<RamDriveTransferProgress>? Progress { get; init; }

    internal RamDriveTransferOptions Validate() {
        if (MaxBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxBytes));
        if (!Enum.IsDefined(ConflictPolicy))
            throw new ArgumentOutOfRangeException(nameof(ConflictPolicy));
        if (!Enum.IsDefined(CopyMode))
            throw new ArgumentOutOfRangeException(nameof(CopyMode));
        if (BufferSize is < 4096 or > 16 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(BufferSize), "Buffer size must be between 4 KiB and 16 MiB.");
        if (DoubleBufferThresholdBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(DoubleBufferThresholdBytes));
        if (MaxConcurrentFiles is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrentFiles));
        return this;
    }
}

public readonly record struct RamDriveTransferProgress(long BytesTransferred, int FilesTransferred, string CurrentPath);
public readonly record struct RamDriveTransferResult(long BytesTransferred, int FilesTransferred, int DirectoriesTransferred, int SkippedEntries);

/// <summary>Streamed import/export helpers for a mounted RamDriveSession.</summary>
public static class RamDriveDirectoryTransfer {
    public static async Task<RamDriveTransferResult> ImportDirectoryAsync(
        RamDriveSession drive,
        string sourceDirectory,
        RamDriveTransferOptions? options = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        options = (options ?? new RamDriveTransferOptions()).Validate();
        string sourceRoot = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException(sourceRoot);
        string destinationRoot = ResolveDrivePath(drive.RootPath, options.DestinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        int directories = 0;
        int skippedDirectories = 0;
        var directoryMetadata = new List<DirectoryTransferEntry>();
        string destinationRelativeRoot = Path.GetRelativePath(drive.RootPath, destinationRoot);
        RamDriveTransferResult copied = await RamDriveFileTransfer.ImportFilesAsync(drive,
            EnumerateFiles(), options, cancellationToken).ConfigureAwait(false);

        ApplyDirectoryMetadata(directoryMetadata, options);
        return copied with { DirectoriesTransferred = directories, SkippedEntries = copied.SkippedEntries + skippedDirectories };

        IEnumerable<RamDriveFileTransferItem> EnumerateFiles() {
            var pending = new Stack<string>();
            pending.Push(sourceRoot);
            while (pending.TryPop(out string? currentSource)) {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (string entry in Directory.EnumerateFileSystemEntries(currentSource)) {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) {
                        if (options.SkipReparsePoints) { skippedDirectories++; continue; }
                        throw new IOException($"Reparse points are not supported by this transfer: {entry}");
                    }

                    string relative = Path.GetRelativePath(sourceRoot, entry);
                    string destination = ResolveDrivePath(destinationRoot, relative);
                    if ((attributes & FileAttributes.Directory) != 0) {
                        if (Directory.Exists(destination)) {
                            if (options.ConflictPolicy == RamDriveConflictPolicy.Fail)
                                throw new IOException($"Destination directory already exists: {destination}");
                            if (options.ConflictPolicy == RamDriveConflictPolicy.Skip) {
                                skippedDirectories++;
                                continue;
                            }
                        } else {
                            Directory.CreateDirectory(destination);
                        }
                        var directoryInfo = new DirectoryInfo(entry);
                        directoryMetadata.Add(new DirectoryTransferEntry(destination, attributes,
                            directoryInfo.CreationTimeUtc, directoryInfo.LastAccessTimeUtc, directoryInfo.LastWriteTimeUtc));
                        pending.Push(entry);
                        directories++;
                        continue;
                    }

                    yield return new RamDriveFileTransferItem(entry, Path.Combine(destinationRelativeRoot, relative));
                }
            }
        }
    }

    public static async Task<RamDriveTransferResult> ExportDirectoryAsync(
        RamDriveSession drive,
        string destinationDirectory,
        string sourceDirectory = "",
        RamDriveTransferOptions? options = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        options = (options ?? new RamDriveTransferOptions()).Validate();
        string sourceRoot = ResolveDrivePath(drive.RootPath, sourceDirectory);
        if (!Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException(sourceRoot);
        string destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);

        int directories = 0;
        int skippedDirectories = 0;
        var directoryMetadata = new List<DirectoryTransferEntry>();
        RamDriveTransferResult copied = await RamDriveFileTransfer.ExportFilesAsync(drive,
            EnumerateFiles(), options, cancellationToken).ConfigureAwait(false);

        ApplyDirectoryMetadata(directoryMetadata, options);
        return copied with { DirectoriesTransferred = directories, SkippedEntries = copied.SkippedEntries + skippedDirectories };

        IEnumerable<RamDriveFileTransferItem> EnumerateFiles() {
            var pending = new Stack<string>();
            pending.Push(sourceRoot);
            while (pending.TryPop(out string? currentSource)) {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (string entry in Directory.EnumerateFileSystemEntries(currentSource)) {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) {
                        if (options.SkipReparsePoints) { skippedDirectories++; continue; }
                        throw new IOException($"Reparse points are not supported by this transfer: {entry}");
                    }

                    string relative = Path.GetRelativePath(sourceRoot, entry);
                    string destination = ResolveDrivePath(destinationRoot, relative);
                    if ((attributes & FileAttributes.Directory) != 0) {
                        if (Directory.Exists(destination)) {
                            if (options.ConflictPolicy == RamDriveConflictPolicy.Fail)
                                throw new IOException($"Destination directory already exists: {destination}");
                            if (options.ConflictPolicy == RamDriveConflictPolicy.Skip) {
                                skippedDirectories++;
                                continue;
                            }
                        } else {
                            Directory.CreateDirectory(destination);
                        }
                        var directoryInfo = new DirectoryInfo(entry);
                        directoryMetadata.Add(new DirectoryTransferEntry(destination, attributes,
                            directoryInfo.CreationTimeUtc, directoryInfo.LastAccessTimeUtc, directoryInfo.LastWriteTimeUtc));
                        pending.Push(entry);
                        directories++;
                        continue;
                    }

                    string sourceRelative = Path.GetRelativePath(drive.RootPath, entry);
                    yield return new RamDriveFileTransferItem(sourceRelative, destination);
                }
            }
        }
    }

    private static void ApplyDirectoryMetadata(List<DirectoryTransferEntry> directories, RamDriveTransferOptions options) {
        foreach (var directory in directories.OrderByDescending(item => item.Path.Count(ch => ch is '\\' or '/'))) {
            if (options.PreserveTimestamps) {
                Directory.SetCreationTimeUtc(directory.Path, directory.CreationTimeUtc);
                Directory.SetLastAccessTimeUtc(directory.Path, directory.LastAccessTimeUtc);
                Directory.SetLastWriteTimeUtc(directory.Path, directory.LastWriteTimeUtc);
            }
            if (options.PreserveFileAttributes)
                File.SetAttributes(directory.Path, directory.Attributes & ~FileAttributes.ReparsePoint);
        }
    }

    internal static string ResolveDrivePath(string root, string relativePath) {
        if (string.IsNullOrEmpty(relativePath) || relativePath == ".")
            return Path.GetFullPath(root);
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("Transfer paths must be relative to their selected root.", nameof(relativePath));

        string fullRoot = Path.GetFullPath(root);
        string fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        string rootPrefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Transfer path escapes its selected root.", nameof(relativePath));
        return fullPath;
    }

    private readonly record struct DirectoryTransferEntry(string Path, FileAttributes Attributes,
        DateTime CreationTimeUtc, DateTime LastAccessTimeUtc, DateTime LastWriteTimeUtc);
}
