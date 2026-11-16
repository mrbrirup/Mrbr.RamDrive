using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace HighSpeedRamDrive;

public readonly record struct RamDriveSnapshotResult(string Path, long PayloadBytes, int EntryCount, RamDriveSnapshotPurpose Purpose);

public sealed record RamDriveSnapshotLoadOptions {
    public string? StagingDirectory { get; init; }
    public int MaxEntries { get; init; } = 1_000_000;
    public long? MaxPayloadBytes { get; init; }

    internal RamDriveSnapshotLoadOptions Validate() {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEntries);
        if (MaxPayloadBytes is <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxPayloadBytes));
        return this;
    }
}

/// <summary>Creates and restores versioned, file-level Mrbr snapshot archives.</summary>
public static class RamDriveSnapshot {
    public const int CurrentFormatVersion = 1;
    public const string DefaultExtension = ".mrd";
    private const string ManifestEntryName = "mrbr/manifest.json";
    private const int CopyBufferSize = 128 * 1024;
    private const int MaximumManifestBytes = 64 * 1024 * 1024;

    public static async Task<RamDriveSnapshotResult> SaveAsync(
        RamDriveSession drive,
        string destinationPath,
        RamDriveSnapshotOptions? options = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        options = (options ?? new RamDriveSnapshotOptions()).Validate();
        string destination = Path.GetFullPath(destinationPath);
        if (IsWithin(destination, drive.RootPath))
            throw new ArgumentException("A snapshot must be saved outside the RAM drive being captured.", nameof(destinationPath));

        string? destinationDirectory = Path.GetDirectoryName(destination);
        if (string.IsNullOrEmpty(destinationDirectory))
            throw new ArgumentException("Snapshot destination must include a parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);
        string temporaryPath = Path.Combine(destinationDirectory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        IAsyncDisposable? quiescence = null;
        try {
            if (options.AcquireQuiescenceAsync is not null)
                quiescence = await options.AcquireQuiescenceAsync(cancellationToken).ConfigureAwait(false);

            List<SnapshotEntry> entries = EnumerateEntries(drive, options, cancellationToken);
            if (entries.Count > options.MaxEntries)
                throw new IOException($"Snapshot contains {entries.Count} entries; the configured limit is {options.MaxEntries}.");

            long totalPayload = 0;
            int dataIndex = 0;
            foreach (SnapshotEntry entry in entries) {
                if (entry.IsDirectory)
                    continue;
                entry.DataEntry = DataEntryName(dataIndex++);
                var sourceInfo = new FileInfo(entry.SourcePath!);
                entry.Length = sourceInfo.Length;
                entry.SourceLastWriteTimeUtcTicks = sourceInfo.LastWriteTimeUtc.Ticks;
                if (entry.Length < 0 || (options.MaxPayloadBytes is { } max && entry.Length > max - totalPayload))
                    throw new IOException("Snapshot payload exceeds the configured maximum.");
                totalPayload = checked(totalPayload + entry.Length);
            }

            var manifest = new SnapshotManifest {
                FormatVersion = CurrentFormatVersion,
                DriveName = drive.Options.Name,
                VolumeLabel = drive.Options.VolumeLabel,
                CapacityBytes = drive.Options.CapacityBytes,
                ChunkSize = drive.Options.ChunkSize,
                CreatedUtc = DateTimeOffset.UtcNow,
                Purpose = options.Purpose,
                Metadata = options.Metadata,
                PayloadBytes = totalPayload,
                Entries = entries
            };

            var compression = options.Compression switch {
                RamDriveSnapshotCompression.None => CompressionLevel.NoCompression,
                RamDriveSnapshotCompression.Fastest => CompressionLevel.Fastest,
                RamDriveSnapshotCompression.Optimal => CompressionLevel.Optimal,
                _ => throw new ArgumentOutOfRangeException(nameof(options))
            };

            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan)) {
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true)) {
                    foreach (SnapshotEntry entry in entries) {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (entry.IsDirectory)
                            continue;
                        ZipArchiveEntry payload = archive.CreateEntry(entry.DataEntry!, compression);
                        await using var source = new FileStream(entry.SourcePath!, FileMode.Open, FileAccess.Read,
                            FileShare.Read, CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                        await using Stream target = payload.Open();
                        entry.Sha256 = await CopyAndHashAsync(source, target, cancellationToken).ConfigureAwait(false);
                        var afterCopy = new FileInfo(entry.SourcePath!);
                        if (afterCopy.Length != entry.Length ||
                            afterCopy.LastWriteTimeUtc.Ticks != entry.SourceLastWriteTimeUtcTicks)
                            throw new IOException($"File changed while it was being saved: {entry.RelativePath}. Pause writers or provide a quiescence callback, then retry.");
                    }

                    ZipArchiveEntry metadataEntry = archive.CreateEntry(ManifestEntryName, CompressionLevel.Fastest);
                    await using Stream manifestStream = metadataEntry.Open();
                    await JsonSerializer.SerializeAsync(manifestStream, manifest, SnapshotJsonOptions,
                        cancellationToken).ConfigureAwait(false);
                }
                output.Flush(flushToDisk: true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
            return new RamDriveSnapshotResult(destination, totalPayload, entries.Count, options.Purpose);
        } catch {
            try { File.Delete(temporaryPath); } catch { }
            throw;
        } finally {
            if (quiescence is not null)
                await quiescence.DisposeAsync().ConfigureAwait(false);
        }
    }

    public static async Task<RamDriveSession> LoadAsync(
        string snapshotPath,
        RamDriveOptions? driveOptions = null,
        RamDriveSnapshotLoadOptions? loadOptions = null,
        CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotPath);
        loadOptions = (loadOptions ?? new RamDriveSnapshotLoadOptions()).Validate();
        string snapshot = Path.GetFullPath(snapshotPath);
        string stagingParent = Path.GetFullPath(loadOptions.StagingDirectory ?? Path.GetTempPath());
        Directory.CreateDirectory(stagingParent);
        string staging = Path.Combine(stagingParent, $"mrbr-restore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        RamDriveSession? drive = null;
        try {
            SnapshotManifest manifest = await ExtractAndValidateAsync(snapshot, staging, loadOptions, driveOptions, cancellationToken)
                .ConfigureAwait(false);
            RamDriveOptions effectiveOptions = (driveOptions ?? new RamDriveOptions {
                Name = manifest.DriveName,
                CapacityBytes = manifest.CapacityBytes,
                ChunkSize = manifest.ChunkSize,
                VolumeLabel = manifest.VolumeLabel
            }).Validate();
            long requiredStorage = 0;
            foreach (SnapshotEntry entry in manifest.Entries) {
                if (entry.IsDirectory || entry.Length == 0)
                    continue;
                long chunks = checked(((entry.Length - 1) / effectiveOptions.ChunkSize) + 1);
                requiredStorage = checked(requiredStorage + checked(chunks * effectiveOptions.ChunkSize));
            }
            if (requiredStorage > effectiveOptions.CapacityBytes)
                throw new IOException($"Snapshot requires {requiredStorage} bytes of chunk storage, which exceeds the configured drive capacity ({effectiveOptions.CapacityBytes} bytes).");

            drive = RamDriveSession.Mount(effectiveOptions);
            var transferOptions = new RamDriveTransferOptions {
                ConflictPolicy = RamDriveConflictPolicy.Fail,
                PreserveFileAttributes = false,
                PreserveTimestamps = false,
                MaxBytes = loadOptions.MaxPayloadBytes ?? effectiveOptions.CapacityBytes
            };
            await RamDriveDirectoryTransfer.ImportDirectoryAsync(drive, staging, transferOptions, cancellationToken)
                .ConfigureAwait(false);
            ApplyFileMetadata(drive.RootPath, manifest);
            ApplyDirectoryMetadata(drive.RootPath, manifest);
            return drive;
        } catch {
            if (drive is not null) {
                try { await drive.AbortMountAsync().ConfigureAwait(false); } catch { }
            }
            throw;
        } finally {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    private static List<SnapshotEntry> EnumerateEntries(RamDriveSession drive, RamDriveSnapshotOptions options,
        CancellationToken cancellationToken) {
        var entries = new List<SnapshotEntry>();
        var pending = new Stack<string>();
        pending.Push(drive.RootPath);
        while (pending.TryPop(out string? directory)) {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string path in Directory.EnumerateFileSystemEntries(directory)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!options.IncludeTempWorkspaces && IsWithin(path, drive.TempNamespacePath))
                    continue;

                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    continue;
                string relative = Path.GetRelativePath(drive.RootPath, path).Replace(Path.DirectorySeparatorChar, '/');
                bool isDirectory = (attributes & FileAttributes.Directory) != 0;
                var info = isDirectory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                var entry = new SnapshotEntry {
                    RelativePath = relative,
                    IsDirectory = isDirectory,
                    Attributes = (options.Metadata & RamDriveSnapshotMetadata.FileAttributes) != 0 ? (int)attributes : null,
                    CreationTimeUtcTicks = (options.Metadata & RamDriveSnapshotMetadata.Timestamps) != 0 ? info.CreationTimeUtc.Ticks : null,
                    LastAccessTimeUtcTicks = (options.Metadata & RamDriveSnapshotMetadata.Timestamps) != 0 ? info.LastAccessTimeUtc.Ticks : null,
                    LastWriteTimeUtcTicks = (options.Metadata & RamDriveSnapshotMetadata.Timestamps) != 0 ? info.LastWriteTimeUtc.Ticks : null,
                    SourceLastWriteTimeUtcTicks = !isDirectory && info is FileInfo fileInfo ? fileInfo.LastWriteTimeUtc.Ticks : null,
                    SourcePath = isDirectory ? null : path
                };
                entries.Add(entry);
                if (isDirectory)
                    pending.Push(path);
            }
        }
        return entries.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static async Task<SnapshotManifest> ExtractAndValidateAsync(string snapshotPath, string staging,
        RamDriveSnapshotLoadOptions options, RamDriveOptions? driveOptions, CancellationToken cancellationToken) {
        await using var input = new FileStream(snapshotPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry manifestEntry = archive.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("This file does not contain an Mrbr.RamDrive snapshot manifest.");
        if (manifestEntry.Length > MaximumManifestBytes)
            throw new InvalidDataException("Snapshot manifest exceeds the maximum supported size.");

        SnapshotManifest manifest;
        await using (Stream manifestStream = manifestEntry.Open()) {
            manifest = await JsonSerializer.DeserializeAsync<SnapshotManifest>(manifestStream, SnapshotJsonOptions,
                cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Snapshot manifest is empty.");
        }
        if (manifest.FormatVersion != CurrentFormatVersion)
            throw new InvalidDataException($"Snapshot version {manifest.FormatVersion} is not supported.");
        if (string.IsNullOrWhiteSpace(manifest.DriveName) || string.IsNullOrWhiteSpace(manifest.VolumeLabel) ||
            manifest.Entries is null)
            throw new InvalidDataException("Snapshot drive metadata or entry list is missing.");
        if (manifest.CapacityBytes <= 0 || manifest.ChunkSize < 4096 || !int.IsPow2(manifest.ChunkSize))
            throw new InvalidDataException("Snapshot drive configuration is invalid.");
        const RamDriveSnapshotMetadata supportedMetadata = RamDriveSnapshotMetadata.FileAttributes | RamDriveSnapshotMetadata.Timestamps;
        if (!Enum.IsDefined(manifest.Purpose) || manifest.PayloadBytes < 0 || (manifest.Metadata & ~supportedMetadata) != 0)
            throw new InvalidDataException("Snapshot metadata is invalid.");
        if (manifest.Entries.Count > options.MaxEntries)
            throw new InvalidDataException("Snapshot entry count exceeds the configured limit.");
        if (options.MaxPayloadBytes is { } maxBytes && manifest.PayloadBytes > maxBytes)
            throw new InvalidDataException("Snapshot payload exceeds the configured limit.");

        RamDriveOptions effectiveOptions = (driveOptions ?? new RamDriveOptions {
            Name = manifest.DriveName,
            CapacityBytes = manifest.CapacityBytes,
            ChunkSize = manifest.ChunkSize,
            VolumeLabel = manifest.VolumeLabel
        }).Validate();
        if (manifest.PayloadBytes > effectiveOptions.CapacityBytes)
            throw new InvalidDataException("Snapshot payload exceeds the configured drive capacity.");
        long requiredStorage = 0;
        foreach (SnapshotEntry entry in manifest.Entries) {
            if (entry is null)
                throw new InvalidDataException("Snapshot contains a null entry.");
            if (entry.IsDirectory)
                continue;
            if (entry.Length < 0)
                throw new InvalidDataException($"Snapshot file '{entry.RelativePath}' has an invalid length.");
            if (entry.Length == 0)
                continue;
            long chunks = checked(((entry.Length - 1) / effectiveOptions.ChunkSize) + 1);
            requiredStorage = checked(requiredStorage + checked(chunks * effectiveOptions.ChunkSize));
        }
        if (requiredStorage > effectiveOptions.CapacityBytes)
            throw new InvalidDataException("Snapshot requires more chunk storage than the configured drive capacity.");

        long totalPayload = 0;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dataNames = new HashSet<string>(StringComparer.Ordinal);
        int fileIndex = 0;
        foreach (SnapshotEntry entry in manifest.Entries) {
            if (entry is null)
                throw new InvalidDataException("Snapshot contains a null entry.");
            cancellationToken.ThrowIfCancellationRequested();
            string relativePath = ValidateRelativePath(entry.RelativePath);
            if (!paths.Add(relativePath))
                throw new InvalidDataException($"Snapshot has duplicate path '{relativePath}'.");
            string targetPath = GetSafePath(staging, relativePath);

            if (entry.IsDirectory) {
                if (entry.Length != 0 || entry.DataEntry is not null || entry.Sha256 is not null)
                    throw new InvalidDataException($"Snapshot directory '{relativePath}' contains file payload metadata.");
                Directory.CreateDirectory(targetPath);
                continue;
            }

            if (entry.Length < 0)
                throw new InvalidDataException($"Snapshot file '{relativePath}' has an invalid length.");
            totalPayload = checked(totalPayload + entry.Length);
            if (totalPayload > manifest.PayloadBytes || (options.MaxPayloadBytes is { } limit && totalPayload > limit))
                throw new InvalidDataException("Snapshot payload exceeds its declared or configured limit.");
            string expectedDataName = DataEntryName(fileIndex++);
            if (!string.Equals(entry.DataEntry, expectedDataName, StringComparison.Ordinal) || !dataNames.Add(expectedDataName))
                throw new InvalidDataException($"Snapshot payload reference for '{relativePath}' is invalid.");
            if (entry.Sha256 is null || entry.Sha256.Length != 64)
                throw new InvalidDataException($"Snapshot checksum for '{relativePath}' is invalid.");
            ZipArchiveEntry data = archive.GetEntry(expectedDataName)
                ?? throw new InvalidDataException($"Snapshot payload for '{relativePath}' is missing.");
            if (data.Length != entry.Length)
                throw new InvalidDataException($"Snapshot payload length for '{relativePath}' does not match its manifest.");

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await ExtractFileAsync(data, targetPath, entry, cancellationToken).ConfigureAwait(false);
        }
        if (totalPayload != manifest.PayloadBytes)
            throw new InvalidDataException("Snapshot payload total does not match its manifest.");
        if (archive.Entries.Count != fileIndex + 1)
            throw new InvalidDataException("Snapshot contains unexpected or duplicate archive entries.");
        var archiveNames = new HashSet<string>(StringComparer.Ordinal);
        if (archive.Entries.Any(entry => !archiveNames.Add(entry.FullName)) ||
            !archiveNames.Contains(ManifestEntryName))
            throw new InvalidDataException("Snapshot contains duplicate or missing archive entry names.");
        for (int i = 0; i < fileIndex; i++) {
            if (!archiveNames.Contains(DataEntryName(i)))
                throw new InvalidDataException("Snapshot contains unexpected or missing payload entries.");
        }
        return manifest;
    }

    private static async Task ExtractFileAsync(ZipArchiveEntry data, string targetPath, SnapshotEntry entry,
        CancellationToken cancellationToken) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferSize];
        long copied = 0;
        await using Stream source = data.Open();
        await using var target = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true) {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            copied = checked(copied + read);
            if (copied > entry.Length)
                throw new InvalidDataException($"Snapshot file '{entry.RelativePath}' expands beyond its declared length.");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        if (copied != entry.Length)
            throw new InvalidDataException($"Snapshot file '{entry.RelativePath}' is truncated.");
        byte[] expectedHash;
        try { expectedHash = Convert.FromHexString(entry.Sha256!); }
        catch (FormatException ex) { throw new InvalidDataException($"Snapshot checksum for '{entry.RelativePath}' is invalid.", ex); }
        if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expectedHash))
            throw new InvalidDataException($"Snapshot checksum failed for '{entry.RelativePath}'.");

    }

