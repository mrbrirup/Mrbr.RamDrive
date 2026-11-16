using System.Buffers;

namespace HighSpeedRamDrive;

/// <summary>One file transfer. For import, source is a host path and destination is drive-relative; export reverses that.</summary>
public readonly record struct RamDriveFileTransferItem(string SourcePath, string DestinationPath);

/// <summary>Asynchronous single- and multi-file transfers with bounded concurrency and pooled buffers.</summary>
public static class RamDriveFileTransfer {
    public static async Task<long> ImportFileAsync(RamDriveSession drive, string sourcePath,
        string destinationRelativePath, RamDriveTransferOptions? options = null,
        CancellationToken cancellationToken = default) {
        RamDriveTransferResult result = await ImportFilesAsync(drive,
            [new RamDriveFileTransferItem(sourcePath, destinationRelativePath)], options, cancellationToken).ConfigureAwait(false);
        return result.BytesTransferred;
    }

    public static async Task<long> ExportFileAsync(RamDriveSession drive, string sourceRelativePath,
        string destinationPath, RamDriveTransferOptions? options = null,
        CancellationToken cancellationToken = default) {
        RamDriveTransferResult result = await ExportFilesAsync(drive,
            [new RamDriveFileTransferItem(sourceRelativePath, destinationPath)], options, cancellationToken).ConfigureAwait(false);
        return result.BytesTransferred;
    }

    /// <summary>Writes a caller-owned stream to a drive file. The input stream remains open.</summary>
    public static async Task<long> ImportStreamAsync(RamDriveSession drive, Stream source,
        string destinationRelativePath, RamDriveTransferOptions? options = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
            throw new ArgumentException("The source stream must be readable.", nameof(source));
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRelativePath);
        options = (options ?? new RamDriveTransferOptions()).Validate();
        if (options.CopyMode == RamDriveCopyMode.RandomAccess)
            throw new NotSupportedException("RandomAccess mode requires a file path; use FileStream or DoubleBufferedFileStream for arbitrary streams.");

