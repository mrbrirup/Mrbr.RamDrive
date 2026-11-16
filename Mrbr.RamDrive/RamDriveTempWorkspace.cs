namespace HighSpeedRamDrive;

/// <summary>Limits applied to one temporary workspace.</summary>
public sealed record TempWorkspaceOptions {
    public long MaxBytes { get; init; } = 512L * 1024 * 1024;
    public int MaxEntries { get; init; } = 10_000;
    public TimeSpan? ExpiresAfter { get; init; } = TimeSpan.FromHours(8);

    public TempWorkspaceOptions Validate() {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxEntries);
        if (ExpiresAfter is { } lifetime && lifetime <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ExpiresAfter));
        return this;
    }
}

/// <summary>A temporary directory whose descendant files are subject to byte and entry quotas.</summary>
public sealed class RamDriveTempWorkspace : IAsyncDisposable, IDisposable {
    private readonly RamDriveSession _session;
    private readonly string _virtualPath;
    private readonly RamDriveTempQuota _quota;
    private readonly CancellationTokenSource _expirationCancellation = new();
    private readonly SemaphoreSlim _cleanupGate = new(1, 1);
    private readonly Task _expirationTask;
    private int _disposed;

    public string Path { get; }
    public TempWorkspaceOptions Options { get; }
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ExpiresAt { get; }
    public bool IsExpired => ExpiresAt is { } expiration && DateTimeOffset.UtcNow >= expiration;
    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
    public long UsedBytes => _quota.UsedBytes;
    public int EntryCount => _quota.EntryCount;
    public Exception? LastCleanupError { get; private set; }

    /// <summary>Raised on background expiry cleanup failure. Implement UI or retry policy in the referencing app.</summary>
    public event Action<RamDriveTempWorkspace, Exception>? CleanupFailed;

    internal RamDriveTempWorkspace(RamDriveSession session, string path, string virtualPath,
        TempWorkspaceOptions options, RamDriveTempQuota quota) {
        _session = session;
        Path = path;
        _virtualPath = virtualPath;
        Options = options;
        _quota = quota;
        ExpiresAt = options.ExpiresAfter is { } lifetime ? CreatedAt + lifetime : null;
        _expirationTask = options.ExpiresAfter is { } expiry ? ExpireAsync(expiry) : Task.CompletedTask;
    }

    public string GetPath(string relativePath) {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (IsExpired)
            throw new InvalidOperationException("This temporary workspace has expired.");
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (System.IO.Path.IsPathRooted(relativePath))
            throw new ArgumentException("A workspace path must be relative.", nameof(relativePath));

        if (relativePath == ".")
            return Path;

        string fullPath = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
        string root = System.IO.Path.GetFullPath(Path) + System.IO.Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The path escapes its temporary workspace.", nameof(relativePath));
        return fullPath;
    }

    public string CreateDirectory(string relativePath) {
        _cleanupGate.Wait();
        try {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            if (IsExpired)
                throw new InvalidOperationException("This temporary workspace has expired.");
            string directory = GetPath(relativePath);
            Directory.CreateDirectory(directory);
            return directory;
        } finally {
            _cleanupGate.Release();
        }
    }

    public RamDriveTempFile CreateTempFile(string? prefix = null, string extension = ".tmp") {
        _cleanupGate.Wait();
        try {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        if (IsExpired)
            throw new InvalidOperationException("This temporary workspace has expired.");
        prefix ??= "tmp-";
        if (prefix.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
            prefix.Contains('/') || prefix.Contains('\\'))
            throw new ArgumentException("Prefix must be a filename fragment.", nameof(prefix));
        if (string.IsNullOrEmpty(extension) || extension[0] != '.' ||
            extension.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 ||
            extension.Contains('/') || extension.Contains('\\'))
            throw new ArgumentException("Extension must be a filename extension such as '.tmp'.", nameof(extension));

        for (int attempt = 0; attempt < 4; attempt++) {
            string filePath = System.IO.Path.Combine(Path, prefix + Guid.NewGuid().ToString("N") + extension);
            try {
                var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete,
                    bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
                return new RamDriveTempFile(filePath, stream);
            } catch (IOException) when (File.Exists(filePath)) {
                // Extremely unlikely GUID collision; choose another name.
            }
        }
        throw new IOException("Could not allocate a unique temporary file name.");
        } finally {
            _cleanupGate.Release();
        }
    }

    public async ValueTask DisposeAsync() {
        if (IsDisposed)
            return;
        _expirationCancellation.Cancel();
        try {
            await _expirationTask.ConfigureAwait(false);
        } catch (OperationCanceledException) {
        }
        await CleanupAsync().ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task ExpireAsync(TimeSpan lifetime) {
        try {
            await Task.Delay(lifetime, _expirationCancellation.Token).ConfigureAwait(false);
        } catch (OperationCanceledException) {
            return;
        }

        try {
            await CleanupAsync().ConfigureAwait(false);
        } catch (Exception ex) {
            LastCleanupError = ex;
            try { CleanupFailed?.Invoke(this, ex); } catch { }
        }
    }

    private async ValueTask CleanupAsync() {
        await _cleanupGate.WaitAsync().ConfigureAwait(false);
        try {
            if (IsDisposed)
                return;
            await Task.Run(() => {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }).ConfigureAwait(false);
            _session.FileSystem.UnregisterTempScope(_virtualPath);
            Interlocked.Exchange(ref _disposed, 1);
            _session.OnWorkspaceRemoved(this);
        } finally {
            _cleanupGate.Release();
        }
    }
}

/// <summary>A newly created temp file that is deleted when its lease is disposed.</summary>
public sealed class RamDriveTempFile : IAsyncDisposable, IDisposable {
    private readonly Stream _stream;
    private int _disposed;

    public string Path { get; }
    public Stream Stream => Volatile.Read(ref _disposed) == 0
        ? _stream
        : throw new ObjectDisposedException(nameof(RamDriveTempFile));

    internal RamDriveTempFile(string path, Stream stream) {
        Path = path;
        _stream = stream;
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _stream.DisposeAsync().ConfigureAwait(false);
        File.Delete(Path);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