    private static async Task<string> CopyAndHashAsync(Stream source, Stream destination, CancellationToken cancellationToken) {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[CopyBufferSize];
        while (true) {
            int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void ApplyFileMetadata(string driveRoot, SnapshotManifest manifest) {
        foreach (SnapshotEntry entry in manifest.Entries.Where(entry => !entry.IsDirectory)) {
            string path = GetSafePath(driveRoot, entry.RelativePath);
            if (!File.Exists(path))
                continue;
            if (entry.CreationTimeUtcTicks is { } creation)
                File.SetCreationTimeUtc(path, new DateTime(creation, DateTimeKind.Utc));
            if (entry.LastWriteTimeUtcTicks is { } write)
                File.SetLastWriteTimeUtc(path, new DateTime(write, DateTimeKind.Utc));
            if (entry.LastAccessTimeUtcTicks is { } access)
                File.SetLastAccessTimeUtc(path, new DateTime(access, DateTimeKind.Utc));
            if (entry.Attributes is { } attributes)
                File.SetAttributes(path, (FileAttributes)attributes & ~FileAttributes.ReparsePoint);
        }
    }

    private static void ApplyDirectoryMetadata(string driveRoot, SnapshotManifest manifest) {
        if ((manifest.Metadata & (RamDriveSnapshotMetadata.FileAttributes | RamDriveSnapshotMetadata.Timestamps)) == 0)
            return;
        foreach (SnapshotEntry entry in manifest.Entries.Where(entry => entry.IsDirectory)
            .OrderByDescending(entry => entry.RelativePath.Count(ch => ch == '/'))) {
            string path = GetSafePath(driveRoot, entry.RelativePath);
            if (!Directory.Exists(path))
                continue;
            if (entry.CreationTimeUtcTicks is { } creation)
                Directory.SetCreationTimeUtc(path, new DateTime(creation, DateTimeKind.Utc));
            if (entry.LastWriteTimeUtcTicks is { } write)
                Directory.SetLastWriteTimeUtc(path, new DateTime(write, DateTimeKind.Utc));
            if (entry.LastAccessTimeUtcTicks is { } access)
                Directory.SetLastAccessTimeUtc(path, new DateTime(access, DateTimeKind.Utc));
            if (entry.Attributes is { } attributes)
                File.SetAttributes(path, (FileAttributes)attributes & ~FileAttributes.ReparsePoint);
        }
    }

    private static string ValidateRelativePath(string path) {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':'))
            throw new InvalidDataException("Snapshot contains an invalid relative path.");
        string normalized = path.Replace('\\', '/');
        string[] segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException($"Snapshot path '{path}' is not safe.");
        return string.Join('/', segments);
    }