        string destination = RamDriveDirectoryTransfer.ResolveDrivePath(drive.RootPath, destinationRelativePath);
        EnsureNotDriveRoot(destination, drive);
        bool exists = File.Exists(destination);
        if (exists && options.ConflictPolicy == RamDriveConflictPolicy.Fail)
            throw new IOException($"Destination file already exists: {destination}");
        if (exists && options.ConflictPolicy == RamDriveConflictPolicy.Skip)
            return 0;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = GetSiblingTemporaryPath(destination);
        try {
            long copied;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan)) {
                bool doubleBuffered = ShouldDoubleBuffer(options, GetRemainingLength(source));
                copied = await CopyCallerStreamAsync(source, output, options, doubleBuffered, cancellationToken)
                    .ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destination, exists);
            options.Progress?.Report(new RamDriveTransferProgress(copied, 1, destinationRelativePath));
            return copied;
        } catch {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }

    /// <summary>Writes caller-owned memory directly to a drive file without allocating a copy buffer.</summary>
    public static async Task<long> ImportBufferAsync(RamDriveSession drive, ReadOnlyMemory<byte> contents,
        string destinationRelativePath, RamDriveTransferOptions? options = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationRelativePath);
        options = (options ?? new RamDriveTransferOptions()).Validate();
        if (options.MaxBytes is { } maxBytes && contents.Length > maxBytes)
            throw new IOException($"Transfer exceeds the configured limit of {maxBytes} bytes.");

        string destination = RamDriveDirectoryTransfer.ResolveDrivePath(drive.RootPath, destinationRelativePath);
        EnsureNotDriveRoot(destination, drive);
        bool exists = File.Exists(destination);
        if (exists && options.ConflictPolicy == RamDriveConflictPolicy.Fail)
            throw new IOException($"Destination file already exists: {destination}");
        if (exists && options.ConflictPolicy == RamDriveConflictPolicy.Skip)
            return 0;

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary = GetSiblingTemporaryPath(destination);
        try {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 1, FileOptions.Asynchronous | (options.CopyMode == RamDriveCopyMode.RandomAccess
                    ? FileOptions.RandomAccess : FileOptions.SequentialScan))) {
                if (options.CopyMode == RamDriveCopyMode.RandomAccess)
                    await System.IO.RandomAccess.WriteAsync(output.SafeFileHandle, contents, 0, cancellationToken).ConfigureAwait(false);
                else
                    await output.WriteAsync(contents, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, destination, exists);
            options.Progress?.Report(new RamDriveTransferProgress(contents.Length, 1, destinationRelativePath));
            return contents.Length;
        } catch {
            try { File.Delete(temporary); } catch { }
            throw;
        }
    }

    /// <summary>Copies a drive file to a caller-owned stream. The destination stream remains open.</summary>
    public static async Task<long> ExportStreamAsync(RamDriveSession drive, string sourceRelativePath,
        Stream destination, RamDriveTransferOptions? options = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination stream must be writable.", nameof(destination));
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceRelativePath);
        options = (options ?? new RamDriveTransferOptions()).Validate();
        if (options.CopyMode == RamDriveCopyMode.RandomAccess)
            throw new NotSupportedException("RandomAccess mode requires both ends to be file paths; use FileStream or DoubleBufferedFileStream for arbitrary streams.");

        string source = RamDriveDirectoryTransfer.ResolveDrivePath(drive.RootPath, sourceRelativePath);
        if (!File.Exists(source))
            throw new FileNotFoundException("Transfer source file was not found.", source);
        FileAttributes attributes = File.GetAttributes(source);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Reparse points are not supported by this transfer: {source}");

        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        long? remainingLength = GetRemainingLength(input);
        if (options.MaxBytes is { } maxBytes && remainingLength is { } length && length > maxBytes)
            throw new IOException($"Transfer exceeds the configured limit of {maxBytes} bytes.");
        bool doubleBuffered = ShouldDoubleBuffer(options, remainingLength);
        long copied = await CopyCallerStreamAsync(input, destination, options, doubleBuffered, cancellationToken)
            .ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        options.Progress?.Report(new RamDriveTransferProgress(copied, 1, sourceRelativePath));
        return copied;
    }

    /// <summary>Copies host files into the drive. Each destination path is relative to the drive root.</summary>
    public static Task<RamDriveTransferResult> ImportFilesAsync(RamDriveSession drive,
        IEnumerable<RamDriveFileTransferItem> files, RamDriveTransferOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TransferFilesAsync(drive, files, importing: true, options, cancellationToken);

    /// <summary>Copies files from the drive to host paths. Each source path is relative to the drive root.</summary>
    public static Task<RamDriveTransferResult> ExportFilesAsync(RamDriveSession drive,
        IEnumerable<RamDriveFileTransferItem> files, RamDriveTransferOptions? options = null,
        CancellationToken cancellationToken = default) =>
        TransferFilesAsync(drive, files, importing: false, options, cancellationToken);

    private static async Task<RamDriveTransferResult> TransferFilesAsync(RamDriveSession drive,
        IEnumerable<RamDriveFileTransferItem> files, bool importing, RamDriveTransferOptions? options,
        CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(files);
        options = (options ?? new RamDriveTransferOptions()).Validate();

        long reservedBytes = 0;
        long completedBytes = 0;
        int completedFiles = 0;
        int skippedFiles = 0;
        var parallelOptions = new ParallelOptions {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = options.MaxConcurrentFiles
        };

        await Parallel.ForEachAsync(files, parallelOptions, async (item, token) => {
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SourcePath);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.DestinationPath);

            string source = importing
                ? Path.GetFullPath(item.SourcePath)
                : RamDriveDirectoryTransfer.ResolveDrivePath(drive.RootPath, item.SourcePath);
            string destination = importing
                ? RamDriveDirectoryTransfer.ResolveDrivePath(drive.RootPath, item.DestinationPath)
                : Path.GetFullPath(item.DestinationPath);
            if (importing && string.Equals(Path.TrimEndingDirectorySeparator(destination),
                Path.TrimEndingDirectorySeparator(drive.RootPath), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("A drive root cannot be used as a file destination.", nameof(files));
            if (!File.Exists(source))
                throw new FileNotFoundException("Transfer source file was not found.", source);

            FileAttributes sourceAttributes = File.GetAttributes(source);
            if ((sourceAttributes & FileAttributes.ReparsePoint) != 0) {
                if (options.SkipReparsePoints) {
                    Interlocked.Increment(ref skippedFiles);
                    return;
                }
                throw new IOException($"Reparse points are not supported by this transfer: {source}");
            }

            bool exists = File.Exists(destination);
            if (exists && options.ConflictPolicy == RamDriveConflictPolicy.Fail)
                throw new IOException($"Destination file already exists: {destination}");
            if (exists && options.ConflictPolicy == RamDriveConflictPolicy.Skip) {
                Interlocked.Increment(ref skippedFiles);
                return;
            }

            var sourceInfo = new FileInfo(source);
            long expectedLength = sourceInfo.Length;
            ReserveBytes(ref reservedBytes, expectedLength, options.MaxBytes);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await CopyFileCoreAsync(source, destination, exists, options, importing, token).ConfigureAwait(false);

            long bytes = Interlocked.Add(ref completedBytes, expectedLength);
            int count = Interlocked.Increment(ref completedFiles);
            options.Progress?.Report(new RamDriveTransferProgress(bytes, count, item.DestinationPath));
        }).ConfigureAwait(false);

        return new RamDriveTransferResult(completedBytes, completedFiles, 0, skippedFiles);
    }

    private static void ReserveBytes(ref long reserved, long bytes, long? maximum) {
        while (true) {
            long current = Interlocked.Read(ref reserved);
            if (maximum is { } limit && bytes > limit - current)
                throw new IOException($"Transfer exceeds the configured limit of {limit} bytes.");
            long next = checked(current + bytes);
            if (Interlocked.CompareExchange(ref reserved, next, current) == current)
                return;
        }
    }

    private static async Task<long> CopyCallerStreamAsync(Stream source, Stream destination,
        RamDriveTransferOptions options, bool doubleBuffered, CancellationToken cancellationToken) {
        byte[] first = ArrayPool<byte>.Shared.Rent(options.BufferSize);
        byte[]? second = doubleBuffered ? ArrayPool<byte>.Shared.Rent(options.BufferSize) : null;
        try {
            byte[] current = first;
            byte[]? next = second;
            int count = await source.ReadAsync(current.AsMemory(0, options.BufferSize), cancellationToken)
                .ConfigureAwait(false);
            long total = 0;
            while (count != 0) {
                if (options.MaxBytes is { } limit && count > limit - total)
                    throw new IOException($"Transfer exceeds the configured limit of {limit} bytes.");

                ValueTask<int> readAhead = default;
                if (next is not null)
                    readAhead = source.ReadAsync(next.AsMemory(0, options.BufferSize), cancellationToken);

                try {
                    await destination.WriteAsync(current.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                } catch {
                    if (next is not null) {
                        try { await readAhead.ConfigureAwait(false); } catch { }
                    }
                    throw;
                }
                total = checked(total + count);
                if (next is null) {
                    count = await source.ReadAsync(current.AsMemory(0, options.BufferSize), cancellationToken)
                        .ConfigureAwait(false);
                } else {
                    count = await readAhead.ConfigureAwait(false);
                    (current, next) = (next, current);
                }
            }
            return total;
        } finally {
            ArrayPool<byte>.Shared.Return(first, clearArray: options.ClearBuffersOnReturn);
            if (second is not null)
                ArrayPool<byte>.Shared.Return(second, clearArray: options.ClearBuffersOnReturn);
        }
    }

    private static bool ShouldDoubleBuffer(RamDriveTransferOptions options, long? remainingLength) =>
        options.CopyMode == RamDriveCopyMode.DoubleBufferedFileStream ||
        (options.CopyMode == RamDriveCopyMode.Auto && remainingLength is { } length &&
         length >= options.DoubleBufferThresholdBytes);

    private static long? GetRemainingLength(Stream stream) {
        if (!stream.CanSeek)
            return null;
        try { return Math.Max(0, stream.Length - stream.Position); }
        catch (NotSupportedException) { return null; }
    }

    private static string GetSiblingTemporaryPath(string destination) => Path.Combine(
        Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.mrdtmp");

    private static void EnsureNotDriveRoot(string destination, RamDriveSession drive) {
        if (string.Equals(Path.TrimEndingDirectorySeparator(destination),
            Path.TrimEndingDirectorySeparator(drive.RootPath), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A drive root cannot be used as a file destination.", nameof(destination));
    }

    internal static async Task<long> CopyFileCoreAsync(string source, string destination, bool overwrite,
        RamDriveTransferOptions options, bool importing, CancellationToken cancellationToken) {
        var sourceInfo = new FileInfo(source);
        long expectedLength = sourceInfo.Length;
        long expectedWriteTime = sourceInfo.LastWriteTimeUtc.Ticks;
        var metadata = new FileMetadata(sourceInfo.CreationTimeUtc, sourceInfo.LastAccessTimeUtc,
            sourceInfo.LastWriteTimeUtc, sourceInfo.Attributes);
        string temporaryDestination = Path.Combine(Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.mrdtmp");
        try {
            RamDriveCopyMode mode = options.CopyMode switch {
                RamDriveCopyMode.Auto when expectedLength >= options.DoubleBufferThresholdBytes && !importing => RamDriveCopyMode.DoubleBufferedFileStream,
                RamDriveCopyMode.Auto => RamDriveCopyMode.FileStream,
                _ => options.CopyMode
            };

            long copied = mode == RamDriveCopyMode.RandomAccess
                ? await CopyRandomAccessAsync(source, temporaryDestination, expectedLength, options.BufferSize,
                    options.ClearBuffersOnReturn, cancellationToken).ConfigureAwait(false)
                : await CopyStreamAsync(source, temporaryDestination, options.BufferSize,
                    mode == RamDriveCopyMode.DoubleBufferedFileStream, options.ClearBuffersOnReturn,
                    cancellationToken).ConfigureAwait(false);

            var afterCopy = new FileInfo(source);
            if (copied != expectedLength || afterCopy.Length != expectedLength ||
                afterCopy.LastWriteTimeUtc.Ticks != expectedWriteTime)
                throw new IOException($"File changed while it was being copied: {source}");

            if (options.PreserveTimestamps) {
                File.SetCreationTimeUtc(temporaryDestination, metadata.CreationTimeUtc);
                File.SetLastWriteTimeUtc(temporaryDestination, metadata.LastWriteTimeUtc);
                File.SetLastAccessTimeUtc(temporaryDestination, metadata.LastAccessTimeUtc);
            }
            if (options.PreserveFileAttributes)
                File.SetAttributes(temporaryDestination, metadata.Attributes & ~FileAttributes.ReparsePoint);
            File.Move(temporaryDestination, destination, overwrite);
            return copied;
        } catch {
            try { File.Delete(temporaryDestination); } catch { }
            throw;
        }
    }

    private static async Task<long> CopyStreamAsync(string source, string destination, int bufferSize,
        bool doubleBuffered, bool clearBuffers, CancellationToken cancellationToken) {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.SequentialScan);

        long total = doubleBuffered
            ? await CopyDoubleBufferedAsync(input, output, bufferSize, clearBuffers, cancellationToken).ConfigureAwait(false)
            : await CopySingleBufferedAsync(input, output, bufferSize, clearBuffers, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        return total;
    }

    private static async Task<long> CopySingleBufferedAsync(Stream input, Stream output, int bufferSize,
        bool clearBuffers, CancellationToken cancellationToken) {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try {
            long total = 0;
            while (true) {
                int read = await input.ReadAsync(buffer.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    return total;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total = checked(total + read);
            }
        } finally {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: clearBuffers);
        }
    }

    private static async Task<long> CopyDoubleBufferedAsync(Stream input, Stream output, int bufferSize,
        bool clearBuffers, CancellationToken cancellationToken) {
        byte[] first = ArrayPool<byte>.Shared.Rent(bufferSize);
        byte[] second = ArrayPool<byte>.Shared.Rent(bufferSize);
        try {
            byte[] current = first;
            byte[] next = second;
            int count = await input.ReadAsync(current.AsMemory(0, bufferSize), cancellationToken).ConfigureAwait(false);
            long total = 0;
            while (count != 0) {
                ValueTask<int> readAhead = input.ReadAsync(next.AsMemory(0, bufferSize), cancellationToken);
                try {
                    await output.WriteAsync(current.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                } catch {
                    try { await readAhead.ConfigureAwait(false); } catch { }
                    throw;
                }
                total = checked(total + count);
                count = await readAhead.ConfigureAwait(false);
                (current, next) = (next, current);
            }
            return total;
        } finally {
            ArrayPool<byte>.Shared.Return(first, clearArray: clearBuffers);
            ArrayPool<byte>.Shared.Return(second, clearArray: clearBuffers);
        }
    }

    private static async Task<long> CopyRandomAccessAsync(string source, string destination, long expectedLength,
        int bufferSize, bool clearBuffers, CancellationToken cancellationToken) {
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try {
            long offset = 0;
            while (offset < expectedLength) {
                int requested = (int)Math.Min(bufferSize, expectedLength - offset);
                int read = await System.IO.RandomAccess.ReadAsync(input.SafeFileHandle,
                    buffer.AsMemory(0, requested), offset, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                await System.IO.RandomAccess.WriteAsync(output.SafeFileHandle,
                    buffer.AsMemory(0, read), offset, cancellationToken).ConfigureAwait(false);
                offset = checked(offset + read);
            }
            return offset;
        } finally {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: clearBuffers);
        }
    }

    private readonly record struct FileMetadata(DateTime CreationTimeUtc, DateTime LastAccessTimeUtc,
        DateTime LastWriteTimeUtc, FileAttributes Attributes);
}