    private static string GetSafePath(string root, string relativePath) {
        string path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string rootPrefix = Path.EndsInDirectorySeparator(Path.GetFullPath(root)) ? Path.GetFullPath(root) : Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Snapshot path escapes the selected root.");
        return path;
    }

    private static bool IsWithin(string candidate, string root) {
        string fullCandidate = Path.GetFullPath(candidate);
        string fullRoot = Path.GetFullPath(root);
        if (fullCandidate.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
            return true;
        string prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string DataEntryName(int index) => $"mrbr/payload/{index:D8}";

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new() {
        PropertyNameCaseInsensitive = false,
        MaxDepth = 32
    };

    private sealed class SnapshotManifest {
        public SnapshotManifest() { }
        public int FormatVersion { get; set; }
        public string DriveName { get; set; } = "";
        public string VolumeLabel { get; set; } = "";
        public long CapacityBytes { get; set; }
        public int ChunkSize { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public RamDriveSnapshotPurpose Purpose { get; set; }
        public RamDriveSnapshotMetadata Metadata { get; set; }
        public long PayloadBytes { get; set; }
        public List<SnapshotEntry> Entries { get; set; } = [];
    }

    private sealed class SnapshotEntry {
        public SnapshotEntry() { }
        public string RelativePath { get; set; } = "";
        public bool IsDirectory { get; set; }
        public string? DataEntry { get; set; }
        public long Length { get; set; }
        public string? Sha256 { get; set; }
        public int? Attributes { get; set; }
        public long? CreationTimeUtcTicks { get; set; }
        public long? LastAccessTimeUtcTicks { get; set; }
        public long? LastWriteTimeUtcTicks { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public long? SourceLastWriteTimeUtcTicks { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public string? SourcePath { get; set; }
    }
}
